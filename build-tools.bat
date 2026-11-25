@echo off
setlocal

rem ---------------------------------------------------------------------------
rem  build-tools.bat - 補助ツール(pdf2text / word2text / excel2text)を
rem  単体の exe としてビルドします。通常は不要です。
rem
rem  tau.exe は PDF / Word / Excel のパーサを内蔵しているので、
rem  ドキュメントを読むだけなら tau.exe だけで足ります。
rem  この exe は、コマンドラインから直接 PDF をテキストにしたい場合や、
rem  他のプログラムから呼びたい場合に使います。
rem
rem  出力先: dist-tools
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] dotnet が見つかりません。.NET SDK 10 をインストールしてください。
    echo         winget install Microsoft.DotNet.SDK.10
    exit /b 1
)

if exist "%~dp0dist-tools" rmdir /s /q "%~dp0dist-tools"

for %%T in (pdf2text word2text excel2text) do (
    echo ビルド中: %%T
    dotnet publish "%~dp0src\%%T\%%T.csproj" -c Release -r win-x64 ^
        --self-contained -p:PublishSingleFile=true -o "%~dp0dist-tools"
    if errorlevel 1 (
        echo.
        echo [ERROR] %%T のビルドに失敗しました。
        exit /b 1
    )
)

echo.
echo ビルド完了: dist-tools フォルダ
echo   pdf2text.exe, word2text.exe, excel2text.exe
echo   使い方: pdf2text.exe --help
endlocal
