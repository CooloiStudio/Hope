// Package ipc 实现基于 Windows 命名管道的双向 JSON Lines 传输。
// 服务端每秒向所有订阅者广播顶栏状态，并接收来自 Desktop 的控制命令（文档 §5.2）。
package ipc

import (
	"bufio"
	"encoding/json"
	"log/slog"
	"net"
	"sync"

	winio "github.com/Microsoft/go-winio"

	"hope/headless/task"
)

// PipeName 为 Hope 进度广播管道。
const PipeName = `\\.\pipe\Hope\progress`

// State 为服务端 → 客户端的广播结构。
type State struct {
	Version  int            `json:"version"`
	Visible  bool           `json:"visible"`
	State    string         `json:"state"` // idle / running / expired
	Segments []task.Segment `json:"segments"`
	Expired  []ExpiredEvent `json:"expired,omitempty"`
}

// ExpiredEvent 在任务到达截止时刻时随广播一次性下发，供 Desktop 执行一次性提醒（如 notify）。
// blink / celebrate 等持续表现由广播 Segment 的 expired / behaviors 字段驱动。
type ExpiredEvent struct {
	TaskID    string   `json:"taskId"`
	Name      string   `json:"name"`
	Behaviors []string `json:"behaviors"` // blink / celebrate / notify（keep/hide 已废弃）
}

// Command 为客户端 → 服务端的控制命令。
type Command struct {
	Action    string          `json:"action"`
	Task      *task.Task      `json:"task,omitempty"`
	TaskID    string          `json:"taskId,omitempty"`
	Settings  json.RawMessage `json:"settings,omitempty"`
	RequestID string          `json:"requestId,omitempty"`
}

// CommandHandler 处理一条命令，可返回需要单播给该客户端的响应（如 listTasks）。
type CommandHandler func(cmd Command) any

// clientConn 单条管道连接：写操作须持 mu，避免 serveConn 回包与 Broadcast 并发写同一 bufio.Writer。
type clientConn struct {
	w  *bufio.Writer
	mu sync.Mutex
}

// Server 管理命名管道监听与客户端连接。
type Server struct {
	log     *slog.Logger
	handler CommandHandler

	mu      sync.Mutex
	clients map[net.Conn]*clientConn
	last    *State
}

// NewServer 创建服务端。
func NewServer(log *slog.Logger, handler CommandHandler) *Server {
	return &Server{
		log:     log,
		handler: handler,
		clients: make(map[net.Conn]*clientConn),
	}
}

// Listen 启动命名管道监听（阻塞，通常在 goroutine 中调用）。
// 配置安全描述符允许本机用户读写；Phase 2 沙盒插件如需访问再扩展 ACL。
func (s *Server) Listen() error {
	cfg := &winio.PipeConfig{
		// Phase 1：订阅方（Desktop）与服务端同为当前用户会话，使用默认安全描述符即可。
		// 仅授予交互用户完全访问（DACL，无 owner 子句，避免非提权进程赋 owner 失败）。
		// Phase 2 若引入 UWP 沙盒插件，再追加 ALL APPLICATION PACKAGES (AC)。
		SecurityDescriptor: "D:(A;;GA;;;IU)",
		MessageMode:        false,
	}
	l, err := winio.ListenPipe(PipeName, cfg)
	if err != nil {
		return err
	}
	s.log.Info("ipc listening", "pipe", PipeName)
	for {
		conn, err := l.Accept()
		if err != nil {
			s.log.Error("pipe accept failed", "err", err)
			continue
		}
		go s.serveConn(conn)
	}
}

func (s *Server) serveConn(conn net.Conn) {
	cc := &clientConn{w: bufio.NewWriter(conn)}
	s.mu.Lock()
	s.clients[conn] = cc
	last := s.last
	s.mu.Unlock()
	s.log.Info("client connected")

	// 新连接立即推送最近一次状态，避免空窗。
	if last != nil {
		s.writeTo(conn, cc, last)
	}

	defer func() {
		s.mu.Lock()
		delete(s.clients, conn)
		s.mu.Unlock()
		_ = conn.Close()
		s.log.Info("client disconnected")
	}()

	sc := bufio.NewScanner(conn)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for sc.Scan() {
		line := sc.Bytes()
		if len(line) == 0 {
			continue
		}
		var cmd Command
		if err := json.Unmarshal(line, &cmd); err != nil {
			s.log.Warn("bad command", "err", err)
			continue
		}
		if s.handler == nil {
			continue
		}
		if resp := s.handler(cmd); resp != nil {
			s.writeTo(conn, cc, resp)
		}
	}
}

// Broadcast 向所有连接发送状态，并缓存为最近状态。
func (s *Server) Broadcast(st State) {
	s.mu.Lock()
	s.last = &st
	conns := make([]net.Conn, 0, len(s.clients))
	for c := range s.clients {
		conns = append(conns, c)
	}
	s.mu.Unlock()
	for _, c := range conns {
		s.mu.Lock()
		cc := s.clients[c]
		s.mu.Unlock()
		if cc != nil {
			s.writeTo(c, cc, st)
		}
	}
}

func (s *Server) writeTo(conn net.Conn, cc *clientConn, v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	b = append(b, '\n')
	cc.mu.Lock()
	defer cc.mu.Unlock()
	if _, err := cc.w.Write(b); err != nil {
		s.log.Warn("write failed", "err", err)
		return
	}
	_ = cc.w.Flush()
}
