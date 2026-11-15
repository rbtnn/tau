@echo off
setlocal
chcp 65001 >nul 2>nul
cd /d "%~dp0"

echo ============================================================
echo  tau input diagnostic
echo ============================================================
echo.
echo  This script checks, step by step, where tau stops when you
echo  type a long Japanese prompt.
echo.

echo [1] Version of tau-cli.exe
if not exist "tau-cli.exe" (
    echo     tau-cli.exe not found. Run build.bat first.
    goto :eof
)
tau-cli.exe --version
echo.

echo [2] Console code page
chcp
echo.

echo [3] Environment variables
echo     TAU_DEBUG       = %TAU_DEBUG%
echo     TAU_INPUT_ENCODING = %TAU_INPUT_ENCODING%
echo     NO_COLOR        = %NO_COLOR%
echo.

echo [4] Configuration
tau-cli.exe --config "%APPDATA%\tau\tau.json" --list >nul 2>nul
echo     (see %APPDATA%\tau\tau.json)
echo.

echo [5] Long Japanese input through tau (with TAU_DEBUG=1)
echo ------------------------------------------------------------
echo  Type a LONG Japanese sentence below and press Enter.
echo  Then type /exit and press Enter.
echo.
set TAU_DEBUG=1
tau-cli.exe
echo ------------------------------------------------------------
echo.

echo [6] If tau printed nothing after Enter, run the same text
echo     through a file to see whether input is the problem:
echo       notepad input.txt
echo       tau-cli.exe ^< input.txt
echo.
pause
