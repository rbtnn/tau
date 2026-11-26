# tau — コンソール上で動く AI エージェント

`tau` は **コンソール（ターミナル）で動く AI エージェント**です。
ソースは **クラスごとに 1 ファイル**に分割されています（`src/` 以下）。

通常は標準入出力だけで対話します。`--web` を付けるとブラウザからも使えますが、
**緊急用の最小構成**です（チャットとセッション切替だけ）。

`tau` が内部に持つメッセージ（ヘルプ・ツール定義・エラー表示など）は**英語**です。

---

## ビルドと実行（Windows）

```
build.bat          … tau.exe をビルド（.NET SDK 10 が必要）
run.bat            … 起動（未ビルドなら自動でビルド）
tau.exe            … 直接起動
build-tools.bat    … 補助ツール(pdf2text など)を単体でビルド（通常は不要）
```

`build.bat` は `dotnet publish` で単一ファイルの `tau.exe` を作ります。
PDF / Word / Excel の読み取りも含めて **1 つの exe（約 16MB）**です。
**.NET SDK 10** が必要です（`winget install Microsoft.DotNet.SDK.10`）。

Linux でもビルドして動かせます（開発・テスト用）。

```
dotnet publish tau-cli.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist
./dist/tau
```

配布しているのは **Windows 版（x86 / x64）だけ**です。

---

## 使い方

```
tau.exe [オプション]

  --version            ビルド識別子を表示して終了
  -e, --execute <text> 1 回だけプロンプトを送って終了（セッションに保存しない）
  --config show        現在の設定を表示
  --config set <k> <v> 設定を 1 項目だけ変更 (例: --config set model gpt-4o)
                       設定には設定ファイル(tau.json)のパスも含まれます
  --config edit        設定を対話形式で変更
  --new                新しいセッションを開始
  --open <n/id>        既存セッションを開いて続きから対話する
  --list               保存済みセッションの一覧を表示して終了
  --test               接続診断(TLS/HTTP)と自己診断を実行
                       (実際の API リクエストを含みます)
  --web                ブラウザ用の最小 Web UI を起動（緊急用）
  --port <n>           --web のポート (既定 8080、使用中なら空きを選ぶ)
  --debug              デバッグ出力を有効にする（TAU_DEBUG=1 と同じ）
  --pdf2text <file>    PDF のテキストを出力（pdf2text.exe と同じ動作）
  --word2text <file>   Word のテキストを出力
  --excel2text <file>  Excel のテキストを出力
                       --json / --describe / --sheet / --rows も使える
  --help, -h, /?       ヘルプを表示
```

> `--config <path>` のように**パスを直接渡す**書き方も引き続き使えます。

### 1 回だけ実行する（`-e`）

`-e` を付けると、プロンプトを 1 回送って終了します。**セッションには保存しません**。
スクリプトやテストから使えます。

```
tau.exe -e "このコードを説明して"
tau.exe -e "1行目
2行目"
```

- バナー・進捗表示・`Bye.` の行は出ません（結果だけが出力されます）
- セッションを作らないので、`%APPDATA%\tau-cli\sessions` は変わりません
- `--open` と併用すると、そのセッションの履歴を文脈として使えます（書き換えません）

### 複数行を入力する

ソースコードを貼り付ける場合は、**クリップボードからそのまま貼り付け**られます。

```
> [Ctrl-V]      ← クリップボードの内容がそのまま入る
  (pasted 12 lines)
```

- 改行・空行・インデントがそのまま保持されます
- 1 行に潰れないので、コードをそのまま貼れます

**パイプやファイルからも渡せます。**

```
tau.exe < prompt.txt
type prompt.txt | tau.exe
```

`/exit` などのコマンド行が来るまでを **1 つのメッセージ**として送ります。

```
$ printf 'def f(x):\n    return x\n/exit\n' | tau.exe
→ "def f(x):\n    return x" を 1 メッセージとして送信
```

> `"""` や `\` による区切りは採用していません。ソースコードに普通に現れるためです。

### 設定をコマンドラインから扱う

```
tau.exe --config show
tau.exe --config set model gpt-4o
tau.exe --config set allowTools true
tau.exe --config edit
```

`--config show` は設定ファイルのパスと全項目を表示します。
`--config set` は 1 項目だけ変更して保存します。`--config edit` は対話形式です。

起動すると、そのまま入力を受け付けます。**前回開いていたセッションが自動で復元**され、
過去の会話が表示された状態から続きを入力できます。

```
> ファイル一覧を見せて
  [thinking] まず一覧を見よう。
  ▶ Tool: list_dir  .

一覧を取得しました。完了です。
```

### 対話中のコマンド

| コマンド | 説明 |
|---|---|
| `/n[ew]` | 新しいセッションを開始 |
| `/l[ist]` | 保存済みセッションの一覧（番号付き） |
| `/o[pen] <n\|id>` | セッションを開く（番号は `/list` のもの） |
| `/d[elete] <n\|id>` | セッションを削除（確認あり） |
| `/c[lear]` | 現在のセッションの会話履歴をクリア（確認あり） |
| `/t[ools]` | ツール実行の on/off を切り替え |
| `/h[elp]` | ヘルプを表示 |
| `/e[xit]`, `/q[uit]` | 終了（`Ctrl+D` でも終了） |

`[ ]` は **Vim のヘルプと同じ書き方**で、省略できる部分を表します。
`/n` でも `/ne` でも `/new` でも同じコマンドです。

モデルやツールなどの設定変更は **`--config`** に一本化しました。
`/model` `/set` `/config` は廃止しています。

```
tau.exe --config show
tau.exe --config set model gpt-4o
tau.exe --config edit
```

### セッションの内容をクリア

`/clear` は**現在のセッションの会話履歴だけ**を消します。セッション自体（ID・タイトル）は残ります。

```
> /clear
  このセッションの会話履歴を消しますか？ (12 件)  [y/N]: y
  · Cleared the session history: s20261109...  (タイトル)
```

- `y` / `yes` 以外を入力すると中止します
- `messages` / `summary` / `contextStart` をリセットします（コンテキスト整理の状態も初期化）
- 会話が空のセッションでは何もしません（メッセージだけ表示）

> 以前の「画面を消す」動作は廃止しました。画面を消したい場合はコンソールの `cls`（Windows）/
> `clear`（Unix）を使ってください。

### セッションの削除

`/list` で番号付きの一覧が出ます。`*` が現在のセッションです。

```
> /list
  保存済みセッション (C:\Users\...\AppData\Roaming\tau\sessions)
   1. s20261109...  Mermaid矢印置換  (12 件)
*  2. s20261109...  テスト            (4 件)
   3. s20261109...  新規会話          (2 件)

  * = 現在のセッション。開く: /open <番号|ID>、削除: /delete <番号|ID>
```

**番号**でも **ID** でも指定できます。誤操作を防ぐため確認があります。

```
> /delete 3
  削除しますか？ s20261109...  (新規会話)  [y/N]: y
  · 削除しました: s20261109...  (新規会話)
```

- `y` / `yes` 以外を入力すると中止します
- **表示中のセッションを削除した場合**は、自動的に別のセッション（無ければ新規）へ切り替わります
- 削除した ID は「最後に開いていたセッション」からも取り除かれます

### セッションを開く

`/open` も `/delete` と同じく、**番号**でも **ID** でも指定できます。

```
> /open 3
  · Opened session: s20261109...  (新規会話)

> /open s20261109...
  · Opened session: s20261109...  (新規会話)
```

番号は `/list` の表示順（1 始まり）です。範囲外の番号や存在しない ID を
指定すると、使い方の案内が出ます。

`%APPDATA%\tau-cli\sessions\` のファイルを直接消しても構いません（`_state.json` は残してください）。

`Ctrl+C` で生成中の応答を中断できます。

---

## Web UI（緊急用）

`--web` を付けると、ブラウザからも対話できます。**コンソールが使えないときの
緊急用**の位置づけで、できることは意図的に絞ってあります。

```
tau.exe --web
tau.exe --web --port 9000
```

起動すると URL が表示され、ブラウザで開けます。

```
  · Web UI: http://127.0.0.1:8080/
  Settings come from C:\Users\...\AppData\Roaming\tau-cli\tau.json (read-only from the browser)
  Press Ctrl-C to stop
```

### できること

| 機能 | 内容 |
|---|---|
| チャット | 送信・思考・ツール実行・本文のストリーミング表示 |
| セッション切替 | 一覧から選ぶ／新規作成 |
| 履歴の表示 | 選んだセッションの過去のやりとり |

### できないこと（意図的）

- **設定の変更**（モデル・API キー・ツール on/off など）
- セッションの削除・名前変更
- Mermaid などのリッチ表示

設定は `tau.json` を直接編集してください。ブラウザからは**読み取り専用**です。

### 仕様

| 項目 | 内容 |
|---|---|
| 待ち受け | `127.0.0.1` のみ（外部からは接続できません） |
| 既定ポート | 8080（使用中なら空きポートを自動で選ぶ） |
| 同時実行 | 1 ターンだけ（二重送信は拒否） |
| 中止 | `Ctrl-C`（コンソール側）で停止 |
| 画面 | 実行ファイルに埋め込み（追加ファイル不要） |

`--port` でポートを指定できます。

```
tau.exe --web --port 9000
```

### API

画面が使うのは次の 4 つだけです。

| メソッド | パス | 用途 |
|---|---|---|
| `GET` | `/` | 画面 |
| `GET` | `/api/sessions` | セッション一覧 |
| `GET` | `/api/sessions/{id}` | 1 セッションの履歴 |
| `POST` | `/api/chat/stream` | チャット（SSE） |

SSE のイベントは `think`（思考）・`tool`（ツール実行）・`delta`（本文）・
`done`（終了）・`error`（エラー）・`log`（補助表示）です。

---

## 設定とデータの保存先

設定とセッションは `tau-cli` 独自の場所に保存します。

| 種類 | パス |
|---|---|
| 設定 | `%APPDATA%\tau-cli\tau.json` |
| メモリ（全セッション共通） | `%APPDATA%\tau-cli\memory.json` |
| セッション | `%APPDATA%\tau-cli\sessions\<id>.json` |
| 最後に開いたセッション | `%APPDATA%\tau-cli\sessions\_state.json` |

Linux / macOS では `~/.config/tau-cli/` が使われます。

> `--web` で起動したときも、設定とセッションは**同じ場所**を使います。
> ブラウザからは設定を変更できません。

### 旧バージョンからの移行

以前のバージョンは `%APPDATA%\tau\` に保存していました。
起動時に **`%APPDATA%\tau-cli\` が空で、`%APPDATA%\tau\` にデータがある場合**、
自動でコピーします。

```
Migrated data from C:\Users\...\AppData\Roaming\tau to
  C:\Users\...\AppData\Roaming\tau-cli
  (the old directory was left in place; you can delete it once you are happy)
```

- コピーのみで、**旧いフォルダは削除しません**（確認後に手動で消してください）
- 新しい場所に既にデータがある場合は**移行しません**（上書きしない）
- `--config` でパスを明示した場合は移行しません

### 環境変数

`agentweb` と同じ名前の環境変数が使えます（設定ファイルがある場合はそちらが優先）。

```
LLM_API_URL / LLM_API_KEY / LLM_MODEL
ALLOW_TOOLS / LLM_EMIT_REASONING
MAX_TOOLS_PER_TURN / TOOL_TIMEOUT_SEC
LLM_CONTEXT_WINDOW / LLM_RESERVE_TOKENS / LLM_KEEP_RECENT_TOKENS
LLM_PROXY_URL / LLM_PROXY_USERNAME / LLM_PROXY_PASSWORD / LLM_USE_SYSTEM_PROXY
AI_SYSTEM_PROMPT
TAU_CONFIG
```

`tau.json` が無くても、環境変数だけで動作します。

---

## 設定項目（`tau.json`）

`tau` が使う項目は次のとおりです。

```json
{
  "llmApiUrl": "https://api.openai.com/v1/chat/completions",
  "llmApiKey": "YOUR_API_KEY",
  "model": "gpt-4o-mini",
  "allowTools": true,
  "emitReasoning": true,
  "maxToolsPerTurn": 16,
  "toolTimeoutSec": 30,
  "contextWindow": 64000,
  "reserveTokens": 12000,
  "keepRecentTokens": 20000,
  "proxyUrl": "",
  "proxyUser": "",
  "proxyPass": "",
  "useSystemProxy": true,
  "systemPrompt": "",
  "inputEncoding": "auto",
  "memoryMaxChars": 8000
}
```

- モデルは `model` に**1 つだけ**指定します。`models`（モデル名の一覧）は `tau` では使いません。
  接続先・API キー・コンテキスト上限も共通の 1 つです。
- モデル名は任意の名前を受け付けます（事前登録は不要）。
- 既存の `tau.json` に `models` があっても無視され、保存時に取り除かれます
  （`agentweb` 側の `models` はそのまま残ります）。
- `tau` が扱わないキーを手で追記しても、保存時に失われません。

### 設定を変更する

`tau.json` を直接編集しなくても、コマンドラインから設定を変更できます。

| コマンド | 説明 |
|---|---|
| `--config show` | 現在の設定を一覧表示（API キーは伏せ字） |
| `--config edit` | 番号を選んで 1 項目ずつ対話形式で変更 |
| `--config set <k> <v>` | 1 項目だけ変更 |
| `/tools` | 対話中にツール実行の on/off を切り替え |

```
> tau.exe --config show
  Config file: C:\Users\...\AppData\Roaming\tau-cli\tau.json

    llmApiUrl          = https://api.openai.com/v1/chat/completions
    llmApiKey          = sk-...********
    model              = gpt-4o-mini
    allowTools         = true
    emitReasoning      = true
    contextWindow      = 64000
    ...

> tau.exe --config set model gpt-4o
  · model = gpt-4o  (updated)

> tau.exe --config set contextWindow 128000
  · contextWindow = 128000  (updated)

> tau.exe --config edit
  1. llmApiUrl          = https://...
     OpenAI 互換の /chat/completions の URL
  2. llmApiKey          = sk-...********
     API キー(表示時は伏せ字)
  ...
  変更する項目の番号を入力 (Enter で中止): 3
  model  (使用するモデル名)
  現在値: gpt-4o
  新しい値を入力 (Enter で中止): gpt-4o-mini
  · model = gpt-4o-mini  に変更しました
```

変更は即座に `tau.json` へ保存され、次回の起動から反映されます。
`systemPrompt` だけは**複数行**で入力できます（空行で終了）。

入力チェックも行います（不明な key、数値でない値、不正な真偽値は拒否）。

---

## 機能

### 日本語の入出力

出力は常に **UTF-8**（日本語の表示が化けない）。
**入力のエンコーディングは `inputEncoding` 設定で選べます。**

| `inputEncoding` | 内容 |
|---|---|
| **`auto`**（既定） | コンソールに合わせる。`chcp 65001` なら UTF-8、それ以外は Shift-JIS（CP932） |
| `utf-8` | 常に UTF-8（CP65001） |
| `shift-jis` | 常に Shift-JIS（CP932） |
| `default` | コンソール/OS の既定に任せる（変更しない） |

```
tau.json:
  "inputEncoding": "auto"
```

**`auto` が既定です。** 起動時にコンソールのコードページを見て決めます。

- `chcp 65001`（UTF-8）のコンソールなら、入力も UTF-8 のまま使います
- それ以外（CP932 など）なら、従来どおり CP932 にします

出力は常に UTF-8 です。入力と出力のコードページが食い違うと、Windows では
日本語が NUL 文字（`\u0000`）に化けることがあります（dotnet/runtime#43295）。
`auto` はこの食い違いを避けます。

- 入力が**リダイレクト**されている場合は設定に関係なく触りません（パイプ・ファイル入力）
- 設定は `--config set inputEncoding utf-8` または `--config edit` の **16. Input Encoding** で変更できます
- `cp932` / `sjis` / `65001` / コードページ番号などの別名も受け付けます

#### 入力が NUL に化けた場合

以前は NUL を黙って除去した結果、**入力が空になり何も表示せず再入力待ち**になって
「応答が返らない」ように見えることがありました。
現在はその場合に警告と対処方法を表示します。

```
> ⚠ The input was lost (all 30 characters became NUL).
  The console input encoding does not match.
  Try one of the following:
   - Run `chcp 65001` before starting tau (or run it in Windows Terminal)
   - Set TAU_DEBUG=1 and check the reported input encoding
   - Paste the text from a file:  tau --open <id> < input.txt
  · input encoding: utf-8 (CP65001)
```

一部だけ NUL に化けた場合は、**欠落した文字数を表示**したうえで、
残りの文字を送信します。

**`--config edit` の複数行入力（システムプロンプトなど）でも同じ対処をします。**
以前は NUL で空になった行を「入力終了の空行」と誤判定し、何も入力できていないのに
即終了して反映されない、という状態になっていました。現在は:

- NUL で全滅した行は**空行扱いにせず**、警告して入力をやり直してもらう
- 一部だけ NUL の行は、欠落を表示して残りを受け付ける

また、**連続して入力が失われる場合は打ち切って**対処方法を案内します（無限ループ防止）。

```
  The input has been lost 2 times in a row; stopping here.
  Change the console code page (chcp 65001) and start tau again,
  or write your text to a file and pipe it in:
    tau.exe < input.txt
```

`tau --test` でも現在の入出力エンコーディングを確認できます。

```
  [environment]
    input encoding : utf-8 (CP65001)
    output encoding: utf-8 (CP65001)
```

### Markdown の整形表示

モデルの回答は Markdown として解釈し、**記号をそのまま出さずに色・太字・罫線に変換**して表示します。

| Markdown | 表示 |
|---|---|
| `# 見出し` / `## 見出し` | 太字＋色、下に `====` / `----` の下線 |
| `**太字**` / `*斜体*` | **太字** / *斜体*（ANSI） |
| `__太字__` / `_斜体_` | 同上（`_` も使えます。下記の注意を参照） |
| `` `コード` `` | 黄色の文字 |
| ` ```コードブロック``` ` | 緑色。**罫線なし**（言語名は `── lang` の行） |
| `- 項目` / `* 項目` | `・項目`（インデントは深さで表現） |
| `1. 項目` | `1. 項目`（そのまま、色付き） |
| `| A | B |` | **罫線で整形した表**（下記） |
| `> 引用` | 左に `│` を付けて斜体 |
| `---` | 横罫線 |
| `[text](url)` | `text (url)`（下線＋青、URL は灰色） |

表は罫線文字で枠を作り、**列幅を内容に合わせて自動調整**します。
`:---` `---:` `:---:` の指定で左寄せ・右寄せ・中央寄せになります。
**全角文字を 2 桁幅として計算**するので、日本語の表でも桁がずれません。

幅の判定は Unicode の **East Asian Width** に従います。

| 分類 | 幅 | 例 |
|---|---|---|
| `W` / `F`（全角） | 2 桁 | あ 漢 Ａ ー ～ |
| **`A`（Ambiguous）** | **2 桁**（日本語端末） | **①②③ ← → ■ □ ● ○ ★ ☆ ※ ℡ № Ⅰ ⅰ ㈱ ㍿** |
| `Na` / `H`（半角） | 1 桁 | A 1 ｱ - |

`A` の文字は欧文端末では 1 桁ですが、日本語のターミナルでは 2 桁で表示されます。
**どちらになるかは端末のフォント次第**で、Unicode のデータからは決められません。

ただし、**実際に何桁で描かれるかは端末のフォント次第**です。
`①②③` を 1 桁で描くコンソール（フォント）もあるため、tau は
**起動時に実際の表示幅を測って**判断します。

- **Windows の対話コンソール**では、起動時に実際に文字を書いて
  カーソル位置から幅を測ります。丸数字・矢印・図形・星・記号・単位・
  ローマ数字・囲み文字など **40 文字以上**を測り、**多数決**で
  Ambiguous の幅を決めます（ブロックによって描き方が違うフォントもあるため）
- 測定は毎回**カーソルを 1 行目の先頭に戻して**から行います。
  `Console.Out` はバッファされるため、**フラッシュしてから `WriteConsoleW` で
  直接書き込み**、カーソル位置を読みます。測定後は空白で上書きして消します
  （以前は `\b \b` で戻していたため、カーソルがずれて後続の測定が壊れていました）
- 測定結果が矛盾している場合（「あ」が 2 桁でない、`A` が 1 桁でない）は
  **測定値を捨てて計算値に戻します**
- カーソル位置は `GetConsoleScreenBufferInfo` で直接読みます。
  `Console.CursorLeft` は Mono や一部の端末で `ESC[6n`（カーソル位置問い合わせ）
  を送って応答待ちになるため使いません
- 測れない環境（リダイレクト・Windows 以外）では計算値（`A` は 2 桁）を使います

手動で指定したい場合は環境変数で強制できます。

```
set TAU_AMBIGUOUS_WIDTH=1   … ①②③ を半角(1桁)として扱う
set TAU_AMBIGUOUS_WIDTH=2   … ①②③ を全角(2桁)として扱う
```

`tau --test` の「6. Markdown renderer」で、実際の端末が何桁で描いているかと、
罫線が揃っているかの見本が表示されます。

### コードブロックは罫線で囲まない

コードブロックを `│` などで囲むと、**端末からコピーしたときに罫線と余白まで
貼り付いてしまいます**。そのため罫線は使わず、次の形で表示します。

```
── csharp
int x = 1;
if (x > 0) {
    Console.WriteLine(x);
}
──
```

コードの各行は**行頭から始まり、末尾に余白が付きません**。
範囲選択してコピーすれば、そのままコードとして貼り付けられます。

### 識別子の `_` は斜体にしない

`XXX_YYY_ZZZ` のような識別子を斜体として扱うと、`_` が消えて
`XXXYYYZZZ` になってしまいます。CommonMark と同じく、
**単語の内側にある `_` は装飾とみなしません**。

| 入力 | 表示 |
|---|---|
| `XXX_YYY_ZZZ` | `XXX_YYY_ZZZ`（そのまま） |
| `snake_case_name` | `snake_case_name`（そのまま） |
| `日本語_テスト_日本語` | そのまま（日本語も単語の一部とみなす） |
| `_斜体_` | *斜体*（前後が単語でないので装飾される） |
| `prefix _斜体_ suffix` | prefix *斜体* suffix |

`*` は識別子に使われないため、`a*b*c` は従来どおり斜体になります。

**同じプログラムでも、環境によって幅は変わります。**

| 環境 | コードページ | `①②③` の幅 | tau の測定結果 |
|---|---|---|---|
| 英語ロケールの Windows | 437 | 1 桁 | `1 column` |
| 日本語 Windows（フォント次第） | 932 | 1 桁 or 2 桁 | 実測に従う |

#### 無人実行・画面キャプチャできない環境での確認

```
set TAU_TEST_NO_INPUT=1            … 対話入力の確認を省く
set TAU_TEST_WIDTH_FILE=C:\width.txt … 測定結果をファイルに書き出す
```

`TAU_TEST_WIDTH_FILE` の中身:

```
ambiguous=1
あ=2
A=1
ｱ=1
①=1
③=1
⑩=1
←=1
...
```

`ambiguous=` が端末の実測値、それ以降が文字ごとの幅です。
**別の PC と比較すれば、どちらが正しいか判断できます。**

```
       terminal renders ambiguous-width as: 1 column (half-width)
       --- visual check: every | should line up ---
       +------+
       | あ   |
       | ①   |
       | ←   |
       | ★   |
       | A    |
       | ｱ    |
       +------+
```

```
  ┌────────┬────────────┐
  │ 項目   │       説明 │
  ├────────┼────────────┤
  │ あ     │ あいうえお │
  │ あああ │         い │
  └────────┴────────────┘
```

`NO_COLOR=1` を設定するか、出力をリダイレクトすると色は付かず、罫線と字下げだけで整形されます。

### 動かないときの確認

まず **どのビルドを動かしているか** を確認してください。
古い `tau.exe` が残っていると、修正が反映されません。

```
tau.exe --version
```

`run.bat` はソースが新しい場合に自動でビルドし直すようにしました。

#### どこで止まっているかを調べる

```
set TAU_DEBUG=1
tau.exe
```

次のように、段階ごとに表示されます。

```
  [waiting for input]      ← 入力待ち(ここで止まるなら入力の問題)
>   [input] length=100     ← 入力を受け取った文字数
  sending 100 chars...     ← API へ送信
  / connecting...          ← 接続中
  - receiving... 45 chars  ← 受信中の進行状況
```

- `[waiting for input]` の後で何も出ない → **入力が返ってきていない**
- `[input] length=0` → 入力が空になっている(エンコーディング不一致)
- `sending ...` の後で止まる → **通信の問題**

### `tau --test`（自己診断テスト）

`tau --test` は接続診断（実際の API リクエストを含む）と、実行環境に依存する
自己診断を実行します。ネットワークを使わず確認する場合は `--test=offline` を使います。

```
tau.exe --test            … 接続診断 + 自己診断
tau.exe --test=offline    … 自己診断だけ（CI 向け）
```

自己診断は環境・コンソールモード・エンコーディング・長文のエンコーディング往復・
TLS 設定の 5 セクションです。詳細は後述の「テスト」を参照してください。
JSON・Markdown・セッション保存などの検証は `dotnet test` に移しています。

`diag_input.bat` をダブルクリックすると、バージョン・コードページ・
環境変数・長い日本語入力のテストをまとめて実行できます。

#### 行編集（Emacs キーバインド）

`chcp 65001`（UTF-8）のコンソールでは、自前の行編集が有効になります。

対話で動かしているときは、入力の前後に罫線を引きます。
応答との境目が分かりやすくなります。罫線は入力待ちに入る前に
上下とも表示されるので、Enter を押すのを待たずに枠が見えます。

```
────────────────────────────────────────
> 仕様書を要約して
────────────────────────────────────────
（応答）

────────────────────────────────────────
> _
────────────────────────────────────────
```

罫線はパイプ入力（`tau < input.txt`）では引きません。

セッションを再開したときの履歴表示も、対話中と同じ形（上下の罫線と
`> `）で出ます。見出しは付けません。

```
────────────────────────────────────────
> 最初の質問
────────────────────────────────────────

最初の回答

────────────────────────────────────────
> 次の質問
────────────────────────────────────────

次の回答
```

| キー | 動作 |
|---|---|
| Ctrl-P / 上キー | 1 つ前の入力(履歴) |
| Ctrl-N / 下キー | 1 つ後の入力(履歴) |
| Ctrl-A / Ctrl-E | 行頭 / 行末へ |
| Ctrl-B / Ctrl-F | 1 文字戻る / 進む |
| Ctrl-K / Ctrl-U | カーソル以降 / 以前を削除 |
| Ctrl-W | 直前の単語を削除 |
| Ctrl-Y | ペースト |
| Alt-B / Alt-F | 単語単位で移動 |
| Alt-D | 次の単語を削除 |
| Ctrl-T | 文字の入れ替え |
| Ctrl-L | 画面をクリアして再描画 |

履歴は対話で入力した行を覚えます(`/exit` などのコマンドも含みます)。
空行と、直前と同じ行は残しません。シェルと同じ感覚で使えます。
履歴は保存しないので、tau を起動し直すと空から始まります。

UTF-8 以外（CP932 など）のコンソールでは、`Console.ReadKey()` が返す日本語が
壊れる（「あ」→ U+7E3A）ため、従来どおり `Console.ReadLine()` に任せます。

`tau --test` で今どちらで動いているかを確認できます。

```
console code page: UTF-8 (chcp 65001)
line editing: on (Emacs keys)
```

`inputEncoding` に `utf-8` や `shift-jis` を明示した場合は、自動判定より
その指定が優先されます。

#### 入力が返ってこない場合

Windows のコンソールでは `Console.ReadKey()` が IME と組み合わさると
入力が返らないことがあります。そのため UTF-8 以外のコンソールでは
自前の行編集を使いません（`Console.ReadLine()` に任せます）。
実際のコンソール コードページ（`GetConsoleCP` / `GetConsoleOutputCP`）も
確認しています。

`inputEncoding` が実際のコンソールと合っていないと入力が壊れます。
`chcp` で表示されるコードページと `inputEncoding` を合わせてください。

```
chcp                 ← 現在のコードページを確認(932 など)
--config set inputEncoding shift-jis
```

### 貼り付け（Ctrl-V）

Windows のコンソールで **`Ctrl-V`** による貼り付けが使えます。

- Windows の対話コンソールでは `ReadConsoleW` で行を読みます。コンソールの
  行編集（`ENABLE_LINE_INPUT`）が働くため、**`Ctrl-V` の貼り付け・矢印キー・
  `Home`/`End`・IME** がそのまま使えます。
- `Console.In` を差し替えると `ReadFile` 経由になり、行編集を通らないため
  `Ctrl-V` が効かなくなります。そのため **Windows の対話時は差し替えません**。
- 行編集が使えない環境では `Ctrl-V` が `SYN`（0x16）として届きます。その場合は
  クリップボードを直接読んで貼り付けます。改行は空白に置き換えます。

```
> 貼り付けたテキスト
```

> `Ctrl-V` で何も貼り付かないときは、クリップボードが空か読めていない旨を
> 表示します。

### Ctrl-C による中止

`Ctrl-C` を押しても **プロセスは終了しません**。状況に応じて次のように動きます。

| 状況 | 動作 |
|---|---|
| **応答を受信中** | 通信を中断し、`(cancelled)` と表示してプロンプトに戻る |
| **入力待ち** | 入力中の行を捨て、`(cancelled input)` と表示してプロンプトに戻る |
| ツール実行中 | ターンを中止し、プロンプトに戻る |

いずれの場合も**そのまま次のプロンプトを入力できます**。
中止したターンは履歴に `⚠ Cancelled.` として残ります。

終了したいときは `/exit`（または `/quit`）を使ってください。

> 入力待ちの Ctrl-C は、押したあと **Enter** を押すと確定します
> （`Console.ReadLine()` はシグナルで中断できないため）。

### 進行状況の表示

Markdown の整形には回答全体が必要なため、本文はいったんバッファして
完成後にまとめて表示します。そのままだと長い回答の間ずっと画面が止まって
「応答が無い」ように見えるため、**受信中の進行状況**を同じ行に表示します。

```
> 日本語の長いプロンプトです。...
  / connecting...  - receiving... 45 chars  \ receiving... 94 chars
```

- Enter を押した直後に `connecting...` が出ます（無言の時間を作りません）
- 受信が進むと文字数が増えます
- 表示が終わると進行状況の行は消え、整形された回答に置き換わります

`TAU_DEBUG=1` のときは送信した文字数も表示します。

### 入力（行編集）

入力は **`Console.ReadLine()`**（コンソール標準の行編集）に任せています。
`←` `→` `Home` `End` `Backspace` `Delete` などは、お使いのコンソールの
標準機能としてそのまま使えます。

> **自前の Emacs 風キーバインドは使っていません。**
> `Console.ReadKey()` で 1 文字ずつ読む方式は、コンソールの実際の
> コードページと `inputEncoding` が合っていないと日本語が壊れ
> （例: 「あ」→ U+7E3A）、**長い日本語が途中で切れて送信される**
> ことがありました。安定して動く `Console.ReadLine()` に任せています。
>
> 起動時に `SetConsoleCP` でコードページを `inputEncoding` に合わせる
> 処理は入れてあるので、将来復活させる土台はできています。ただし
> **実機で長い日本語と IME の確認が取れるまで有効にしない**方針です。

日本語が化ける場合は `inputEncoding` を実際のコンソールに合わせてください。

#### 長い入力が途中で切れる場合

`Console.ReadLine()` は .NET Framework で**既定 256 単位の入力バッファ**を使います。
この上限はバイト数ではなく**文字数**で判定され、256 には CR/LF も含まれるため
**254 文字**までしか読めません。それ以降はエラーも警告もなく黙って捨てられます。

全角・半角を問わず **254 文字**で切れるので、日本語の長いプロンプトが
途中で切れて送信されます。

> 実測例: `[input] length=254 bytes=532`
> 254 は**文字数**の上限で、532 バイト（UTF-8）は上限とは無関係です。

tau は起動時に `Console.In` を **64KB バッファの StreamReader** に差し替えて
この上限を回避しています。`Console.ReadLine()` を使うので、コンソールの
行編集・IME はそのまま使えます。

`TAU_DEBUG=1` で入力の文字数とバイト数が確認できます。

```
>   [input] length=128 bytes=256
```



```
chcp                          ← 現在のコードページを確認（932 など）
--config set inputEncoding shift-jis  ← 932 のとき
--config set inputEncoding utf-8      ← 65001（UTF-8）のとき
```

### ストリーミング表示

LLM からの応答はストリーミングで受信します。コンソールでは本文をバッファし、
受信完了後に Markdown として整形表示します。`emitReasoning` が有効なら
**思考（reasoning_content）** は受信中に表示します。
Web UI では本文も逐次表示します。

ANSI エスケープが使える環境（Windows 10 以降のコンソール、Unix 系のターミナル）では色付きで表示し、
リダイレクト時や `NO_COLOR` 指定時は素のテキストになります。

### ツール実行（Function Calling）

次のツールが使えます。

| ツール名 | 説明 | 引数 |
|---|---|---|
| `shell` | コマンドを実行（Windows は `cmd.exe`、Unix 系は `/bin/sh`） | `cmd` |
| `read_file` | ファイル内容を読み取る | `path` |
| `read_document` | PDF / Word / Excel の本文を読む | `path`, `sheet`?, `maxRows`?, `offset`?, `limit`? |
| `write_file` | ファイルへ書き込む | `path`, `content` |
| `list_dir` | ディレクトリ一覧を返す | `path`, `pattern`? |
| `curl` | HTTP/HTTPS リクエスト送信（プロキシ設定を反映） | `url`, `method`?, `headers`?, `body`? |
| `memory_read` | メモリを読む | なし |
| `memory_write` | メモリを置き換える | `text` |
| `memory_append` | メモリに追記する | `text` |
| `memory_clear` | メモリを消す | なし |

ツール実行は `▶ Tool: <name>  <detail>` として 1 行で表示されます。
再起動して履歴を表示するときも、同じ形式で詳細まで表示します
（引数は直前の `tool_calls` から呼び出し ID で復元します）。
旧形式の `steps` だけが残る履歴も表示します。同じターンで既に表示した実行記録と
一致する `steps` だけを省くため、旧形式と新形式が混在しても記録は失われません。
本文や結果全体ではなく、コマンド・パス・URL などの詳細を表示します。

### PDF / Word / Excel の読み取り（`read_document`）

Office をインストールしていなくても読めます。COM は使いません。

解析は **`tau.exe` に内蔵**されています。`pdf2text` などを別の exe で
配布する必要はありません。

以前は `pdf2text.exe` / `word2text.exe` / `excel2text.exe` の 3 つの
補助 exe を子プロセスで呼んでいました。しかし exe ごとに .NET ランタイムを
抱えるため、4 つの exe で合計 150MB を超えていました。
いまはパーサを `tau.exe` に含めて **1 つの exe(約 16MB)** で動きます。

`tau.exe` には補助ツールと同じ動作をするサブコマンドがあります。

```
tau --pdf2text   report.pdf               テキストを出力
tau --pdf2text   --json report.pdf        JSON で出力
tau --pdf2text   --describe report.pdf    構造とフォントを調べる(原因の切り分け)

tau --word2text  memo.docx
tau --excel2text --sheet 売上 --rows 50 sales.xlsx
tau --excel2text --json sales.xlsx
```

`--describe` は構造とフォントの構成を出します。復号できない PDF の
原因を切り分けるのに使います。

```
tau --pdf2text --describe report.pdf
size=9858475  version=1.7  objects=950  objstm=872  encrypted=no  pages=268  contents=260  decoded=260  filters=[FlateDecode]
  fonts=4  undecodable=1
    x12 Ryumin [Type0] enc=Identity-H order=Japan1 twoByte=yes toUnicode=no differences=no fontFile2=no fontFile3=yes japan1=yes mapped=0
    x3  Gothic [Type0] enc=Identity-H order=Identity twoByte=yes toUnicode=no differences=no fontFile2=no fontFile3=no japan1=no mapped=0 UNDECODABLE
```

各行の意味です。

| 項目 | 意味 |
|---|---|
| `enc` | `/Encoding`（`Identity-H` なら 2 バイトの CID） |
| `order` | `/CIDSystemInfo` の `Ordering`（`Japan1` なら標準表で復元できる） |
| `toUnicode` | `/ToUnicode` があるか（あれば必ず復元できる） |
| `differences` | `/Encoding /Differences` のグリフ名があるか |
| `fontFile2` / `fontFile3` | 埋め込みフォント（TrueType / CFF）があるか |
| `japan1` | Adobe-Japan1 の標準表を使うか |
| `mapped` | 復元できた文字コードの数 |
| `UNDECODABLE` | 対応表が無く、文字に戻せない |

`undecodable` は「文字に戻せないフォントの数」です。1 つでも復号できる
フォントがあれば、読めた分は返します。以前は 1 つでも復号できない
フォントがあると PDF 全体を読めない扱いにしていたため、
`WinAnsiEncoding` の英字フォントが混ざっているだけで失敗していました。

引数と出力は `pdf2text.exe` などとまったく同じです。終了コードも同じで、
成功が 0、読めなかったのが 1、使い方の誤りが 2 です。

補助ツールを単体の exe として使いたい場合だけ、`build-tools.bat` で
ビルドできます。

```
build-tools.bat                     dist-tools に 3 つの exe を作る
pdf2text report.pdf                 同じ動作(tau --pdf2text と同じ)
```

| 形式 | 読み取り方法 | 取り出せるもの |
|---|---|---|
| PDF (`.pdf`) | PDF の構文を自前で解析 | テキスト（下記の形式に対応） |
| Word (`.docx`) | ZIP 内の `word/document.xml` | 本文・見出し・表（セルはタブ区切り） |
| Excel (`.xlsx`) | ZIP 内の `xl/*.xml` | 各シートのセル（タブ区切り、共有文字列対応） |

PDF は次の形式に対応しています。

| 項目 | 対応 |
|---|---|
| 圧縮 | FlateDecode / ASCII85 / ASCIIHex / RunLength / LZW（連鎖も可） |
| 圧縮（PNG） | `/Predictor` 10〜15（xref ストリームで使われる） |
| 文書構造 | オブジェクトストリーム（`/ObjStm`）・xref ストリーム（`/XRef`） |
| フォント | CID フォント（`Identity-H`/`Identity-V`）+ `ToUnicode` CMap |
| フォント（代替） | `/Encoding /Differences` のグリフ名、埋め込み TrueType の `cmap` |
| フォント（CID） | Adobe-Japan1 の CID → Unicode 標準表（`Ordering (Japan1)`） |
| フォント（1バイト） | `WinAnsiEncoding` / `MacRomanEncoding` などの標準エンコーディング |
| タグ付き PDF | `/ActualText` があればその文字を使う |
| フォーム | `AcroForm` のフィールド値（入力欄だけの PDF） |
| 暗号化 | 検出して「暗号化されている」と返す（復号はしない） |

読めないときは理由を返します。

| 症状 | 返るメッセージ |
|---|---|
| 画像だけ | `This PDF may be a scan (image only) and needs OCR.` |
| パスワード保護 | `this PDF is encrypted (password protected)` |
| フォントに対応表がない | `the font has no ToUnicode map and no embedded cmap` |

- 旧形式の `.doc` / `.xls` / `.ppt` は非対応です。PDF か `.docx` / `.xlsx` で保存し直してください。
- Excel は既定で各シート先頭 200 行までです。`sheet` でシートを指定できます。

#### 大きな PDF

35MB・368 ページ・4292 オブジェクトの書籍 PDF を 0.5 秒で読みます。
以前はオブジェクトごとに全文を文字列化していたため、応答が返りませんでした。

#### 大きな文書（コンテキストを超えるもの）

長い文書でもコンテキストを溢れさせません。1 回の結果は 16000 文字までで、
残りは `truncated=true` と `nextOffset` として返します。

```
{"ok":true,"length":400000,"offset":0,"returned":16000,"content":"...","truncated":true,"nextOffset":16000}
```

エージェントは `offset` を進めて続きを読みます。

```
  ▶ Tool: read_document  big.xlsx
  ▶ Tool: read_document  big.xlsx  @16000
```

ツール反復が上限に達してコンパクションが走っても、読み取り位置は
「続きを」の指示に引き継がれるため、最初から読み直しません。
Excel は `maxRows` で行単位に、`sheet` でシート単位に読むこともできます。

```
  ▶ Tool: read_document  report.pdf
  ▶ Tool: read_document  sales.xlsx  [売上]
```

---

## メモリ機能

**システムプロンプトとは別に、エージェントが自分で読み書きする覚え書き**です。
すべてのセッションで共通なので、別の会話で決めたことを次の会話へ引き継げます。

| 項目 | 内容 |
|---|---|
| 保存先 | `%APPDATA%\tau-cli\memory.json`（Linux は `~/.config/tau-cli/memory.json`） |
| 容量 | `memoryMaxChars`（既定 **8000 文字**） |
| 操作 | エージェントが `memory_read` / `memory_write` / `memory_append` / `memory_clear` で行う |

### どう動くか

1. メモリの内容は**毎回システムプロンプトに含めて** LLM へ渡します
   （`[Memory]` セクションとして末尾に付きます）
2. エージェントは必要と判断したとき、自分でメモリを更新します
   （「覚えておいて」と言われたときなど）
3. 更新はすぐに `memory.json` へ保存され、次のターンから反映されます

```
> このプロジェクトはタブ幅 4 で、C# を使っています。覚えておいて
  ▶ Tool: memory_append
  · 覚えました。

（別のセッションを開く）
> このプロジェクトの規約は？
  タブ幅 4 で C# ですね。
```

### 容量の制限

`memoryMaxChars` を超える書き込みは**拒否**されます。

```json
{"error":"memory is too long (200 chars, limit 100). Shorten it or raise memoryMaxChars."}
```

上限は変更できます。

```
tau.exe --config set memoryMaxChars 4000
```

### セッションとの違い

| | メモリ | セッション |
|---|---|---|
| 範囲 | **全セッション共通** | セッションごと |
| 内容 | エージェントが選んだ要点だけ | 会話そのもの |
| 削除 | メモリツールで削除・置換するまで残る | `/delete` で消える |
| 容量 | 上限あり（既定 8000 文字） | 上限なし（自動で要約） |

## 通信とコンテキスト管理

### TLS（HTTPS 接続）

現在の `tau` は **.NET 10 の `HttpClient` / `SocketsHttpHandler`** を使います。
`LlmClient.PreferredTls` は **TLS 1.2 と TLS 1.3** を指定し、`ApplyTlsTo()` で
ハンドラへ適用します。実際の接続で使われるバージョンは OS と接続先に依存します。

旧 .NET Framework / Mono 向けの TLS 1.2 固定設定とは異なります。
接続できない場合は `tau --test` で通信診断を行ってください。

### 一時的な失敗の自動再試行

接続が一時的に失敗することがあるため、**最大 3 回**まで自動で再試行します
（0.5 秒 → 1 秒 のバックオフ）。

```
  · 接続を再試行します(1/2)…
  · 接続を再試行します(2/2)…
再試行で成功
```

| 再試行する | 再試行しない |
|---|---|
| `SecureChannelFailure`（一時的なことも多い） | `ProtocolError`（HTTP 401/429/500 など） |
| `ConnectionClosed` / `KeepAliveFailure` | `TrustFailure`（証明書の問題） |
| `ReceiveFailure` / `SendFailure` / `Timeout` / `ConnectFailure` | |

HTTP エラーや証明書エラーは、繰り返しても結果が変わらないため再試行しません。

### デバッグと接続診断

うまく接続できない場合は、**接続診断**を実行してください。

```
tau.exe --test              # 接続診断 + 自己診断(API リクエストを含む)
tau.exe --test=offline      # ネットワークを使わない
```

環境情報（.NET / Mono / OS / 既定の TLS）、接続先の DNS、**TLS の各バージョンでの接続可否**、
実際の API 呼び出し結果を表示します。そのあとに自己診断（エンコーディング・TLS）が続きます。

```
  [TLS の接続試験]
    (TCP 接続 + TLS ハンドシェイクのみを試します。HTTP は送りません)
    via proxy: proxy.example.com:8080 (CONNECT api.example.com:443)

    SystemDefault : NG  AuthenticationException: ... <- ...CERTIFICATE_VERIFY_FAILED
    Tls12         : OK  Tls12 / Aes128  (via proxy)
    Tls11         : NG  AuthenticationException: ... <- ...TLSV1_ALERT_PROTOCOL_VERSION
```

TLS の可否は **TCP 接続 + TLS ハンドシェイクだけ**で判定します（HTTP は送りません）。
`HEAD` を使うと HEAD 非対応のサーバで `501` が返り、TLS が使えるのに NG と誤表示するためです。

#### プロキシ環境での TLS 試験

社内ネットワークなどで外向きの 443 が遮断されている場合、プロキシを経由しないと
すべての TLS 試験が**タイムアウト**します。`tau` はプロキシ設定を読んで、
プロキシへ `CONNECT` してから TLS を始めます。

- 明示設定 (`proxyUrl`) があればそれを使います。
- `useSystemProxy` が有効なら、システム既定のプロキシを使います。
- 認証が必要なプロキシには `Proxy-Authorization: Basic` を送ります
  （`proxyUser` / `proxyPass`）。
- 407 が返った場合は、認証情報の設定を促すメッセージを出します。

TLS 試験の表示は `via proxy: <host>:<port>` または `direct: <host>:<port>` で、
どちらの経路を使ったかが分かります。

#### 出力の見方

`--test` は 3 種類の試験を行います。

| 試験 | 内容 |
|---|---|
| **TLS の接続試験** | TCP + TLS ハンドシェイクのみ（HTTP は送らない） |
| **API 呼び出し試験** | 非ストリーミング（`CallOnce`）で実際に API を呼ぶ |
| **ストリーミング呼び出し試験** | 対話と同じ経路（`CallStream` / `Accept: text/event-stream`） |

| 表示 | 意味 |
|---|---|
| **API 呼び出し試験が OK** | 接続・認証は正常（これが最終判定） |
| API は OK だがストリーミングが NG | ストリーミング固有の問題（プロキシが SSE を妨害等） |
| `TLSV1_ALERT_PROTOCOL_VERSION` | サーバがその TLS バージョンを受け付けない |
| `CERTIFICATE_VERIFY_FAILED` | 証明書の検証に失敗（社内プロキシの傍受など） |
| `SslProtocolType` エラー | .NET がその値（`SystemDefault` 等）を解釈できない |
| すべてタイムアウト | 外向きの接続が遮断されている。プロキシ設定を確認する |
| `proxy CONNECT failed: 407` | プロキシが認証を要求している。`proxyUser` / `proxyPass` を設定する |
| `501 実装されていません` | HTTP メソッド（HEAD）が未対応 |

> `Proxy` の表示が接続先 URL と同じ場合は、**プロキシを経由せず直接接続**しているという意味です
> （`IsBypassed=true`）。`tau` はその場合「直接接続(プロキシを経由しない)」と表示します。

**`API 呼び出し試験` が `OK` なら、`tau` の接続は正常です。**
TLS の接続試験が NG でも、それは試験方法の差であり、
実際の `HttpClient` のリクエストは別経路で成功している場合があります。

さらに詳しく見たい場合は**デバッグ出力**を有効にします。

```
tau.exe --debug            (または環境変数 TAU_DEBUG=1)
```

リクエストの設定（TLS・プロキシ・HTTP バージョン・タイムアウト）、応答ヘッダ、
SSE の先頭行、例外の連鎖、再試行の判断が `stderr` に表示されます。

```
[debug] TLS: SystemDefault(0) -> Tls12(3072)
[debug]   URL      : https://...
[debug]   Proxy    : null(直接接続)
[debug]   HTTP     : 1.1
[debug] WebException: status=TrustFailure
[debug]   連鎖: System.Net.WebException: ... <- AuthenticationException: ...
```

エラー時は `TAU_DEBUG=1` の案内も表示されます。

### 通信エラーの表示

エラーは原因が分かる形で表示します。HTTP エラーの場合は**ステータスコードと本文**を、
TLS などの内部エラーは**内部例外のメッセージ**も併記します。

```
⚠ エラー: LLM API(stream)エラー: The remote server returned an error:
(401) Unauthorized. (status=ProtocolError) [HTTP 401]
{"error": {"message": "Invalid API key"}}
```

**応答が途中で切断された場合**（`[DONE]` を受け取らずにストリームが終わった場合）は、
途中までの本文を確定させずにエラーとして報告します。

```
⚠ エラー: LLM API(stream)エラー: 応答が途中で切断されました(受信済み 42 文字)。
  もう一度お試しください。
```

> `受信時に予期しないエラーが発生しました` のようなメッセージは、
> ストリーム読み取り中の切断（ネットワーク/プロキシのタイムアウト、サーバ側の応答中断）で発生します。
> `tau` は `HttpClient` を再利用し、`Expect: 100-continue` を無効化しています。

### コンテキストの自動整理

会話が `contextWindow - reserveTokens` を超えると、古い部分を LLM で要約し、
`summary` と `contextStart` を更新します（`agentweb` と同じ方式）。
要約はセッションに保存されるため、再開しても引き継がれます。

### ツール反復の上限に達したとき

1 ターンでツールを呼べる回数は `MAX_TURNS`（10 回）までです。
以前は上限に達すると

```
⚠ Error: The LLM did not produce a final answer (too many tool iterations)
```

で打ち切っていましたが、長い作業が途中で切れてしまいます。
現在は**コンテキストを要約して続行**します。

```
  · Compacting context after 10 tool iterations (round 1/2)...
  （続きを求めるメッセージを送り、処理を再開）
```

- 要約は最大 2 回まで（`MAX_COMPACT_ROUNDS`）
- それでも終わらない場合は、従来どおりエラーにします（無限ループ防止）
- 要約対象が空にならないよう、適切な切断点が無い場合は
  直近 4 件を残してそれ以前をまとめます

---

## テスト

### `dotnet test`（単体・結合テスト）

```
dotnet test tests/tests.csproj
```

xUnit による単体テストと、モック LLM を使う結合テストです。
.NET SDK 10 だけで動きます（`mono` 不要）。外部 LLM の API キーは不要です。
ツールのテストは表示だけでなく、標準出力・終了コード・API に返す呼び出し ID も検証します。

### テストの種類

テストは **2 つ**だけです。

| テスト | 実行 | 用途 |
|---|---|---|
| `dotnet test` | `dotnet test tests/tests.csproj` | 開発・CI。**すべての検証** |
| `tau.exe --test` | `tau.exe --test` | ユーザーの実機診断（下記） |

以前あった `test_tau.py`（Python）は廃止し、内容をすべて `dotnet test` に移しました。
Python は不要です。

### 単体テストの内訳

| ファイル | 内容 |
|---|---|
| `tests/JsonTests.cs` | JSON のパース・シリアライズ・往復・厳格性 |
| `tests/MarkdownTests.cs` | 表示幅・記号除去・表・コードブロック・識別子の `_` |
| `tests/ConfigTests.cs` | 設定の保存・読み込み・未知キーの保持・移行 |
| `tests/SessionStoreTests.cs` | セッションの保存・一覧・削除・`lastSession` |
| `tests/WebServerTests.cs` | Web サーバの起動・停止・イベント名・`ConsoleUi` 差し替え |
| `tests/ToolDisplayTests.cs` | 実行時・履歴共通のツール詳細書式、不正な引数への耐性 |
| `tests/ToolExecutorTests.cs` | 空白・引用符を含むシェルコマンド、標準出力・標準エラー・終了コード |
| `tests/DocumentReaderTests.cs` | PDF / Word / Excel の抽出・圧縮・UTF-16・表・シート選択・非対応形式 |
| `tests/DocumentSizeTests.cs` | 大きな文書の分割読み取り・続きの位置・コンテキストへの収まり |
| `tests/PdfStructureTests.cs` | ObjStm / XRef ストリーム・各種フィルタ・CID フォント・暗号化 |
| `tests/DiagnosticsProxyTests.cs` | プロキシ経由の TLS ハンドシェイク・CONNECT・認証・経路選択 |
| `tests/TauRunnerTests.cs` | テスト用 DLL がフレームワーク依存であること |

### 実行時テスト（tau をプロセスとして起動）

`tests/MockLlm.cs` がモック LLM サーバ、`tests/TauRunner.cs` が tau の起動役です。
`tests.csproj` がテスト実行前にテスト用のフレームワーク依存ビルドを
`tests/tau-fx/` へ用意します。

| ファイル | 内容 |
|---|---|
| `tests/ChatIntegrationTests.cs` | ストリーミング・ツールループ・セッション・エラー処理 |
| `tests/CommandIntegrationTests.cs` | `/tools` `/open` `/delete` `/clear`・廃止コマンドの拒否 |
| `tests/EncodingIntegrationTests.cs` | 日本語・NUL・長文入力・`--test` |
| `tests/WebIntegrationTests.cs` | `--web` の画面・SSE・CSRF 拒否・404 |
| `tests/OutputIntegrationTests.cs` | 改行・進行状況・データ移行・デバッグ出力 |
| `tests/MultiLineInputTests.cs` | パイプ入力の複数行・コマンドでの区切り |
| `tests/ExecuteOptionTests.cs` | `-e` の実行・セッション非保存・出力の整形 |
| `tests/CompactionTests.cs` | ツール反復上限でのコンパクションと続行 |
| `tests/MemoryTests.cs` | メモリの保存・容量制限・システムプロンプトへの埋め込み |
| `tests/HistoryDisplayTests.cs` | 再起動後のツール詳細・旧形式との混在・重複防止・不完全な履歴 |

```
Passed!  - Failed: 0, Passed: 572, Skipped: 0, Total: 572
```

所要時間は約 30 秒です。

### `tau.exe --test`（実機診断）

配布バイナリに同梱されています。**そのマシンでしか確かめられない致命的な項目**
だけを確認します。

```
tau.exe --test              # ネットワークも確認
tau.exe --test=offline      # ネットワークを使わない
```

| セクション | 内容 |
|---|---|
| 1. environment | OS・入出力エンコーディングの一致 |
| 2. console line-input | Ctrl-V 貼り付けが効く状態か（Windows） |
| 3. input encoding | 実際のコンソールで日本語が往復するか |
| 4. input buffer | 254 文字の壁がないか |
| 5. TLS | TLS 1.2 以上が有効で、ハンドラに適用できるか |

```
Result: 18 passed, 2 skipped
```

単体テストで確認できるものは `dotnet test` に移してあるので、ここは最小限です。

## 開発ルール

### `dotnet format` で整形してからコミットする

ソースコードは **`dotnet format` で整形した状態**を保ちます。
コミット前に必ず実行してください。

```
dotnet format tau-cli.csproj
dotnet build tau-cli.csproj -c Release
dotnet test tests/tests.csproj
```

CI でも `--verify-no-changes` で整形済みかを検査します。整形が崩れていると
**ビルドジョブが失敗**します。

```
dotnet format tau-cli.csproj --verify-no-changes
```

### コミット前のチェックリスト

1. `dotnet format` で整形
2. `dotnet build -c Release` が **警告 0・エラー 0**
3. `dotnet test` がすべて通る
4. `tau.exe --test=offline` がすべて通る（実機診断）

## CI / リリース

GitHub Actions（`.github/workflows/build.yml`）で次を実行します。

| ジョブ | 内容 |
|---|---|
| `test` | `dotnet test` を Ubuntu / Windows で実行 |
| `selftest` | ビルドした `tau` で `--test=offline` を実行 |
| `build` | Windows (x86 / x64) 向けに単一ファイルを publish |
| `release` | タグ push 時に GitHub Release を作成 |

### 配布物

`v*` タグを push すると、以下が Release に添付されます。

```
tau-win-x86.zip   32bit Windows 向け
tau-win-x64.zip   64bit Windows 向け
```

どちらも**自己完結の単一ファイル**（`tau.exe` 1 個だけ）です。
.NET ランタイムも SDK も**インストール不要**で、展開してそのまま実行できます。

```
git tag v1.0.0
git push origin v1.0.0
```

### どちらを選ぶか

| 環境 | 使うファイル |
|---|---|
| 64bit Windows (10 / 11) | `tau-win-x64.zip` |
| 32bit Windows | `tau-win-x86.zip` |

迷ったら `tau-win-x64.zip` を選んでください。64bit Windows 上では
x64 版の方がわずかに高速です。`--version` の `runtime:` 行で
どちらが動いているか確認できます。

```
tau 2026-11-23 dotnet10+json
  exe   : C:\work\tau.exe
  built : 2026-11-23 11:32:18 UTC
  runtime: 10.0.15 (win-x64)
```

## JSON の扱い

`Json` は **.NET 標準の `System.Text.Json`** を使います（`src/Json.cs`・約 200 行）。

| 用途 | 使う API |
|---|---|
| 読み込み | `JsonDocument.Parse`（厳格モード） |
| 書き出し | `Utf8JsonWriter` |
| 内部表現 | `Dictionary<string, object>` / `List<object>`（従来どおり） |

追加パッケージは不要です（`System.Text.Json` は .NET に同梱）。

### 内部表現を変えなかった理由

`JsonNode` に置き換えると、`Dictionary<string, object>` を使う
**138 箇所の型宣言と 179 箇所の呼び出し**をすべて書き換える必要があります。
一方、`JsonDocument` / `Utf8JsonWriter` は同じ内部表現のまま使えるため、
**呼び出し側を 1 行も変えずに**標準ライブラリへ移行できました。

`JsonTests` でパース・書き出し・型変換の互換性を検証しています。

### 厳格モードにしている理由

`JsonDocument.Parse` は既定でも**コメント・末尾カンマ・単一引用符**を拒否します。
`tau` ではコメント・末尾カンマの拒否を明示し、最大深度を 256 に設定しています。
壊れた API 応答は解析時にエラーにします。

```csharp
new JsonDocumentOptions {
    CommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    MaxDepth = 256,
}
```

### 日本語をエスケープしない理由

既定のエンコーダは `あ` を `\u3042` に変換します。それだと設定ファイルや
セッションが読めなくなるため、`UnsafeRelaxedJsonEscaping` を使います。

出力先は LLM へのリクエストとローカルの設定/セッションだけで、
HTML へ埋め込むことはないので安全です。

### 挙動の変化（意図的なもの）

| 入力 | 旧 | 新 |
|---|---|---|
| `NaN` / `Infinity` | `NaN`（不正な JSON） | `null`（例外で落とさない） |
| `{"a":}` | 例外 | 例外（同じ） |
| コメント・末尾カンマ | 一部受理 | すべて拒否 |
| `Json.AsInt("1.9")` | `0` | `1` |
| BOM 付き | 受理 | 受理（取り除いてから解析） |

## ファイル構成

```
tau-cli/  (= リポジトリ直下)
  src/              ソース（クラスごとに 1 ファイル）
    Program.cs              エントリポイント・CLI 引数・コンソール設定
    Config.cs               設定（tau.json）と環境変数
    Json.cs                 JSON の解析・生成（System.Text.Json 経由）
    SessionStore.cs         セッションの保存・読み込み・削除
    Memory.cs               メモリ（全セッション共通の覚え書き）
    ProxyHelper.cs          プロキシ設定
    LlmResp.cs              LLM 応答の入れ物
    LlmCancelledException.cs 中断を表す例外
    LlmClient.cs            HTTP 通信（ストリーミング・再試行・TLS）
    ToolExecutor.cs         ツール定義と実行
    ToolDisplay.cs          ツール詳細と保存用 steps の共通書式
    pdf2text/               PDF の解析とテキスト抽出
    word2text/              Word の読み取り
    excel2text/             Excel の読み取り
    SessionCtx.cs           セッションの状態
    Agent.cs                エージェント本体（ターン実行・要約）
    ConsoleUi.cs            色付き出力の補助（出力先は差し替え可能）
    Chat.cs                 対話ループ・コマンド処理
    WebServer.cs            --web の HTTP サーバ（SSE・最小 API）
    www/index.html          --web の画面（埋め込みリソース）
    Diagnostics.cs          接続診断（--test から呼ばれる）
    Settings.cs             設定の表示・変更（--config）
    LineEditor.cs           入力（行編集・コンソールモード・クリップボード）
    MarkdownRenderer.cs     Markdown の整形表示
    SelfTest.cs             --test 自己診断テスト
  build.bat         ビルド（Windows / CP932・CRLF）
  build-tools.bat   補助ツール単体のビルド（通常は不要）
  run.bat           起動
  tests/            dotnet test 用のテスト(xunit)
    MockLlm.cs / TauRunner.cs   実行時テストの補助
  .github/workflows/build.yml  CI(テスト・自己診断・リリース)
  diag_input.bat    入力まわりの診断
  README.md         このファイル
```

### クラスの役割

| クラス | 役割 |
|---|---|
| `Program` | エントリポイント、CLI 引数、コンソールのエンコーディングとバッファ設定 |
| `Config` | `tau.json` と環境変数から設定を読み、未知のキーを保持して保存 |
| `Json` | .NET 標準の System.Text.Json による JSON 解析・生成 |
| `SessionStore` | `%APPDATA%\tau-cli\sessions` への保存・読み込み・削除 |
| `LlmClient` | HTTP 通信、SSE ストリーミング、TLS、再試行、キャンセル |
| `ToolExecutor` | ツールの定義と実行（ファイル操作・シェルなど） |
| `Agent` | 1 ターンの実行、ツール呼び出しのループ、文脈の圧縮 |
| `Chat` | 対話ループ、コマンド（`/help` `/clear` `/tools` など） |
| `ConsoleUi` | 色付き・太字・淡色の出力 |
| `LineEditor` | 入力の読み取り、コンソールの行編集モード、クリップボード |
| `MarkdownRenderer` | 見出し・太字・表などを ANSI と罫線で整形 |
| `Settings` | 設定の表示・変更（`--config show/set/edit`） |
| `Diagnostics` | 接続診断（`--test` から呼ばれる） |
| `SelfTest` | `--test` の自己診断テスト |

## リポジトリ構成

```
tau/                 ← このリポジトリ
├── src/             ソース（クラスごとに 1 ファイル）
│   └── www/         --web の画面（実行ファイルに埋め込む）
├── build.bat / run.bat / tau-cli.csproj
└── tests/           dotnet test 用の単体テスト
```
