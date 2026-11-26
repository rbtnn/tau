// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  agentweb (Web UI 版) とは独立した実装です。
//  コードは agentweb を参考にしていますが、共通化はしていません。
//  HTTP サーバ / Web UI を持たず、標準入出力だけで対話します。
//
//  ビルド: csc.exe /nologo /out:tau.exe src\*.cs
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tau
{
    class Chat
    {
        readonly Config cfg;
        readonly SessionStore store;
        readonly Agent agent;

        SessionCtx ctx;
        bool cancelled;          // 現在のターンを中止するか
        bool ctrlCPressed;       // Ctrl-C が押されたか
        bool waitingForInput;    // 入力待ちの最中か

        public Chat(Config cfg)
        {
            this.cfg = cfg;
            this.store = new SessionStore();
            this.agent = new Agent(cfg);
        }

        // ---------------- セッションの読み込み ----------------

        // resume=true なら前回のセッションを開き、false なら必ず新規作成する。
        SessionCtx LoadOrCreate(string wantId, bool resume)
        {
            string id = "";
            if (wantId != null && wantId.Trim().Length > 0) id = wantId.Trim();
            // 再開指定のときだけ「最後に開いていたセッション」を使う。
            // /new では使わない(使うと直前と同じセッションが開き直されてしまう)。
            if (id.Length == 0 && resume) id = store.LastSessionId();

            if (id.Length > 0)
            {
                Dictionary<string, object> meta = store.Load(id);
                if (meta != null) return FromMeta(id, meta);
            }

            // 新規作成
            SessionCtx c = new SessionCtx();
            c.Id = store.NewId();
            c.Title = "New conversation";
            // -e / --execute ではディスクに書かない
            if (!noSave)
            {
                store.Save(c.Id, c.Messages, c.Title);
                store.SetLastSessionId(c.Id);
            }
            return c;
        }

        SessionCtx FromMeta(string id, Dictionary<string, object> meta)
        {
            SessionCtx c = new SessionCtx();
            c.Id = id;
            c.Title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "New conversation";
            c.Summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
            c.ContextStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
            if (meta.ContainsKey("messages") && meta["messages"] is List<object>)
                c.Messages = (List<object>)meta["messages"];
            return c;
        }

        void Save(SessionCtx c)
        {
            if (noSave) return;
            store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
        }

        // 最初のユーザー発言からタイトルを作る
        void AutoTitle(SessionCtx c, string userMessage)
        {
            if (c.Title != null && c.Title != "New conversation" && c.Title.Trim().Length > 0) return;
            string t = (userMessage ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return;
            if (t.Length > 40) t = t.Substring(0, 40) + "…";
            c.Title = t;
        }

        // ---------------- 実行 ----------------

        // -e / --execute のときは true。セッションに書き込まない。
        // スクリプトから何度も呼ばれる用途を想定し、履歴を残さない。
        bool noSave;

        // 1 回だけ実行して終わる(-e / --execute)。
        // 対話ループには入らない。テストやスクリプトから使える。
        public void RunOnce(string sessionArg, bool forceNew, string prompt)
        {
            noSave = true;

            string openId = (sessionArg != null && sessionArg.Trim().Length > 0)
                ? ResolveSessionId(sessionArg) : "";
            ctx = forceNew ? LoadOrCreate("", false) : LoadOrCreate(openId, true);

            // バナーは出さない(出力をそのままパイプで使えるようにする)
            Turn(prompt);
            // 保存はしない(noSave により Turn 内の Save も抑止される)
        }

        public void Run(string sessionArg, bool forceNew = false)
        {
            // --open は番号でも ID でも受け付ける(/open と同じ規則)
            string openId = (sessionArg != null && sessionArg.Trim().Length > 0)
                ? ResolveSessionId(sessionArg) : "";

            // --new: 必ず新しいセッションを作る
            ctx = forceNew ? LoadOrCreate("", false) : LoadOrCreate(openId, true);
            bool resumed = ctx.Messages.Count > 0;
            ConsoleUi.Banner(cfg, ctx, resumed);

            if (resumed) PrintHistory(ctx);

            // Ctrl-C はプロセスを終了させず、現在のターンだけ中止する。
            // e.Cancel = true で既定の終了動作を抑止する。
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;          // プロセスを終了しない
                ctrlCPressed = true;
                cancelled = true;         // 実行中のターンを中止する
                LlmClient.Cancel();       // 通信中なら HTTP リクエストを中断する

                // 入力待ちの途中なら、すぐ改行してプロンプトを出し直す
                if (waitingForInput)
                {
                    try { Console.Write("\n"); } catch { }
                }
            };

            int nulLost = 0;   // 連続して入力が NUL で失われた回数

            // 入力がリダイレクトされている場合(パイプ・ファイル)は、
            // 残りをまとめて読んでおく。1 行ずつ読むと、ソースコードを
            // 貼り付けたときに 1 行ずつ別メッセージとして送られてしまう。
            bool inputRedirected = false;
            try { inputRedirected = Console.IsInputRedirected; }
            catch { }

            // パイプ入力のときは、読みすぎた 1 行を押し戻せるようにする。
            // (/clear などの確認プロンプトが同じ stdin から読むため、
            //  先読みしすぎると確認に答えられなくなる)
            pushedBack = null;

            while (true)
            {
                string line;
                ctrlCPressed = false;
                if (LlmClient.DebugOn)
                    ConsoleUi.Dim("  [waiting for input]\n");

                // 入力の前に罫線を引く。応答との境目が分かりやすくなる。
                // 入力がリダイレクトされている(パイプ)ときは、人が見ないので
                // 線は引かない。
                if (!inputRedirected) ConsoleUi.RuleTop();

                waitingForInput = true;
                try
                {
                    if (inputRedirected)
                    {
                        // パイプ入力: コマンド行でない行が続く限り 1 メッセージに
                        // まとめる。確認プロンプトが続けて読めるよう、余分に
                        // 読んだ 1 行は押し戻す。
                        line = ReadPipedMessage();
                        if (line == null) { /* EOF */ }
                        else
                        {
                            ConsoleUi.Accent("> ");
                            Console.WriteLine(line);
                        }
                    }
                    else
                    {
                        // 罫線は入力の前に上下とも出しておく。
                        // 下線は LineEditor が出すので、ここでは出さない。
                        line = LineEditor.ReadLineColored("> ", "36", true);
                    }
                }
                catch (Exception ex)
                {
                    waitingForInput = false;
                    // 入力の読み取りで例外が出たら、黙って終了せず原因を出す
                    ConsoleUi.Danger("\n⚠ Failed to read input: " + ex.GetType().Name
                        + ": " + ex.Message + "\n");
                    if (LlmClient.DebugOn)
                        ConsoleUi.Dim("  " + ex.ToString().Replace("\n", "\n  ") + "\n");
                    break;
                }
                waitingForInput = false;

                // 罫線は入力の前に引いてあるので、ここでは何もしない。
                // (パイプ入力は人が見ないので線を引かない)

                if (LlmClient.DebugOn)
                    ConsoleUi.Dim("  [input] length=" + (line == null ? -1 : line.Length)
                        + " bytes=" + (line == null ? -1 : Encoding.UTF8.GetByteCount(line))
                        + (line == null ? " (null)" : "") + "\n");

                if (ctrlCPressed)
                {
                    // 入力待ちの途中で Ctrl-C: 入力行を捨ててプロンプトに戻る
                    ctrlCPressed = false;
                    cancelled = false;
                    ConsoleUi.Dim("  (cancelled input)\n");
                    continue;
                }

                if (line == null) break;   // Ctrl-G / Ctrl-D / EOF

                // 入力が NUL 文字に化けていないか確認する。
                // Windows のコンソールで入力エンコーディングが合っていないと、
                // 日本語が NUL になり、SanitizeInput で全て消えてしまう。
                // そのまま黙って再入力待ちにすると「応答が返らない」ように見えるため、
                // 何が起きたかを表示する。
                int nulCount = Program.CountNul(line);
                if (nulCount > 0 && nulCount >= line.Length)
                {
                    nulLost++;
                    ConsoleUi.Danger("⚠ The input was lost (all " + line.Length
                        + " characters became NUL).\n");
                    ConsoleUi.Dim("  The console input encoding does not match.\n");
                    ConsoleUi.Dim("  Try one of the following:\n");
                    ConsoleUi.Dim("   - Run `chcp 65001` before starting tau (or run it in Windows Terminal)\n");
                    ConsoleUi.Dim("   - Set TAU_DEBUG=1 and check the reported input encoding\n");
                    ConsoleUi.Dim("   - Paste the text from a file:  tau --session <id> < input.txt\n");
                    ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());

                    if (nulLost >= 2)
                    {
                        // 何度も失われる場合は、ここで打ち切って原因を伝える
                        ConsoleUi.Danger("  The input has been lost " + nulLost
                            + " times in a row; stopping here.\n");
                        ConsoleUi.Dim("  Change the console code page (chcp 65001) and start tau again,\n");
                        ConsoleUi.Dim("  or write your text to a file and pipe it in:\n");
                        ConsoleUi.Dim("    tau.exe < input.txt\n");
                        break;
                    }
                    continue;
                }
                nulLost = 0;

                if (nulCount > 0)
                {
                    // 一部だけ NUL に化けた場合も、欠落を伝える
                    ConsoleUi.Danger("⚠ " + nulCount + " character(s) of the input became NUL and were dropped.\n");
                    ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                }

                line = Program.SanitizeInput(line);

                // Ctrl-V が SYN(0x16)として届いた場合は、クリップボードの
                // 内容に置き換える(コンソールの行編集が使えない環境向け)。
                //
                // 改行はそのまま残す。ソースコードを貼り付けたときに
                // 1 行に潰れると読めなくなるため。複数行になった場合は
                // そのまま 1 つのメッセージとして送る。
                if (line.IndexOf('\u0016') >= 0)
                {
                    string clip = LineEditor.GetClipboardText();
                    if (clip == null || clip.Length == 0)
                    {
                        ConsoleUi.Danger("⚠ Ctrl-V: the clipboard is empty"
                            + " or could not be read.\n");
                        continue;
                    }

                    // 貼り付け位置に差し込む(前後に打った文字は残す)
                    line = line.Replace("\u0016", clip.TrimEnd('\r', '\n'));

                    // 何行貼り付けたかを知らせる
                    int lines = line.Split('\n').Length;
                    if (lines > 1)
                        ConsoleUi.Dim("  (pasted " + lines + " lines)\n");
                }

                line = line.Trim();
                if (line.Length == 0) continue;   // 空行は何もしない

                if (line.StartsWith("/"))
                {
                    if (HandleCommand(line)) continue;
                    break;   // /exit
                }

                Turn(line);
            }

            Console.WriteLine();
            ConsoleUi.Dim("Bye. Session: " + ctx.Id);
        }

        // パイプ入力から 1 つのメッセージを読む。
        //
        // コマンド行(/)に当たるまでを 1 メッセージにまとめる。
        // 先読みしすぎると /clear などの確認プロンプトが stdin から
        // 読めなくなるため、読みすぎた 1 行は pushedBack に戻して
        // 次回または確認プロンプトで使えるようにする。
        //
        // EOF なら null。
        // パイプ入力で読みすぎた 1 行。確認プロンプトもここから読む。
        string pushedBack;

        // パイプ入力から 1 行読む(押し戻しを考慮する)。
        // 確認プロンプト用。
        string ReadLineForPrompt(string prompt)
        {
            if (pushedBack != null)
            {
                string ln = pushedBack;
                pushedBack = null;
                Console.Write(prompt);
                Console.WriteLine(ln);
                return ln;
            }
            return LineEditor.ReadLine(prompt);
        }

        string ReadPipedMessage()
        {
            string first;
            if (pushedBack != null)
            {
                first = pushedBack;
                pushedBack = null;
            }
            else
            {
                try { first = Console.ReadLine(); }
                catch { return null; }
                if (first == null) return null;
            }

            // コマンド行なら 1 行だけ返す
            if (first.TrimStart().StartsWith("/")) return first;

            StringBuilder sb = new();
            sb.Append(first);

            while (true)
            {
                string ln;
                try { ln = Console.ReadLine(); }
                catch { break; }
                if (ln == null) break;

                // コマンド行に当たったら、そこで区切る。
                // この行は次の呼び出し(または確認プロンプト)で使う。
                if (ln.TrimStart().StartsWith("/"))
                {
                    pushedBack = ln;
                    break;
                }

                sb.Append('\n').Append(ln);
            }
            return sb.ToString();
        }

        // コマンド名と、省略したときに受け付ける最短の長さ。
        //
        // Vim のヘルプと同じ書き方で「/n[ew]」は /n でも /ne でも /new でも
        // 受け付ける、という意味。最短の長さより短いと曖昧なので拒否する。
        static readonly (string name, int minLen)[] COMMANDS =
        {
            ("new", 1),      // /n
            ("list", 1),     // /l
            ("open", 1),     // /o
            ("delete", 1),   // /d
            ("clear", 1),    // /c
            ("tools", 1),    // /t
            ("exit", 1),     // /e
            ("quit", 1),     // /q
            ("help", 1),     // /h
        };

        // 省略されたコマンド名を解決する。見つからなければ null。
        static string ResolveCommand(string cmd)
        {
            if (cmd.Length < 2 || cmd[0] != '/') return null;
            string name = cmd.Substring(1);

            // 完全一致を優先する
            foreach ((string n, int _) in COMMANDS)
                if (n == name) return n;

            // 前方一致で探す。
            //
            // 候補が 1 つだけなら、最短の長さに満たなくても解決する
            // (/s は /set しか候補が無いので /s でよい)。
            // 候補が複数あるときは、最短の長さを満たすものだけを残す。
            // 残った候補が 1 つなら解決、2 つ以上なら曖昧として拒否する。
            List<string> candidates = new();
            foreach ((string n, int _) in COMMANDS)
                if (n.StartsWith(name, StringComparison.Ordinal)) candidates.Add(n);

            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            List<string> allowed = new();
            foreach (string n in candidates)
            {
                int minLen = 1;
                foreach ((string cn, int cl) in COMMANDS)
                    if (cn == n) { minLen = cl; break; }
                if (name.Length >= minLen) allowed.Add(n);
            }

            return (allowed.Count == 1) ? allowed[0] : null;
        }

        // 前方一致する候補を、省略形で返す(例: "c" -> "/cl", "/co")。
        static string[] Suggestions(string prefix)
        {
            List<string> list = new();
            foreach ((string n, int minLen) in COMMANDS)
            {
                if (n.StartsWith(prefix, StringComparison.Ordinal))
                    list.Add("/" + n.Substring(0, minLen));
            }
            return list.ToArray();
        }

        // コマンドを処理する。true なら継続、false なら終了。
        bool HandleCommand(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = (parts.Length > 1) ? parts[1].Trim() : "";

            // /? だけは特別扱い(ヘルプ)
            if (cmd == "/?") { Program.PrintHelp(); return true; }

            // 省略形を解決する(/n -> new, /e -> exit など)
            string resolved = ResolveCommand(cmd);
            if (resolved != null) cmd = "/" + resolved;
            else if (cmd.Length > 1 && cmd[0] == '/')
            {
                // 解決できなかった(未知、または短すぎて曖昧)
                ConsoleUi.Danger("Unknown command: " + cmd + "\n");

                // 前方一致する候補があれば、必要な長さを案内する
                string[] hints = Suggestions(cmd.Substring(1));
                if (hints.Length > 0)
                {
                    ConsoleUi.Dim("  Did you mean: ");
                    for (int i = 0; i < hints.Length; i++)
                    {
                        if (i > 0) ConsoleUi.Dim(", ");
                        ConsoleUi.Accent(hints[i]);
                    }
                    ConsoleUi.Dim("\n");
                }
                ConsoleUi.Dim("  Use /help to list the commands\n");
                return true;
            }

            switch (cmd)
            {
                case "/exit":
                case "/quit":
                    return false;

                case "/help":
                    Program.PrintHelp();
                    return true;

                case "/clear":
                    ClearSession();
                    return true;

                case "/new":
                    ctx = LoadOrCreate("", false);   // 必ず新しいセッションを作る
                    ConsoleUi.Info("Started a new session: " + ctx.Id);
                    return true;

                case "/list":
                    ShowSessions();
                    return true;

                case "/delete":
                    DeleteSession(arg);
                    return true;

                case "/open":
                    if (arg.Length == 0)
                    {
                        ConsoleUi.Danger("Specify the session to open\n");
                        ConsoleUi.Dim("  Usage: /open <number|id>   (numbers come from /sessions)\n");
                        return true;
                    }
                    {
                        // /delete と同じく、番号でも ID でも開けるようにする
                        string openId = ResolveSessionId(arg);
                        if (openId.Length == 0)
                        {
                            ConsoleUi.Danger("No session with that number: " + arg + "\n");
                            ConsoleUi.Dim("  Check the numbers with /sessions\n");
                            return true;
                        }
                        Dictionary<string, object> meta = store.Load(openId);
                        if (meta == null) { ConsoleUi.Danger("No such session: " + arg + "\n"); return true; }
                        ctx = FromMeta(openId, meta);
                        store.SetLastSessionId(ctx.Id);
                        ConsoleUi.Info("Opened session: " + ctx.Id + "  (" + ctx.Title + ")");
                        PrintHistory(ctx);
                    }
                    return true;

                case "/tools":
                    cfg.AllowTools = !cfg.AllowTools;
                    cfg.Save();
                    ConsoleUi.Info("Tools: " + (cfg.AllowTools ? "on" : "off"));
                    return true;

                default:
                    ConsoleUi.Danger("Unknown command: " + cmd + "\n");
                    ConsoleUi.Dim("  Use /help to list the commands\n");
                    return true;
            }
        }

        // 一覧に番号を振って表示する(/delete で番号を指定できるようにするため)
        void ShowSessions()
        {
            List<object> list = store.List();
            if (list.Count == 0) { ConsoleUi.Info("No saved sessions"); return; }
            Console.WriteLine("  Saved sessions (" + store.DataDir + ")");
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                string id = Json.AsString(s.ContainsKey("id") ? s["id"] : "");
                string title = Json.AsString(s.ContainsKey("title") ? s["title"] : "");
                int count = s.ContainsKey("count") ? Json.AsInt(s["count"]) : 0;
                string mark = (id == ctx.Id) ? "* " : "  ";
                Console.WriteLine(mark + (i + 1).ToString().PadLeft(2) + ". "
                    + id + "  " + title + "  (" + count + " msgs)");
            }
            Console.WriteLine();
            ConsoleUi.Dim("  * = current session. Open with /open <number|id>, delete with /delete <number|id>\n");
        }

        // 番号(1 始まり)または ID からセッション ID を解決する
        string ResolveSessionId(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.Length == 0) return "";

            int n;
            if (int.TryParse(a, out n))
            {
                List<object> list = store.List();
                if (n >= 1 && n <= list.Count)
                {
                    Dictionary<string, object> s = list[n - 1] as Dictionary<string, object>;
                    if (s != null && s.ContainsKey("id")) return Json.AsString(s["id"]);
                }
                return "";
            }
            return a;   // ID として扱う
        }

        // セッションを削除する
        // 現在のセッションの内容(会話履歴)をクリアする。
        // セッション自体(ID・タイトル)は残す。
        void ClearSession()
        {
            if (ctx.Messages.Count == 0)
            {
                ConsoleUi.Info("This session has no messages yet");
                return;
            }

            string ans = ReadLineForPrompt("  Clear this session's history? ("
                + ctx.Messages.Count + " msgs)  [y/N]: ");
            ans = Program.SanitizeInput(ans).Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes")
            {
                ConsoleUi.Dim("  Cancelled\n");
                return;
            }

            // 履歴・要約・コンテキスト開始位置をリセットする
            ctx.Messages = new();
            ctx.Summary = "";
            ctx.ContextStart = 0;
            ctx.LastTotalTokens = 0;
            Save(ctx);

            ConsoleUi.Info("Cleared the session history: " + ctx.Id + "  (" + ctx.Title + ")");
            PrintHistory(ctx);
        }

        void DeleteSession(string arg)
        {
            if ((arg ?? "").Trim().Length == 0)
            {
                ConsoleUi.Danger("Specify the session to delete\n");
                ConsoleUi.Dim("  Usage: /delete <number|id>   (numbers come from /sessions)\n");
                return;
            }

            string id = ResolveSessionId(arg);
            if (id.Length == 0)
            {
                ConsoleUi.Danger("No session with that number: " + arg + "\n");
                ConsoleUi.Dim("  Check the numbers with /sessions\n");
                return;
            }

            Dictionary<string, object> meta = store.Load(id);
            if (meta == null)
            {
                ConsoleUi.Danger("No such session: " + arg + "\n");
                return;
            }

            string title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "";

            // 誤操作を防ぐため確認する
            string ans = ReadLineForPrompt("  Delete " + id + "  (" + title + ")?  [y/N]: ");
            ans = Program.SanitizeInput(ans).Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes")
            {
                ConsoleUi.Dim("  Cancelled\n");
                return;
            }

            bool wasCurrent = (id == ctx.Id);
            if (!store.Delete(id))
            {
                ConsoleUi.Danger("Could not delete: " + id + "\n");
                return;
            }
            ConsoleUi.Info("Deleted: " + id + "  (" + title + ")");

            // 表示中のセッションを消した場合は、別のセッションへ切り替える
            if (wasCurrent)
            {
                ctx = LoadOrCreate("", true);
                ConsoleUi.Info("Switched session: " + ctx.Id + "  (" + ctx.Title + ")");
                PrintHistory(ctx);
            }
        }

        // 過去の会話を表示する(続きから再開したとき)
        void PrintHistory(SessionCtx c)
        {
            Console.WriteLine();
            ConsoleUi.Dim("---- conversation so far ----");
            Console.WriteLine();

            // 直前の assistant の tool_calls から引数を覚えておき、
            // 続く tool メッセージの詳細表示に使う。
            Dictionary<string, string> callArgs = new();

            // このターンで既に表示した実行記録だけを steps から除く。
            // 回数も保持し、旧形式・部分的な履歴の記録まで消さない。
            Dictionary<string, int> displayedSteps = new(StringComparer.Ordinal);

            for (int idx = 0; idx < c.Messages.Count; idx++)
            {
                Dictionary<string, object> m = c.Messages[idx] as Dictionary<string, object>;
                if (m == null) continue;
                string role = Json.AsString(m.ContainsKey("role") ? m["role"] : "");
                string content = Json.AsString(m.ContainsKey("content") ? m["content"] : "");

                if (role == "user")
                {
                    callArgs.Clear();
                    displayedSteps.Clear();

                    // 対話中の入力と同じ形にする(上下の罫線 + プロンプト)
                    ConsoleUi.RuleTop();
                    ConsoleUi.Accent("> ");
                    Console.WriteLine(content);
                    ConsoleUi.RuleBottom();

                    // 罫線の直後は必ず 1 行空ける。
                    // 空けないと、次に本文が来るかツール表示が来るかで
                    // 見た目が変わってしまう。
                    Console.WriteLine();
                }
                else if (role == "assistant")
                {
                    // ツール呼び出しの引数を覚えておく(続く tool の表示用)
                    if (m.ContainsKey("tool_calls") && m["tool_calls"] is List<object> tcs)
                    {
                        callArgs.Clear();
                        foreach (object tco in tcs)
                        {
                            Dictionary<string, object> tc = tco as Dictionary<string, object>;
                            if (tc == null) continue;
                            string cid = tc.ContainsKey("id") ? Json.AsString(tc["id"]) : "";
                            Dictionary<string, object> fn = tc.ContainsKey("function")
                                ? tc["function"] as Dictionary<string, object> : null;
                            if (fn == null) continue;
                            object av = fn.ContainsKey("arguments") ? fn["arguments"] : null;
                            string args = (av is string) ? (string)av
                                : (av != null ? Json.ToString(av) : "");
                            if (cid.Length > 0) callArgs[cid] = args;
                        }
                    }

                    // 思考
                    if (m.ContainsKey("think"))
                    {
                        string think = Json.AsString(m["think"]);
                        if (think.Trim().Length > 0)
                            ConsoleUi.Think("  [thinking] " + think + "\n");
                    }

                    // 既に表示した tool と一致する記録だけを、一件ずつ消費する。
                    if (m.ContainsKey("steps") && m["steps"] is List<object> steps)
                    {
                        for (int i = 0; i < steps.Count; i++)
                        {
                            string st = Json.AsString(steps[i]);
                            if (displayedSteps.TryGetValue(st, out int count) && count > 0)
                            {
                                displayedSteps[st] = count - 1;
                                continue;
                            }
                            if (st.Length > 0)
                                ConsoleUi.Dim("  ▶ " + st + "\n");
                        }
                    }

                    if (content.Trim().Length > 0)
                    {
                        MarkdownRenderer.Render(content);
                        Console.WriteLine();
                    }
                }
                else if (role == "tool")
                {
                    string nn = m.ContainsKey("name") ? Json.AsString(m["name"]) : "";
                    string id = m.ContainsKey("tool_call_id") ? Json.AsString(m["tool_call_id"]) : "";

                    // 実行時と同じ「▶ Tool: 名前  詳細」で表示する。
                    // 引数は直前の assistant の tool_calls から取る。
                    string detail = "";
                    if (id.Length > 0 && callArgs.TryGetValue(id, out string args))
                        detail = ToolDisplay.Detail(nn, args);
                    ConsoleUi.Tool(nn, detail);
                    string step = ToolDisplay.Step(nn, detail);
                    displayedSteps.TryGetValue(step, out int count);
                    displayedSteps[step] = count + 1;
                }
            }
            Console.WriteLine();
        }

        // 1 ターン実行する
        // ストリーミング中の進行状況。同じ行を上書きする。
        static readonly string[] SPINNER = { "|", "/", "-", "\\" };
        int progressTick = 0;
        int progressLen = 0;

        // 出力がリダイレクトされているときは進行状況を出さない。
        // パイプで受け取ったときに \r やスピナーが混ざるのを避ける。
        //
        // -e / --execute は結果だけを出力したいので常に抑制する。
        bool ProgressEnabled()
        {
            if (noSave) return false;   // -e のときは静かにする
            try { return !Console.IsOutputRedirected; }
            catch { return true; }
        }

        // 種類が変わったときの区切り改行。
        // 出力がリダイレクトされているときは、先頭の空行を作らないよう
        // 最初の 1 回だけ省く(2 回目以降は種類の区切りとして必要)。
        bool wroteAnyKind;

        void SeparateKind()
        {
            if (!ProgressEnabled() && !wroteAnyKind) return;
            Console.WriteLine();
        }

        void ShowProgress(int chars)
        {
            if (!ProgressEnabled()) return;
            progressTick++;
            string spin = SPINNER[progressTick % SPINNER.Length];
            string msg;
            if (chars <= 0)
                msg = "  " + spin + " connecting...";
            else
                msg = "  " + spin + " receiving... " + chars + " chars";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append('\r').Append(msg);
                int pad = progressLen - msg.Length;
                if (pad > 0) sb.Append(new string(' ', pad));
                Console.Write(sb.ToString());
                progressLen = msg.Length;
            }
            catch { }
        }

        void ClearProgress()
        {
            if (!ProgressEnabled()) return;
            if (progressLen <= 0) return;
            try
            {
                Console.Write("\r" + new string(' ', progressLen) + "\r");
            }
            catch { }
            progressLen = 0;
        }

        void Turn(string userMessage)
        {
            cancelled = false;
            AutoTitle(ctx, userMessage);

            // 出力の種類。種類が変わったら改行を入れてから表示する。
            //   0 = なし, 1 = 思考, 2 = 本文
            // ツール実行は単独の行として出力し、そのあとは「なし」に戻す。
            const int KIND_NONE = 0, KIND_THINK = 1, KIND_CONTENT = 2;
            int kind = KIND_NONE;
            StringBuilder contentBuf = new StringBuilder();   // 本文(Markdown)
            progressLen = 0;
            progressTick = 0;
            wroteAnyKind = false;   // ターンごとにリセットする

            // 出力がリダイレクトされているときは、先頭の空行も出さない
            if (ProgressEnabled()) Console.WriteLine();
            if (LlmClient.DebugOn)
                ConsoleUi.Dim("  sending " + userMessage.Length + " chars...\n");
            ShowProgress(0);
            try
            {
                string reply = agent.RunStream(ctx, userMessage,
                    (c) => { Save(c); },
                    (d) =>
                    {
                        // 本文はバッファする(Markdown として整形して表示するため)
                        if (kind != KIND_CONTENT)
                        {
                            // 「/ connecting...」が残っていると同じ行に本文が
                            // 続いてしまうので、必ず消してから改行する。
                            ClearProgress();
                            if (kind != KIND_NONE) SeparateKind();
                            kind = KIND_CONTENT;
                            wroteAnyKind = true;
                        }
                        contentBuf.Append(d);
                        // Markdown として整形するため本文はバッファするが、
                        // 何も表示されないと「応答が無い」ように見えるので、
                        // 受信した文字数を進行状況として表示する。
                        ShowProgress(contentBuf.Length);
                    },
                    (d) =>
                    {
                        if (kind != KIND_THINK)
                        {
                            // 「/ connecting...」の行に [thinking] が
                            // 続かないよう、まず進行状況を消す。
                            ClearProgress();
                            if (kind != KIND_NONE) SeparateKind();
                            kind = KIND_THINK;
                            wroteAnyKind = true;
                            Console.Write("  ");
                            ConsoleUi.Dim("[thinking] ");
                        }
                        ConsoleUi.Think(d);
                    },
                    (name, detail) =>
                    {
                        ClearProgress();
                        // ツール実行は必ず単独の行にする
                        if (kind != KIND_NONE) SeparateKind();
                        ConsoleUi.Tool(name, detail);
                        kind = KIND_NONE;   // ツール行は改行済みなので次はそのまま始められる
                    },
                    () => cancelled);

                ClearProgress();

                if (cancelled)
                {
                    // Ctrl-C で中止した。途中までの本文は出さず、その旨を伝える。
                    if (kind != KIND_NONE) SeparateKind();
                    ConsoleUi.Dim("  (cancelled)\n");
                    Console.WriteLine();
                    cancelled = false;
                    ctrlCPressed = false;
                    return;
                }

                // 本文を Markdown として整形して表示する
                string body = (contentBuf.Length > 0)
                    ? contentBuf.ToString()
                    : (reply ?? "");
                if (body.Trim().Length > 0)
                {
                    // 本文はストリーミング中にバッファしたものを最後にまとめて
                    // 描画する。種類の区切りは上のコールバックで済んでいるので
                    // ここでは改行を足さない(先頭に空行が入ってしまう)。
                    if (kind == KIND_NONE) SeparateKind();
                    MarkdownRenderer.Render(body);
                    // 対話中は読みやすさのため空行を入れるが、
                    // -e では結果だけを出すので余分な改行を足さない。
                    if (!noSave) Console.WriteLine();
                }
                else if (kind != KIND_NONE)
                {
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                ClearProgress();
                Console.WriteLine();

                if (cancelled)
                {
                    // Ctrl-C による中止はエラーとして扱わない
                    ConsoleUi.Dim("  (cancelled)\n");
                    Console.WriteLine();
                    cancelled = false;
                    ctrlCPressed = false;
                    return;
                }

                string msg;
                WebException wex = ex as WebException;
                if (wex != null) msg = LlmClient.DescribeError(wex);
                else msg = ex.Message;
                ConsoleUi.Danger("⚠ Error: " + msg + "\n");

                // TLS ハンドシェイク失敗は、利用者が対処しやすいよう補足を出す
                if (wex != null && LlmClient.IsTlsFailure(wex))
                {
                    ConsoleUi.Dim("  The TLS handshake failed.\n");
                    ConsoleUi.Dim("  - Endpoint: " + cfg.ApiBaseUrl + "\n");
                    ConsoleUi.Dim("  - tau tried connecting with TLS 1.2 "
                        + (LlmClient.TlsApplied ? "(applied)" : "(could not be applied)") + ".\n");
                    ConsoleUi.Dim("  - If it still fails, one of the following is likely the cause:\n");
                    ConsoleUi.Dim("    1) TLS 1.2 is disabled in the OS\n");
                    ConsoleUi.Dim("       (Internet Options -> Advanced ->\n");
                    ConsoleUi.Dim("        enable \"Use TLS 1.2\")\n");
                    ConsoleUi.Dim("    2) Strong cryptography is disabled for .NET Framework\n");
                    ConsoleUi.Dim("       Set these DWORD values to 1:\n");
                    ConsoleUi.Dim("       HKLM\\SOFTWARE\\Microsoft\\.NETFramework\\v4.0.30319\n");
                    ConsoleUi.Dim("         SchUseStrongCrypto\n");
                    ConsoleUi.Dim("         SystemDefaultTlsVersions\n");
                    ConsoleUi.Dim("       (set the same values under WOW6432Node for 32-bit)\n");
                    ConsoleUi.Dim("    3) The proxy itself does not support TLS 1.2\n");
                    ConsoleUi.Dim("       (check proxyUrl / useSystemProxy in tau.json)\n");
                    ConsoleUi.Dim("  - To isolate the problem, run this in PowerShell:\n");
                    ConsoleUi.Dim("       [Net.ServicePointManager]::SecurityProtocol = 'Tls12'\n");
                    ConsoleUi.Dim("       Invoke-WebRequest " + cfg.ApiBaseUrl + " -Method Head\n");
                    ConsoleUi.Dim("  - For more detail, set TAU_DEBUG=1 and run again;\n");
                    ConsoleUi.Dim("    the actual TLS setting and the exception chain will be shown.\n");
                }

                // TAU_DEBUG=1 のときは、例外の連鎖と環境情報を必ず出す
                if (LlmClient.DebugOn)
                {
                    ConsoleUi.Dim("  --- debug ---\n");
                    ConsoleUi.Dim("  env: " + LlmClient.DescribeEnvironment() + "\n");
                    ConsoleUi.Dim("  TLS: " + LlmClient.CurrentTls + "\n");
                    ConsoleUi.Dim("  exception: " + LlmClient.ExceptionChain(ex).Replace("\n", "\n        ") + "\n");
                    ConsoleUi.Dim("  ----\n");
                }
            }
        }
    }
}
