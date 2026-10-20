@echo off
chcp 65001 >nul
setlocal
echo.
echo =========================================================
echo   SciBridge 自检（只读）
echo =========================================================
echo.
echo 这个自检会连接本机的测试服务器（127.0.0.1:2222），把每个只读操作跑一遍，
echo 并打印一份“通过/失败”清单。
echo.
echo 请先运行“启动测试服务器.cmd”，并保持那个窗口开着。
echo.
echo 下面用的是测试服务器自带的固定账号 test / scibridge，
echo 与你真实的机构服务器密码无关。
echo.
"%~dp0..\SciBridge.exe" --self-test --host 127.0.0.1 --port 2222 --user test --root . --password scibridge
echo.
pause
