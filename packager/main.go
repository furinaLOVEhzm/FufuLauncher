// 可爱的芙芙 - Go 启动打包器
// 版权:Copyright © 可爱的芙芙
// 功能:作为启动器入口,查找并启动主 FufuLauncher.exe(WPF 主程序),
//       找不到时给出友好提示。
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"unsafe"
)

// 版本元信息(运行时显示,同时通过 versioninfo.syso 写入文件属性)
const (
	appName    = "可爱的芙芙 Minecraft启动器"
	appVersion = "1.8.1.2"
	copyright  = "Copyright © 可爱的芙芙"
	company    = "可爱的芙芙"
	fileDesc   = "可爱的芙芙 MC-Java版启动器"
)

// findMainExe 查找主程序 FufuLauncher.exe(跳过自身)
func findMainExe() (string, error) {
	exePath, err := os.Executable()
	if err != nil {
		return "", err
	}
	dir := filepath.Dir(exePath)
	exeName := "FufuLauncher.exe"

	candidates := []string{
		// 当前目录(自包含单文件主程序直接放在 Start/ 根)
		filepath.Join(dir, exeName),
		filepath.Join(dir, "FufuLauncher", exeName),
		filepath.Join(dir, "APP", "MCGAME", ".NET8", exeName),
		filepath.Join(dir, "..", "src", "FufuLauncher", "bin", "Release", "net8.0-windows", "win-x64", exeName),
		filepath.Join(dir, "..", "src", "FufuLauncher", "bin", "Debug", "net8.0-windows", "win-x64", exeName),
	}

	selfAbs, _ := filepath.Abs(exePath)
	for _, p := range candidates {
		abs, _ := filepath.Abs(p)
		// 跳过自身(避免 Go 启动器与主程序同名时死循环)
		if abs == selfAbs {
			continue
		}
		if _, err := os.Stat(abs); err == nil {
			return abs, nil
		}
	}
	return "", fmt.Errorf("未找到主程序 %s", exeName)
}

// messageBox 弹出 Windows 消息框(MB_ICONINFORMATION = 0x40)
func messageBox(text, caption string) {
	user32 := syscall.NewLazyDLL("user32.dll")
	mbox := user32.NewProc("MessageBoxW")
	t, _ := syscall.UTF16PtrFromString(text)
	c, _ := syscall.UTF16PtrFromString(caption)
	mbox.Call(0, uintptr(unsafe.Pointer(t)), uintptr(unsafe.Pointer(c)), 0x40)
}

func main() {
	exe, err := findMainExe()
	if err != nil {
		messageBox(
			"未找到主程序 FufuLauncher.exe。\n\n请确保主程序与本启动器位于同一目录,或在上级目录的 src/FufuLauncher/bin 下。\n\n"+copyright,
			appName,
		)
		os.Exit(1)
	}

	cmd := exec.Command(exe)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		messageBox("启动主程序失败:\n"+err.Error(), appName)
		os.Exit(2)
	}
}
