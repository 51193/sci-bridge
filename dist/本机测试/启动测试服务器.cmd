@echo off
chcp 65001 >nul
setlocal
echo.
echo 正在启动 SciBridge 本机测试服务器（只读）...
echo 保持这个窗口开着；按 Ctrl+C 停止。
echo.
"%~dp0SciBridgeTestServer.exe" -port 2222 %*
echo.
echo 测试服务器已停止。
pause
