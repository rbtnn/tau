@echo off
rem  コンソール版 tau-web.exe を起動します。
cd /d "%~dp0"
if not exist "tau-web.exe" (
    echo [ERROR] tau-web.exe がありません。先に build.bat を実行してください。
    pause
    exit /b 1
)
tau-web.exe %*
pause
