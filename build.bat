@echo off
setlocal

rem ---------------------------------------------------------------------------
rem  build.bat - tau.exe を .NET SDK 10 の dotnet publish でビルドします。
rem  単一ファイルの自己完結バイナリを作るので、.NET ランタイムは不要です。
rem
rem  tau はコンソール上で動く AI エージェントです(Web UI はありません)。
rem  設定とセッションは tau 独自のファイルを使います:
rem    %APPDATA%\tau-cli\tau.json
rem    %APPDATA%\tau-cli\sessions\<id>.json
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] dotnet が見つかりません。.NET SDK 10 をインストールしてください。
    echo         winget install Microsoft.DotNet.SDK.10
    exit /b 1
)

echo ビルド中: dotnet publish -c Release -r win-x64
dotnet publish "%~dp0tau-cli.csproj" -c Release -r win-x64 ^
    --self-contained -p:PublishSingleFile=true -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo [ERROR] ビルドに失敗しました。
    exit /b 1
)

if exist "%~dp0dist\tau.exe" (
    copy /y "%~dp0dist\tau.exe" "%~dp0tau.exe" >nul
)

echo.
echo ビルド完了: tau.exe
echo   実行: tau.exe
echo   ヘルプ: tau.exe --help
endlocal
