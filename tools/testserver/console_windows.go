//go:build windows

package main

import "syscall"

// enableUTF8Console switches the console output code page to UTF-8 so Chinese
// text is readable instead of mojibake under the default legacy code page.
func enableUTF8Console() {
	kernel32 := syscall.NewLazyDLL("kernel32.dll")
	setConsoleOutputCP := kernel32.NewProc("SetConsoleOutputCP")
	setConsoleCP := kernel32.NewProc("SetConsoleCP")
	_, _, _ = setConsoleOutputCP.Call(uintptr(65001))
	_, _, _ = setConsoleCP.Call(uintptr(65001))
}
