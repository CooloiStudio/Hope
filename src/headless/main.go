// Command hope-headless 是 Hope 的无窗口后台核心：
// 负责任务计时、墙钟实时进度计算、配置持久化，并通过命名管道向 Desktop 广播顶栏状态。
package main

import (
	"context"
	"flag"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strings"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"

	"hope/headless/config"
	"hope/headless/engine"
	"hope/headless/ipc"
)

func main() {
	debug := flag.Bool("debug", false, "输出日志到控制台并启用 debug 级别")
	desktop := flag.String("desktop", "", "可选：hope-desktop.exe 路径，提供后将监视并在其退出时重新拉起")
	flag.Parse()

	log := newLogger(*debug)

	if !acquireSingleInstance() {
		log.Info("another instance is running, exiting")
		return
	}

	store, err := config.Load()
	if err != nil {
		log.Error("load config failed", "err", err)
		os.Exit(1)
	}
	log.Info("hope-headless starting", "dataDir", config.Dir())

	ctx, cancel := context.WithCancel(context.Background())
	var quitting atomic.Bool

	eng := engine.New(store, log)
	eng.OnQuit = func() {
		log.Info("quit requested via ipc")
		quitting.Store(true)
		time.Sleep(200 * time.Millisecond) // 给 quitAck 留出回写时间
		cancel()
	}

	srv := ipc.NewServer(log, eng.HandleCommand)
	go func() {
		if err := srv.Listen(); err != nil {
			log.Error("ipc listen failed", "err", err)
			cancel()
		}
	}()

	if *desktop != "" {
		go superviseDesktop(ctx, log, *desktop, &quitting)
	}

	// 处理 Ctrl+C / 终止信号，便于调试期优雅退出。
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-sig
		quitting.Store(true)
		cancel()
	}()

	runBroadcastLoop(ctx, store, eng, srv, log)
	log.Info("hope-headless stopped")
}

// runBroadcastLoop 按设置的刷新间隔计算并广播顶栏状态。
func runBroadcastLoop(ctx context.Context, store *config.Store, eng *engine.Engine, srv *ipc.Server, log *slog.Logger) {
	interval := func() time.Duration {
		s, _ := store.Snapshot()
		if s.RefreshSec <= 0 {
			return time.Second
		}
		return time.Duration(s.RefreshSec) * time.Second
	}

	t := time.NewTimer(0)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			srv.Broadcast(eng.ComputeState())
			t.Reset(interval())
		}
	}
}

// superviseDesktop 轮询监视 Desktop 进程：仅当其不在运行时才拉起，异常退出时补拉（文档 §3.3 互保）。
// 注意：通常是 Desktop 先拉起本核心并传入 --desktop，故必须先判断 Desktop 是否已在运行，
// 否则会与已存在的 Desktop 反复重复拉起（后者撞单实例互斥秒退）形成 fork 死循环。
func superviseDesktop(ctx context.Context, log *slog.Logger, path string, quitting *atomic.Bool) {
	exeName := filepath.Base(path)
	for {
		if ctx.Err() != nil || quitting.Load() {
			return
		}
		if !isProcessRunning(exeName) {
			cmd := exec.Command(path)
			cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
			if err := cmd.Start(); err != nil {
				log.Error("start desktop failed", "err", err)
			} else {
				log.Info("desktop launched", "pid", cmd.Process.Pid, "exe", exeName)
			}
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
	}
}

// isProcessRunning 报告是否存在指定可执行名（如 hope-desktop.exe）的进程。
func isProcessRunning(exeName string) bool {
	snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return false
	}
	defer windows.CloseHandle(snap)

	var entry windows.ProcessEntry32
	entry.Size = uint32(unsafe.Sizeof(entry))
	target := strings.ToLower(exeName)
	for err = windows.Process32First(snap, &entry); err == nil; err = windows.Process32Next(snap, &entry) {
		if strings.ToLower(windows.UTF16ToString(entry.ExeFile[:])) == target {
			return true
		}
	}
	return false
}

// newLogger 构建写入文件（始终）与控制台（--debug）的结构化日志。
func newLogger(debug bool) *slog.Logger {
	level := slog.LevelInfo
	if debug {
		level = slog.LevelDebug
	}

	var writers []io.Writer
	logPath := filepath.Join(config.Dir(), "logs", "hope-headless.log")
	_ = os.MkdirAll(filepath.Dir(logPath), 0o755)
	if f, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); err == nil {
		writers = append(writers, f)
	}
	if debug {
		writers = append(writers, os.Stdout)
	}
	if len(writers) == 0 {
		writers = append(writers, io.Discard)
	}

	return slog.New(slog.NewJSONHandler(io.MultiWriter(writers...), &slog.HandlerOptions{Level: level}))
}
