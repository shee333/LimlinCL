@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo   栎木栎林社区 - 发布服务器列表
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-servers.ps1" -Path "%~dp0community\servers.json" -Bump -Upload
echo.
echo 完成。客户端进入「社区游戏」页时会自动同步（或点「检查列表更新」立即拉取）。
pause