package main

import (
	"os/exec"
	"strings"
	"syscall"
)

// desktopLaunchCmd 构造拉起 Desktop 的命令。
// 商店版必须走 AUMID（shell:AppsFolder），直接 exec WindowsApps 下的 exe 没有包身份。
func desktopLaunchCmd(exePath, aumid string) *exec.Cmd {
	aumid = strings.TrimSpace(aumid)
	if aumid != "" {
		return exec.Command("explorer.exe", "shell:AppsFolder\\"+aumid)
	}
	cmd := exec.Command(exePath)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd
}
