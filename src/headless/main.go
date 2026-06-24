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
	"sync/atomic"
	"syscall"
	"time"

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

// superviseDesktop 启动并监视 Desktop 进程；其异常退出时重新拉起（文档 §3.3 互保）。
func superviseDesktop(ctx context.Context, log *slog.Logger, path string, quitting *atomic.Bool) {
	for {
		if ctx.Err() != nil || quitting.Load() {
			return
		}
		cmd := exec.Command(path)
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		if err := cmd.Start(); err != nil {
			log.Error("start desktop failed", "err", err)
			time.Sleep(3 * time.Second)
			continue
		}
		log.Info("desktop started", "pid", cmd.Process.Pid)
		_ = cmd.Wait()
		if quitting.Load() || ctx.Err() != nil {
			return
		}
		log.Warn("desktop exited unexpectedly, relaunching")
		time.Sleep(2 * time.Second)
	}
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
