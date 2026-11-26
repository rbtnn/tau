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
    // =======================================================================
    //  エントリポイント
    // =======================================================================
    class Program
    {
        static void Main(string[] args)
        {
            // コンソールのエンコーディングを設定する。
            //
            // 重要: 出力だけ UTF-8 にして入力が CP932 のままだと、Windows では
            // コンソールの入出力コードページが食い違い、日本語入力が NUL 文字
            // (\u0000) に化けることがある(dotnet/runtime#43295)。
            // 入出力を同じコードページに揃えるのが安全なので、
            // 対話コンソールのときは両方 UTF-8 にする。
            // 設定ファイルを先に読んで、入力エンコーディングの指定を知る必要がある。
            // (引数解析の後で Config を作るため、ここでは既定値を仮に使い、
            //  Config 生成後に改めて適用する)
            SetupConsoleEncoding(null);

            string configPath = "";
            string sessionArg = "";       // --open で開くセッション
            bool newSession = false;      // --new
            bool help = false;
            bool version = false;
            bool selfTest = false;
            bool selfTestOffline = false;
            bool web = false;
            int webPort = 8080;
            bool configShow = false;
            bool configEdit = false;
            string configSetKey = null;
            string configSetValue = null;
            string executeText = null;    // -e / --execute のプロンプト

            // --pdf2text / --word2text / --excel2text
            // 補助ツールと同じ動作をするサブコマンド。指定すると tau の
            // 対話は始めず、そのまま終了する。
            string toolKind = null;       // "pdf" / "word" / "excel"
            List<string> toolArgs = new();

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();

                // --config show / --config edit / --config set <k> <v> /
                // --config <path> の 4 通り。
                if (a == "--config")
                {
                    if (i + 1 >= args.Length) { configShow = true; }
                    else
                    {
                        string sub = (args[i + 1] ?? "").Trim();
                        if (sub == "show") { configShow = true; i++; }
                        else if (sub == "edit") { configEdit = true; i++; }
                        else if (sub == "set")
                        {
                            if (i + 3 < args.Length)
                            {
                                configSetKey = (args[i + 2] ?? "").Trim();
                                configSetValue = (args[i + 3] ?? "").Trim();
                                i += 3;
                            }
                            else
                            {
                                ConsoleUi.Danger("Usage: --config set <key> <value>\n");
                                Environment.ExitCode = 1;
                                return;
                            }
                        }
                        else { configPath = sub; i++; }   // 従来のパス指定
                    }
                }
                else if (a.StartsWith("--config=")) configPath = a.Substring("--config=".Length);
                else if (a == "--open" && i + 1 < args.Length) sessionArg = args[++i].Trim();
                else if (a.StartsWith("--open=")) sessionArg = a.Substring("--open=".Length);
                else if (a == "--new") newSession = true;
                else if (a == "-e" && i + 1 < args.Length) executeText = args[++i];
                else if (a == "--execute" && i + 1 < args.Length) executeText = args[++i];
                else if (a.StartsWith("--execute=")) executeText = a.Substring("--execute=".Length);
                else if (a.StartsWith("-e=")) executeText = a.Substring("-e=".Length);
                else if (a == "--help" || a == "-h" || a == "/?") help = true;
                else if (a == "--list") { ListSessions(configPath); return; }
                else if (a == "--debug") LlmClient.EnableDebug();
                else if (a == "--version" || a == "-v") version = true;
                else if (a == "--test") selfTest = true;
                else if (a == "--test=offline") { selfTest = true; selfTestOffline = true; }
                else if (a == "--test-offline") { selfTest = true; selfTestOffline = true; }
                else if (a == "--pdf2text") toolKind = "pdf";
                else if (a == "--word2text") toolKind = "word";
                else if (a == "--excel2text") toolKind = "excel";
                else if (a == "--web") web = true;
                else if (a.StartsWith("--web=")) { web = true; ParsePort(a.Substring("--web=".Length), ref webPort); }
                else if (a == "--port" && i + 1 < args.Length) { ParsePort(args[++i], ref webPort); }
                else if (a.StartsWith("--port=")) ParsePort(a.Substring("--port=".Length), ref webPort);

                // 旧オプションの受け皿(互換のため残す)
                else if (a == "--diag") { selfTest = true; selfTestOffline = false; }
                else if (a == "--session" && i + 1 < args.Length) sessionArg = args[++i].Trim();
                else if (a.StartsWith("--session=")) sessionArg = a.Substring("--session=".Length);
            }

            // --pdf2text などの後ろは、そのまま補助ツールへ渡す。
            // ループでは未知の引数を無視していたため、ここで集め直す。
            if (toolKind != null)
            {
                bool afterTool = false;
                foreach (string raw in args)
                {
                    string a = (raw ?? "").Trim();
                    if (!afterTool)
                    {
                        if (a == "--pdf2text" || a == "--word2text" || a == "--excel2text")
                            afterTool = true;
                        continue;
                    }
                    toolArgs.Add(raw);
                }
                Environment.ExitCode = RunTool(toolKind, toolArgs);
                return;
            }

            if (version) { PrintVersion(); return; }
            if (help) { PrintHelp(); return; }

            // 旧バージョンの %APPDATA%\tau にデータがあれば、
            // 新しい %APPDATA%\tau-cli へコピーする(初回のみ)。
            // --config を明示した場合はその場所を使うので移行しない。
            if (configPath == null || configPath.Trim().Length == 0)
            {
                bool migrated = Config.MigrateLegacyData();
                if (migrated)
                {
                    ConsoleUi.Info("Migrated data from "
                        + Config.LegacyDataDir() + " to " + Config.TauDataDir());
                    ConsoleUi.Dim("  (the old directory was left in place;"
                        + " you can delete it once you are happy)\n");
                }
            }

            Config cfg = new Config(configPath);

            // Shift-JIS などのコードページを使えるようにする(.NET では既定で無効)
            RegisterCodePages();

            // 設定ファイルの inputEncoding に従って、入力を設定し直す。
            SetupConsoleEncoding(cfg);

            // 入力バッファを広げる(254 文字の上限を回避する)。
            SetupInputBuffer(cfg);

            // 行編集(Emacs キーバインド)を使えるか判定する。
            LineEditor.Configure(cfg);

            // Markdown の色付けを、コンソールの色対応に合わせる
            MarkdownRenderer.Configure(ConsoleUi.ColorEnabled);

            // 端末が実際に何桁で描くかを、出力を始める前に測っておく。
            // (①②③ などは計算上 2 桁でも 1 桁で描く端末がある)
            MarkdownRenderer.MeasureWidthsOnce();

            // TLS 1.2 を最初に適用しておく。
            // .NET Framework の既定は Ssl3 | Tls のままで、現代のサーバでは
            // ほぼ必ず拒否される。要求のたびに設定するより先に固定しておく。
            LlmClient.ApplyTls();

            // --config show / --config edit / --config set
            // コンソールを立ち上げずに設定だけ扱う。
            if (configShow)
            {
                Settings.Show(cfg);
                return;
            }
            if (configEdit)
            {
                Settings.Edit(cfg);
                return;
            }
            if (configSetKey != null)
            {
                if (!Settings.Set(cfg, configSetKey, configSetValue))
                {
                    Environment.ExitCode = 1;
                }
                return;
            }

            if (selfTest)
            {
                // --test は「接続診断 + 自己診断」。--test=offline では
                // ネットワークを使う診断を省く。
                if (!selfTestOffline) Diagnostics.Run(cfg);
                int code = SelfTest.Run(cfg, !selfTestOffline);
                try { Console.Out.Flush(); } catch { }
                Environment.ExitCode = code;
                return;
            }

            // --web: ブラウザ用の最小 HTTP サーバで起動する。
            // 設定は tau-cli の tau.json をそのまま使い、画面からは
            // チャットとセッション切替だけができる。
            if (web) { RunWeb(cfg, webPort, sessionArg); return; }

            Chat chat = new Chat(cfg);
            if (executeText != null) { chat.RunOnce(sessionArg, newSession, executeText); return; }
            chat.Run(sessionArg, newSession);
        }

        // 入力エンコーディングを「変更しない」のが原則だが、起動時点で UTF-8 に
        // なっていると日本語入力が壊れるため、コンソール既定に戻す。
        // コンソールの入出力エンコーディングを設定する。
        //
        // Windows では入出力のコードページが食い違うと日本語入力が壊れるため、
        // 対話コンソールのときは両方 UTF-8 に揃える。
        // 設定名 → Encoding を解決する。解決できなければ null。
        // .NET (Core) では Shift-JIS などのコードページが既定で使えない。
        // 一度だけ CodePagesEncodingProvider を登録する。
        static bool codePagesRegistered;

        internal static void RegisterCodePages()
        {
            if (codePagesRegistered) return;
            codePagesRegistered = true;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch { }
        }

        public static Encoding ResolveEncoding(string name)
        {
            if (name == null) return null;
            string n = name.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "");
            switch (n)
            {
                case "":
                case "default":
                case "auto":
                    return null;   // 変更しない
                case "utf-8":
                case "utf8":
                case "65001":
                    return Encoding.UTF8;
                case "shift-jis":
                case "shiftjis":
                case "sjis":
                case "cp932":
                case "932":
                case "windows-31j":
                    RegisterCodePages();
                    try { return Encoding.GetEncoding(932); }
                    catch { return null; }
                default:
                    // 数値のコードページ指定も受け付ける
                    {
                        int cp;
                        if (int.TryParse(n, out cp))
                        {
                            RegisterCodePages();
                            try { return Encoding.GetEncoding(cp); }
                            catch { return null; }
                        }
                    }
                    return null;
            }
        }

        // コンソールの入出力エンコーディングを設定する。
        //
        // cfg が null の場合は既定(shift-jis)として扱う。
        // 出力は常に UTF-8(日本語の表示が化けない)。
        // 入力は cfg.InputEncoding に従う:
        //   - shift-jis … Windows のコンソール既定。日本語入力が安定する
        //   - utf-8     … コンソールが UTF-8 のとき
        //   - default   … 触らない
        // Console.ReadLine() は .NET Framework で既定 256 単位の入力バッファを使う。
        // この上限はバイト数ではなく「文字数」で判定され、256 には CR/LF も
        // 含まれるため 254 文字までしか読めず、それ以降は黙って切り捨てられる。
        // 全角・半角を問わず 254 文字で切れるため、日本語の長いプロンプトが
        // 途中で切れて送信される。
        //
        // Console.In を大きなバッファの StreamReader に差し替えると、
        // 既存の Console.ReadLine() 呼び出しすべてがその恩恵を受ける。
        // (コンソールの行編集・IME はそのまま使える)
        // テストから使うための薄いラッパ
        internal static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new();
            m["role"] = role;
            m["content"] = content;
            return m;
        }

        // ------------------------------------------------------------------
        //  コンソールのカーソル桁位置を、端末に問い合わせずに取得する。
        //  Console.CursorLeft は Mono / 一部端末で ESC[6n を送って応答待ちに
        //  なるため、Windows では GetConsoleScreenBufferInfo を直接使う。
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public short wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
            out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", EntryPoint = "SetConsoleCursorPosition",
            SetLastError = true)]
        static extern bool SetConsoleCursorPos(IntPtr hConsoleOutput, COORD dwCursorPosition);

        [DllImport("kernel32.dll",
            CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WriteConsoleW(IntPtr hConsoleOutput, string lpBuffer,
            uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        // コンソールに 1 文字を直接書く(Console.Out のバッファを通さない)。
        internal static bool WriteConsoleChar(char c)
        {
            if (!IsWindows()) return false;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
                uint written;
                return WriteConsoleW(h, c.ToString(), 1, out written, IntPtr.Zero);
            }
            catch { return false; }
        }

        // 指定位置から空白で上書きする(測定した文字を消すため)。
        internal static bool FillConsoleAt(int x, int y, int count)
        {
            if (!IsWindows() || count <= 0) return false;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;

                if (!SetConsoleCursorPos(h, MakeCoord(x, y))) return false;
                uint written;
                return WriteConsoleW(h, new string(' ', count), (uint)count,
                    out written, IntPtr.Zero);
            }
            catch { return false; }
        }

        static COORD MakeCoord(int x, int y)
        {
            COORD c;
            c.X = (short)x;
            c.Y = (short)y;
            return c;
        }

        // カーソルを移動し、移動後の桁位置を返す。失敗したら -1。
        internal static int SetConsoleCursorPosition(int x, int y)
        {
            if (!IsWindows()) return -1;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;

                if (!SetConsoleCursorPos(h, MakeCoord(x, y))) return -1;
                return x;
            }
            catch { return -1; }
        }

        // カーソル位置と画面サイズを取得する。
        internal static bool GetConsoleCursorAndSize(out int col, out int row,
                                                    out int width, out int height)
        {
            col = -1; row = -1; width = 0; height = 0;
            if (!IsWindows()) return false;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;

                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(h, out info)) return false;

                col = info.dwCursorPosition.X;
                row = info.dwCursorPosition.Y;
                // ウィンドウ内の桁数・行数(バッファ全体ではなく表示領域)
                width = info.srWindow.Right - info.srWindow.Left + 1;
                height = info.srWindow.Bottom - info.srWindow.Top + 1;
                return true;
            }
            catch { return false; }
        }

        // 現在のカーソル桁位置。取得できなければ -1。
        internal static int GetConsoleCursorColumn()
        {
            if (!IsWindows()) return -1;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;

                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(h, out info)) return -1;
                return info.dwCursorPosition.X;
            }
            catch { return -1; }
        }

        internal static bool IsWindows()
        {
            try
            {
                int p = (int)Environment.OSVersion.Platform;
                return p == 1 || p == 2;
            }
            catch { return false; }
        }

        static void SetupInputBuffer(Config cfg)
        {
            const int READLINE_BUFFER_SIZE = 64 * 1024;   // 64KB

            try
            {
                // 大きなバッファで読む(254 文字の上限を回避する)。
                //
                // Console.In は Windows では ReadFile を使うが、
                // ENABLE_LINE_INPUT は ReadFile にも効くため、コンソールの
                // 行編集(Ctrl-V の貼り付け・矢印キー・IME)はそのまま働く。
                //
                // 入力がリダイレクトされている場合、Console.InputEncoding は
                // OS の既定(Windows では CP437)のままで、日本語を読めない。
                // 設定の inputEncoding を優先して、パイプでも正しく読む。
                Encoding enc = null;
                if (cfg != null && !IsAutoEncoding(cfg.InputEncoding))
                    enc = ResolveEncoding(cfg.InputEncoding);
                if (enc == null)
                {
                    try { enc = Console.InputEncoding; }
                    catch { enc = Encoding.UTF8; }
                }

                Stream stdin = Console.OpenStandardInput(READLINE_BUFFER_SIZE);
                Console.SetIn(new StreamReader(stdin, enc,
                                               false, READLINE_BUFFER_SIZE));

                // 念のため行編集モードを有効にする
                LineEditor.EnsureConsoleLineInputPublic();
            }
            catch
            {
                // 設定できない環境では既定のままにする
            }
        }

        static void SetupConsoleEncoding(Config cfg)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { }

            // 入力がリダイレクトされている場合は触らない
            // (パイプやファイルからの入力はそのまま読める)
            bool redirected = false;
            try { redirected = Console.IsInputRedirected; }
            catch { }
            if (redirected) return;

            string want = (cfg != null && cfg.InputEncoding != null && cfg.InputEncoding.Trim().Length > 0)
                ? cfg.InputEncoding.Trim()
                : "auto";

            // コンソールが UTF-8(chcp 65001)なら、入力もそのまま UTF-8 にする。
            // コードページと Console.InputEncoding が最初から一致しているので、
            // SetConsoleCP で無理に合わせる必要が無い。
            // ここで CP932 に変えると、ReadKey が返す日本語が壊れる。
            if (IsAutoEncoding(want) && LineEditor.IsUtf8Console())
            {
                try { Console.InputEncoding = Encoding.UTF8; } catch { }
                return;
            }

            // auto で UTF-8 でないコンソールなら、従来どおり CP932 にする
            if (IsAutoEncoding(want)) want = "shift-jis";

            Encoding enc = ResolveEncoding(want);
            if (enc == null)
            {
                // "default" 指定、または解決できなかった場合は変更しない
                return;
            }

            try
            {
                Console.InputEncoding = enc;
            }
            catch
            {
                // 設定できない場合は何もしない(コンソール既定のまま)
            }

            // Windows では Console.InputEncoding を変えても、コンソールの
            // 実際のコードページ(GetConsoleCP)は変わらないことがある。
            // その状態で Console.ReadKey() を使うと日本語が壊れる
            // (例: 「あ」→ U+7E3A)。両者を一致させておく。
            try { SetConsoleCPIfPossible(enc.CodePage); } catch { }
        }

        // inputEncoding が「自動」指定か(空 / default / auto)。
        // utf-8 や shift-jis は明示指定として扱う。
        internal static bool IsAutoEncodingPublic(string name) { return IsAutoEncoding(name); }

        static bool IsAutoEncoding(string name)
        {
            if (name == null) return true;
            string n = name.Trim().ToLowerInvariant();
            return n.Length == 0 || n == "default" || n == "auto";
        }

        // コンソールの実際のコードページを、指定した値に合わせる。
        // Windows 以外や失敗時は何もしない。
        static void SetConsoleCPIfPossible(int codePage)
        {
            if (codePage <= 0) return;
            try
            {
                int p = (int)Environment.OSVersion.Platform;
                if (p != 2 && p != 1) return;   // Windows 以外

                int current = LineEditor.ConsoleInputCodePagePublic();
                if (current == codePage) return;   // 既に一致している

                if (LineEditor.SetConsoleCP((uint)codePage))
                    LineEditor.MarkConsoleCodePage(codePage);
            }
            catch { }
        }

        // 文字列中の NUL 文字の個数を数える
        public static int CountNul(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\0') n++;
            return n;
        }

        // 現在の入力エンコーディングを表示用に返す(診断用)
        public static string SafeInputEncoding()
        {
            try
            {
                Encoding e = Console.InputEncoding;
                return e.WebName + " (CP" + e.CodePage + ")"
                    + (Console.IsInputRedirected ? ", redirected" : "");
            }
            catch (Exception ex) { return "unknown (" + ex.Message + ")"; }
        }

        // 入力文字列から NUL 文字を取り除く。
        // コンソール設定が原因で \u0000 が混入しても、セッションを壊さないための保険。
        public static string SanitizeInput(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('\0') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (s[i] != '\0') sb.Append(s[i]);
            return sb.ToString();
        }

        // ビルド識別子。古い tau.exe を動かしていないか確認できるようにする。
        public const string BuildStamp = "2026-11-23 dotnet10+json";

        // --web の起動。Ctrl-C で止まるまで動き続ける。
        static void RunWeb(Config cfg, int port, string sessionArg)
        {
            WebServer server = new(cfg);
            try
            {
                server.Start(port);
            }
            catch (Exception ex)
            {
                ConsoleUi.Danger("Could not start the web server: " + ex.Message + "\n");
                Environment.ExitCode = 1;
                return;
            }

            ConsoleUi.Banner(cfg, null, false);
            ConsoleUi.Info("Web UI: " + server.Url);
            ConsoleUi.Dim("  Settings come from " + cfg.ConfigPath + " (read-only from the browser)\n");
            ConsoleUi.Dim("  Press Ctrl-C to stop\n");

            // 指定セッションがあれば「最後に開いていたセッション」として記録する
            if (!string.IsNullOrWhiteSpace(sessionArg))
            {
                SessionStore store = new();
                if (store.Load(sessionArg) != null) store.SetLastSessionId(sessionArg);
            }

            ManualResetEventSlim stop = new(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;   // プロセスを即殺させない
                stop.Set();
            };
            stop.Wait();
            server.Stop();
            ConsoleUi.Dim("\n  stopped\n");
        }

        static void ParsePort(string s, ref int port)
        {
            if (int.TryParse((s ?? "").Trim(), out int p) && p >= 1 && p <= 65535) port = p;
        }

        internal static void PrintVersion()
        {
            Console.WriteLine("tau " + BuildStamp);

            // 単一ファイル配布では Assembly.Location が空文字になるため、
            // 実行中のプロセスのパスを使う。
            string exePath = "";
            try { exePath = Environment.ProcessPath; } catch { }
            if (string.IsNullOrEmpty(exePath))
            {
                try
                {
                    exePath = Path.Combine(AppContext.BaseDirectory,
                        Program.IsWindows() ? "tau.exe" : "tau");
                }
                catch { }
            }
            Console.WriteLine("  exe   : " + exePath);

            try
            {
                Console.WriteLine("  built : "
                    + File.GetLastWriteTimeUtc(exePath).ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            }
            catch { }
            Console.WriteLine("  runtime: " + Environment.Version
                + " (" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + ")");
        }

        internal static void PrintHelp()
        {
            Console.WriteLine("tau - AI agent that runs in the console");
            Console.WriteLine();
            Console.WriteLine("Usage: tau.exe [options]");
            Console.WriteLine("  --version            Show the build stamp and exit");
            Console.WriteLine("  --config show        Show the current settings");
            Console.WriteLine("  --config set <k> <v> Change a single setting (e.g. --config set model gpt-4o)");
            Console.WriteLine("                       The settings contain the path to the config file (tau.json)");
            Console.WriteLine("  --config edit        Edit settings interactively");
            Console.WriteLine("  -e, --execute <text> Send one prompt and exit (not saved to a session)");
            Console.WriteLine("  --new                Start a new session");
            Console.WriteLine("  --open <n/id>        Open an existing session and continue it");
            Console.WriteLine("  --list               List saved sessions and exit");
            Console.WriteLine("  --test               Run a connection diagnostic (TLS/HTTP) and the built-in self test");
            Console.WriteLine("                       (includes an API request)");
            Console.WriteLine("  --web                Serve a minimal web UI in the browser (emergency use)");
            Console.WriteLine("  --port <n>           Port for --web (default 8080, picks a free one if taken)");
            Console.WriteLine("  --debug              Enable debug output (same as TAU_DEBUG=1)");
            Console.WriteLine("  --pdf2text <file.pdf>   Print the text of a PDF (like pdf2text.exe)");
            Console.WriteLine("  --word2text <file.docx> Print the text of a Word file");
            Console.WriteLine("  --excel2text <file.xlsx> Print the text of an Excel workbook");
            Console.WriteLine("                       Add --json, --describe, --sheet <name> or --rows <n>");
            Console.WriteLine("  --help, -h, /?       Show this help");
            Console.WriteLine();
            Console.WriteLine("Commands available while chatting:");
            Console.WriteLine("  /n[ew]             Start a new session");
            Console.WriteLine("  /l[ist]            List saved sessions");
            Console.WriteLine("  /o[pen] <n|id>     Open a session by number or id");
            Console.WriteLine("  /d[elete] <n|id>   Delete a session by number or id");
            Console.WriteLine("  /c[lear]           Clear the current session's history");
            Console.WriteLine("  /t[ools]           Toggle tool execution on/off");
            Console.WriteLine("  /h[elp]            Show this help");
            Console.WriteLine("  /e[xit], /q[uit]   Exit (Ctrl+D also works)");
            Console.WriteLine();
            Console.WriteLine("  [ ] marks the optional part, as in Vim help (/n is /new).");
        }

        // ===================================================================
        //  補助ツール相当のサブコマンド
        //
        //  --pdf2text / --word2text / --excel2text は、単体で配布していた
        //  pdf2text.exe などと同じ引数・出力を受け付ける。
        //  tau.exe 1 つで完結させたい人のために用意している。
        //
        //  使い方:
        //    tau.exe --pdf2text <file.pdf>
        //    tau.exe --pdf2text --json <file.pdf>
        //    tau.exe --pdf2text --describe <file.pdf>
        //    tau.exe --excel2text --sheet 売上 --rows 50 <file.xlsx>
        //
        //  終了コード:
        //    0  成功
        //    1  読めなかった(--json では error を含む JSON を返す)
        //    2  使い方の誤り
        // ===================================================================
        internal static int RunTool(string kind, List<string> args)
        {
            // 出力は UTF-8 に固定する(パイプで受けたとき文字化けしない)
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            bool json = false;
            bool describe = false;
            string sheet = null;
            int maxRows = 0;
            string path = null;

            string usage = "usage: tau.exe --" + kind + "2text"
                + (kind == "excel" ? " [--json] [--sheet <name>] [--rows <n>]" : " [--json|--describe]")
                + " <file." + (kind == "pdf" ? "pdf" : kind == "word" ? "docx" : "xlsx") + ">";

            for (int i = 0; i < args.Count; i++)
            {
                string a = (args[i] ?? "").Trim();

                if (a == "--json") json = true;
                else if (a == "--describe") describe = true;
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine(usage);
                    return 0;
                }
                else if (a == "--sheet" && i + 1 < args.Count) sheet = (args[++i] ?? "").Trim();
                else if (a.StartsWith("--sheet=")) sheet = a.Substring("--sheet=".Length);
                else if (a == "--rows" && i + 1 < args.Count)
                {
                    if (!int.TryParse((args[++i] ?? "").Trim(), out maxRows) || maxRows < 0)
                    {
                        Console.Error.WriteLine("invalid --rows value");
                        return 2;
                    }
                }
                else if (a.StartsWith("--rows="))
                {
                    if (!int.TryParse(a.Substring("--rows=".Length), out maxRows) || maxRows < 0)
                    {
                        Console.Error.WriteLine("invalid --rows value");
                        return 2;
                    }
                }
                else if (path == null) path = args[i];
                else
                {
                    Console.Error.WriteLine("too many arguments: " + a);
                    return 2;
                }
            }

            if (path == null)
            {
                Console.Error.WriteLine(usage);
                return 2;
            }

            // ファイルが無い場合は、パーサの奥深い例外ではなく
            // 分かりやすいメッセージを返す(補助ツールと同じ)。
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("file does not exist: " + path);
                return 1;
            }

            if (describe)
            {
                Console.WriteLine(DocumentText.Describe(kind, path));
                return 0;
            }

            DocumentJson.Result r = DocumentText.Read(kind, path, sheet, maxRows);

            if (json)
            {
                Console.Out.Write(DocumentJson.ToJson(kind, r, maxRows));
                Console.Out.Write('\n');
                return r.Text != null ? 0 : 1;
            }

            if (r.Text == null)
            {
                Console.Error.WriteLine(r.Error);
                return 1;
            }

            Console.Out.Write(r.Text);
            Console.Out.Write('\n');
            return 0;
        }

        static void ListSessions(string configPath)
        {
            // 設定を読み込んでおく(保存先ディレクトリの作成も兼ねる)
            new Config(configPath);
            SessionStore store = new SessionStore();
            List<object> list = store.List();
            if (list.Count == 0)
            {
                Console.WriteLine("No saved sessions.");
                Console.WriteLine("  Location: " + store.DataDir);
                return;
            }
            Console.WriteLine("Saved sessions (" + store.DataDir + ")");
            Console.WriteLine();
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                Console.WriteLine("  " + Json.AsString(Get(s, "id")) + "  " + Json.AsString(Get(s, "title")));
            }
        }

        internal static object Get(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k)) return d[k];
            return null;
        }
    }
}
