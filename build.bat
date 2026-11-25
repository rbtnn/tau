@echo off
setlocal

rem ---------------------------------------------------------------------------
rem  build.bat - tau.exe と補助ツールを .NET SDK 10 の dotnet publish でビルドします。
rem  単一ファイルの自己完結バイナリなので、.NET ランタイムは不要です。
rem
rem  tau はコンソール上で動く AI エージェントです(Web UI は --web のみ)。
rem  設定とセッションは tau 独自のファイルを使います:
rem    %APPDATA%\tau-cli\tau.json
rem    %APPDATA%\tau-cli\sessions\<id>.json
rem
rem  補助ツール(read_document が使う):
rem    pdf2text.exe    PDF からテキストを取り出す
rem    word2text.exe   Word (.docx) からテキストを取り出す
rem    excel2text.exe  Excel (.xlsx) からテキストを取り出す
rem  tau.exe と同じフォルダに置きます。
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
    echo [ERROR] tau のビルドに失敗しました。
    exit /b 1
)

rem 補助ツールを同じ dist へ出力する
for %%T in (pdf2text word2text excel2text) do (
    echo ビルド中: %%T
    dotnet publish "%~dp0src\%%T\%%T.csproj" -c Release -r win-x64 ^
        --self-contained -p:PublishSingleFile=true -o "%~dp0dist"
    if errorlevel 1 (
        echo.
        echo [ERROR] %%T のビルドに失敗しました。
        exit /b 1
    )
)

if exist "%~dp0dist\tau.exe" (
    copy /y "%~dp0dist\tau.exe" "%~dp0tau.exe" >nul
)
for %%T in (pdf2text word2text excel2text) do (
    if exist "%~dp0dist\%%T.exe" (
        copy /y "%~dp0dist\%%T.exe" "%~dp0%%T.exe" >nul
    )
)

echo.
echo ビルド完了: tau.exe, pdf2text.exe, word2text.exe, excel2text.exe
echo   実行: tau.exe
echo   ヘルプ: tau.exe --help
endlocal
