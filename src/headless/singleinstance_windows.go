package main

import (
	"golang.org/x/sys/windows"
)

// 持有互斥量句柄至进程退出，确保单实例语义（文档 §4.1 / §5.1）。
var instanceMutex windows.Handle

// acquireSingleInstance 创建全局命名互斥量；若已存在则返回 false。
func acquireSingleInstance() bool {
	name, err := windows.UTF16PtrFromString(`Global\HopeHeadless`)
	if err != nil {
		return true
	}
	h, err := windows.CreateMutex(nil, false, name)
	if err != nil {
		// ERROR_ALREADY_EXISTS：已有实例在运行。
		if err == windows.ERROR_ALREADY_EXISTS {
			if h != 0 {
				_ = windows.CloseHandle(h)
			}
			return false
		}
		// 其他错误下不阻塞启动。
		return true
	}
	instanceMutex = h
	return true
}
