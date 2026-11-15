# tau

**tau** は AI エージェントのプロジェクトです。
同じコアの考え方をもとにした **2 つの独立した実装**を 1 つのリポジトリに置いています。

| ディレクトリ | 内容 | 実行ファイル |
|---|---|---|
| [`tau-cli/`](tau-cli/) | **コンソール（ターミナル）版**。標準入出力だけで対話する | `tau-cli.exe` |
| [`tau-web/`](tau-web/) | **Web UI 版**。ローカル HTTP サーバ + ブラウザ UI | `tau-web.exe` |

2 つは**コードを共有していません**。それぞれ単体でビルド・実行できます。

## tau-cli（コンソール版）

```
cd tau-cli
build.bat          … tau-cli.exe をビルド（Windows 添付の csc.exe / 外部ライブラリ不要）
run.bat            … 起動
tau-cli.exe        … 直接起動
tau-cli.exe --test … 自己診断テスト
tau-cli.exe --diag … 接続診断
```

- HTTP サーバも Web UI も持たず、**ターミナルだけで完結**
- 設定とセッションは `%APPDATA%\tau\`（Linux は `~/.config/tau/`）
- Markdown を色・太字・罫線で整形して表示
- ソースはクラスごとに `src/` 以下へ分割（18 ファイル）

詳しくは [`tau-cli/README.md`](tau-cli/README.md) を参照してください。

## tau-web（Web UI 版）

```
cd tau-web
build.bat          … tau-web.exe をビルド
run.bat            … 起動（ブラウザで http://localhost:8000/ を開く）
```

- 組込み HTTP サーバ + Web UI（`index.html`）
- セッション管理・テーマ切替・Mermaid 描画・コピーボタンなど
- 設定とセッションは `%APPDATA%\agentweb\`

詳しくは [`tau-web/README.md`](tau-web/README.md) を参照してください。

## ビルド環境

どちらも **Windows に同梱の `csc.exe`（C# 5 / .NET Framework 4.5）** だけでビルドできます。
追加のインストールは不要です。Linux / macOS では `mono` の `mcs` を使います。

## テスト

```
cd tau-cli && python3 test_tau.py          … 実行時回帰テスト + 自己診断
cd tau-web && python3 check_consistency.py … ソースと UI の整合性チェック
cd tau-web && python3 test_e2e.py          … 実行時回帰テスト
```

## ライセンス

[LICENSE](LICENSE) を参照してください。
