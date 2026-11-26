// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  自己診断テスト (tau --test)
//
//  ここに残すのは「そのマシンでしか確かめられない、致命的な項目」だけ。
//  単体テストで確認できるものは dotnet test (tests/) に移してある。
//
//  残している理由:
//   - 入力エンコーディング: 実際のコンソールのコードページに依存する。
//     Shift-JIS の環境でないと再現しない不具合があるため。
//   - TLS: 実際のハンドラに設定を適用できるかは、実行環境の .NET に依存する。
//
//  確認する内容:
//   1. 実行環境(OS / .NET / エンコーディング)
//   2. コンソールの行編集モード(Ctrl-V の貼り付けが効く状態か)
//   3. 入力エンコーディングの往復(全角・半角・混在・長文)
//   4. 入力バッファの上限(254 文字の壁がないか)
//   5. TLS の設定
// ---------------------------------------------------------------------------
using System;
using System.Text;

namespace Tau
{
    // =======================================================================
    //  自己診断テスト
    // =======================================================================
    static class SelfTest
    {
        static int passed;
        static int failed;
        static int skipped;

        static void Pass(string msg)
        {
            passed++;
            Console.Write("  ");
            ConsoleUi.Dim("[");
            ConsoleUi.Accent("OK");
            ConsoleUi.Dim("] ");
            Console.WriteLine(msg);
        }

        static void Fail(string msg, string detail)
        {
            failed++;
            Console.Write("  ");
            ConsoleUi.Dim("[");
            ConsoleUi.Danger("NG");
            ConsoleUi.Dim("] ");
            Console.WriteLine(msg);
            if (detail != null && detail.Length > 0)
                ConsoleUi.Dim("       " + detail + "\n");
        }

        static void Skip(string msg)
        {
            skipped++;
            Console.Write("  ");
            ConsoleUi.Dim("[--] " + msg + "\n");
        }

        static void Check(bool condition, string msg, string detail)
        {
            if (condition) Pass(msg); else Fail(msg, detail);
        }

        static void Section(string title)
        {
            Console.WriteLine();
            ConsoleUi.Bold("  " + title + "\n");
        }

        // -------------------------------------------------------------------

        public static int Run(Config cfg, bool online)
        {
            Console.WriteLine();
            ConsoleUi.Bold("tau --test  self test");
            Console.WriteLine();
            ConsoleUi.Dim("  Only the checks that depend on this machine are run here.\n");
            ConsoleUi.Dim("  Everything else lives in dotnet test (tests/).\n");

            Environment();
            ConsoleModes();

            // 入力がリダイレクトされていると、コンソールのコードページではなく
            // パイプの既定エンコーディングが使われる。利用者の入力環境では
            // ないので、エンコーディングの往復は調べない。
            if (SafeRedirectedBool())
            {
                Section("3. input encoding round-trip");
                Skip("stdin is redirected (console input encoding is not in use)");
                Section("4. input buffer limit (254-character wall)");
                Skip("stdin is redirected");
            }
            else
            {
                Encodings();
                BufferLimits();
            }

            Tls(cfg);

            if (!online)
                Skip("network checks skipped (--test=offline)");

            Console.WriteLine();
            if (failed == 0)
            {
                ConsoleUi.Bold("  Result: " + passed + " passed");
                if (skipped > 0) ConsoleUi.Dim(", " + skipped + " skipped");
                ConsoleUi.Bold("\n");
            }
            else
            {
                ConsoleUi.Danger("  Result: " + passed + " passed, " + failed + " FAILED");
                if (skipped > 0) ConsoleUi.Dim(", " + skipped + " skipped");
                ConsoleUi.Danger("\n");
            }
            return (failed == 0) ? 0 : 1;
        }

        // ---------------- 1. 環境 ----------------

        static void Environment()
        {
            Section("1. environment");

            Console.WriteLine("    " + LlmClient.DescribeEnvironment());
            Check(Program.IsWindows() || !Program.IsWindows(),   // 常に真(情報表示)
                "OS: " + (Program.IsWindows() ? "Windows" : "not Windows"), null);

            string inEnc = Program.SafeInputEncoding();
            Console.WriteLine("    input encoding : " + inEnc);

            string outEnc = "";
            try
            {
                outEnc = Console.OutputEncoding.WebName
                    + " (CP" + Console.OutputEncoding.CodePage + ")";
            }
            catch { }
            Console.WriteLine("    output encoding: " + outEnc);

            // 入力と出力のコードページが食い違うと、Windows では日本語が
            // NUL に化けることがある(dotnet/runtime#43295)。
            // ただしリダイレクト時はパイプの既定エンコーディングなので、
            // 利用者のコンソール設定とは無関係。
            if (SafeRedirectedBool())
            {
                Skip("input and output encodings match (stdin is redirected)");
                // リダイレクト時でも、コンソール自体が UTF-8 かは分かる。
                ShowConsoleUtf8();
                return;
            }

            bool match = false;
            try
            {
                match = Console.InputEncoding.CodePage == Console.OutputEncoding.CodePage;
            }
            catch { }
            Check(match, "input and output encodings match",
                match ? null : "input and output use different code pages;"
                    + " Japanese input may turn into NUL");

            ShowConsoleUtf8();
        }

        // コンソールが UTF-8(chcp 65001)か、行編集が使えるかを表示する。
        // UTF-8 のときだけ Emacs 風の行編集を使う。
        //
        // 入力がリダイレクトされているときは、利用者のコンソール設定と
        // 関係ないので判定しない(情報として表示するだけ)。
        static void ShowConsoleUtf8()
        {
            if (!Program.IsWindows()) return;

            bool utf8 = LineEditor.IsUtf8Console();
            Console.WriteLine("    console code page: "
                + (utf8 ? "UTF-8 (chcp 65001)" : "not UTF-8"));

            if (SafeRedirectedBool())
            {
                Skip("console is UTF-8 (chcp 65001) (stdin is redirected)");
            }
            else
            {
                Check(utf8, "console is UTF-8 (chcp 65001)",
                    utf8 ? null
                        : "run `chcp 65001` first. Without it, Japanese typed into"
                          + " the console can be garbled, and line editing"
                          + " (Ctrl-A, Ctrl-K, ...) stays off");
            }

            Console.WriteLine("    line editing: "
                + (LineEditor.Available ? "on (Emacs keys)" : "off (Console.ReadLine)"));
        }

        // ---------------- 2. コンソールの行編集モード ----------------

        static void ConsoleModes()
        {
            Section("2. console line-input mode (Ctrl-V paste)");

            if (!Program.IsWindows())
            {
                Skip("Windows only");
                return;
            }

            if (SafeRedirectedBool())
            {
                Skip("stdin is redirected");
                return;
            }

            uint mode;
            bool got = LineEditor.TryGetConsoleMode(out mode);
            Check(got, "GetConsoleMode succeeded", got ? null : "GetConsoleMode failed");

            if (got)
            {
                bool line = (mode & LineEditor.EnableLineInput) != 0;
                bool echo = (mode & LineEditor.EnableEchoInput) != 0;
                Check(line, "ENABLE_LINE_INPUT is set (paste works)",
                    line ? null : "Ctrl-V paste will not work");
                Check(echo, "ENABLE_ECHO_INPUT is set", echo ? null : "typed input is not echoed");
            }
        }

        static bool SafeRedirectedBool()
        {
            try { return Console.IsInputRedirected; } catch { return false; }
        }

        // ---------------- 3. エンコーディング ----------------

        static void Encodings()
        {
            Section("3. input encoding round-trip");

            Encoding enc = null;
            try { enc = Console.InputEncoding; } catch { }
            if (enc == null)
            {
                Fail("Console.InputEncoding is available", "null");
                return;
            }

            // 代表的な文字列を、そのエンコーディングで往復できるか。
            // 実際のコンソールのコードページでしか確かめられない。
            string[] samples = new string[]
            {
                "あいうえお",                       // 全角ひらがな
                "アイウエオ",                       // 全角カタカナ
                "日本語のテスト",                   // 漢字
                "ABCabc123",                        // 半角英数
                "あAいBうC",                        // 全角半角混在
                "ｱｲｳｴｵ",                          // 半角カタカナ
                "、。「」（）",                     // 記号
                "①②③",                            // 丸数字
            };

            for (int i = 0; i < samples.Length; i++)
            {
                string src = samples[i];
                bool okRound = true;
                string why = "";
                try
                {
                    byte[] bytes = enc.GetBytes(src);
                    string back = enc.GetString(bytes);
                    okRound = (back == src);
                    if (!okRound) why = "got " + Escape(back);
                }
                catch (Exception ex)
                {
                    okRound = false;
                    why = ex.GetType().Name;
                }
                Check(okRound, "round-trip: " + src, okRound ? null : why);
            }

            // NUL に化けないこと
            string jp = "日本語のテストです";
            byte[] b = enc.GetBytes(jp);
            string r = enc.GetString(b);
            int nul = Program.CountNul(r);
            Check(nul == 0, "no NUL characters after round-trip",
                nul > 0 ? nul + " NUL(s) found" : null);
        }

        static string Escape(string s)
        {
            if (s == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length && i < 24; i++)
                sb.Append("U+").Append(((int)s[i]).ToString("X4")).Append(' ');
            return sb.ToString();
        }

        // ---------------- 4. 入力バッファの上限 ----------------

        static void BufferLimits()
        {
            Section("4. input buffer limit (254-character wall)");

            // 254 / 255 / 300 / 600 文字の全角文字列を作り、
            // 現在のエンコーディングで往復できることを確認する。
            int[] sizes = new int[] { 254, 255, 300, 600 };
            Encoding enc = null;
            try { enc = Console.InputEncoding; } catch { }

            for (int i = 0; i < sizes.Length; i++)
            {
                int n = sizes[i];
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < n; k++) sb.Append('あ');
                string src = sb.ToString();

                if (enc == null)
                {
                    Fail("encoding round-trip at " + n + " chars", "no encoding");
                    continue;
                }

                string back = null;
                string why = null;
                try
                {
                    back = enc.GetString(enc.GetBytes(src));
                }
                catch (Exception ex) { why = ex.GetType().Name; }

                if (back == null)
                {
                    Fail("encoding round-trip at " + n + " chars", why);
                    continue;
                }

                int got = 0;
                for (int k = 0; k < back.Length; k++)
                    if (back[k] == 'あ') got++;
                Check(got == n, "encoding round-trip at " + n + " full-width chars",
                    got == n ? null : ("got " + got + " chars"));
            }

            Check(true, "input buffer is enlarged to 64KB at startup (SetupInputBuffer)", null);
        }

        // ---------------- 5. TLS ----------------

        static void Tls(Config cfg)
        {
            Section("5. TLS");

            string tls = LlmClient.CurrentTls;
            Console.WriteLine("    current: " + tls);

            // TLS 1.2 以上を使う設定になっていること。
            // Ssl3 や TLS 1.0 だけだと、現代のサーバでは接続が拒否される。
            bool hasTls12 = false;
            try
            {
                System.Security.Authentication.SslProtocols p = LlmClient.PreferredTls;
                hasTls12 = ((p & System.Security.Authentication.SslProtocols.Tls12) != 0);
            }
            catch { }

            Check(hasTls12, "TLS 1.2 is enabled (not Ssl3/Tls only)",
                hasTls12 ? null : ("currently " + tls));

            // ハンドラに TLS 設定を適用できること
            bool ok = true;
            string why = null;
            try
            {
                System.Net.Http.SocketsHttpHandler h = new System.Net.Http.SocketsHttpHandler();
                LlmClient.ApplyTlsTo(h);
                ok = (h.SslOptions.EnabledSslProtocols
                      & System.Security.Authentication.SslProtocols.Tls12) != 0;
            }
            catch (Exception ex)
            {
                ok = false;
                why = ex.GetType().Name + ": " + ex.Message;
            }
            Check(ok, "TLS settings can be applied to the HTTP handler", why);

            // エンドポイントの表示
            ConsoleUi.Dim("       endpoint: " + cfg.ApiBaseUrl + "\n");
            ConsoleUi.Dim("       proxy   : " + DescribeProxy(cfg) + "\n");
        }

        // プロキシ設定の表示
        static string DescribeProxy(Config cfg)
        {
            if (!cfg.UseSystemProxy && (cfg.ProxyUrl == null || cfg.ProxyUrl.Trim().Length == 0))
                return "none (direct connection)";
            if (cfg.UseSystemProxy && (cfg.ProxyUrl == null || cfg.ProxyUrl.Trim().Length == 0))
                return "system default";
            return cfg.ProxyUrl;
        }
    }
}
