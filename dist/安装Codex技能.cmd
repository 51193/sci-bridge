@echo off
chcp 65001 >nul
setlocal
set "TARGET=%USERPROFILE%\.codex\skills\sci-data-bridge"
set "SOURCE=%~dp0skill\sci-data-bridge"

echo.
echo 正在安装 Codex 技能：sci-data-bridge
echo.

if not exist "%SOURCE%\SKILL.md" (
  echo [失败] 找不到 %SOURCE%\SKILL.md
  echo        请确认解压时保留了 skill 子文件夹，然后重试。
  echo.
  pause
  exit /b 1
)

if not exist "%USERPROFILE%\.codex\skills" mkdir "%USERPROFILE%\.codex\skills"

xcopy /E /I /Y "%SOURCE%" "%TARGET%" >nul
if errorlevel 1 (
  echo [失败] 复制文件时出错。请把上面的信息发给帮你准备这个工具的人。
  echo.
  pause
  exit /b 1
)

echo [成功] 技能已安装到：
echo        %TARGET%
echo.
echo 请关闭并重新打开 Codex，让它识别这个新技能。
echo （不安装也能用：直接把 SciBridge 窗口里的交接文字粘给 Codex 即可。）
echo.
pause
exit /b 0
