package main

import (
	"testing"
)

func TestDesktopLaunchCmd_PrefersAumid(t *testing.T) {
	aumid := "Cooloi.Hope_c6tv1djd4qth2!HopeDesktop"
	cmd := desktopLaunchCmd(`C:\Program Files\WindowsApps\Cooloi.Hope\hope-desktop.exe`, aumid)
	if got := cmd.Args; len(got) != 2 || got[0] != "explorer.exe" || got[1] != "shell:AppsFolder\\"+aumid {
		t.Fatalf("args = %#v", cmd.Args)
	}
}

func TestDesktopLaunchCmd_SideloadUsesExePath(t *testing.T) {
	exe := `C:\Program Files\Hope\hope-desktop.exe`
	cmd := desktopLaunchCmd(exe, "")
	if cmd.Path != exe && (len(cmd.Args) == 0 || cmd.Args[0] != exe) {
		t.Fatalf("path=%q args=%#v", cmd.Path, cmd.Args)
	}
}
