using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Tau;

namespace Tau.Tests
{
    /// <summary>
    /// tau を実際にプロセスとして起動するための補助。
    /// test_tau.py がやっていた「実行時の挙動」を xunit から確認する。
    /// </summary>
    public sealed class TauRunner : IDisposable
    {
        public string WorkDir { get; }
        public string ConfigPath { get; }
        public string DataDir { get; }
        public string SessionsDir { get; }

        readonly Dictionary<string, string> savedEnv = new();
        static readonly string[] EnvKeys =
        {
            "HOME", "XDG_CONFIG_HOME", "APPDATA", "TAU_DATA_DIR", "TAU_HELPERS_DIR",
            "TAU_CONFIG", "TAU_INPUT_ENCODING", "TAU_DEBUG",
            "TAU_AMBIGUOUS_WIDTH", "TAU_TEST_NO_INPUT", "TAU_TEST_WIDTH_FILE",
            "LLM_API_URL", "LLM_API_KEY", "LLM_MODEL", "ALLOW_TOOLS",
            "LLM_EMIT_REASONING", "MAX_TOOLS_PER_TURN", "TOOL_TIMEOUT_SEC",
            "LLM_CONTEXT_WINDOW", "LLM_RESERVE_TOKENS", "LLM_KEEP_RECENT_TOKENS",
            "LLM_PROXY_URL", "LLM_PROXY_USER", "LLM_PROXY_PASS",
            "LLM_USE_SYSTEM_PROXY", "AI_SYSTEM_PROMPT",
        };

        public TauRunner()
        {
            WorkDir = Path.Combine(Path.GetTempPath(),
                "tau-run-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(WorkDir);

            // 実環境の設定を引き継がないよう、対象の環境変数を消す
            foreach (string k in EnvKeys)
            {
                savedEnv[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, null);
            }

            Environment.SetEnvironmentVariable("HOME", WorkDir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME",
                Path.Combine(WorkDir, ".config"));

            DataDir = Path.Combine(WorkDir, ".config", "tau-cli");
            SessionsDir = Path.Combine(DataDir, "sessions");
            Directory.CreateDirectory(SessionsDir);
            ConfigPath = Path.Combine(DataDir, "tau.json");

            // Windows では %APPDATA% を HOME / XDG_CONFIG_HOME で
            // 差し替えられない。TAU_DATA_DIR で保存先を固定する。
            Environment.SetEnvironmentVariable("TAU_DATA_DIR", DataDir);

            // read_document は pdf2text などを別プロセスで呼ぶ。
            // tests.csproj が tests/tau-fx/helpers に用意している。
            string helpers = Path.Combine(RepoRoot(), "tests", "tau-fx", "helpers");
            if (!Directory.Exists(helpers))
                Console.Error.WriteLine("helpers dir missing: " + helpers);
            Environment.SetEnvironmentVariable("TAU_HELPERS_DIR", helpers);
        }

        /// <summary>設定ファイルを書く。</summary>
        public void WriteConfig(string apiUrl, string model = "m",
                                bool allowTools = false, bool emitReasoning = false)
        {
            Dictionary<string, object> d = new();
            d["llmApiUrl"] = apiUrl;
            d["llmApiKey"] = "k";
            d["model"] = model;
            d["allowTools"] = allowTools;
            d["emitReasoning"] = emitReasoning;
            d["useSystemProxy"] = false;
            File.WriteAllText(ConfigPath, Json.ToString(d), Encoding.UTF8);
        }

        static string cachedDll;

        /// <summary>
        /// tau の DLL のパス。
        ///
        /// tau-cli.csproj は自己完結 + 単一ファイル配布なので、テストから
        /// 参照しただけでは `dotnet tau.dll` で起動できない。テスト用に
        /// フレームワーク依存でビルドしたものを使う。
        ///
        /// tests.csproj の BuildTauForTests がテスト実行前にこれを用意する。
        /// 無い場合だけここでビルドする(テスト実行中のビルドは並列実行と
        /// 競合するため、通常は走らない)。
        /// </summary>
        public static string TauDll()
        {
            lock (typeof(TauRunner))
            {
                if (cachedDll != null && File.Exists(cachedDll)) return cachedDll;

                string repo = RepoRoot();
                string outDir = Path.Combine(repo, "tests", "tau-fx", "bin");
                string dll = Path.Combine(outDir, "tau.dll");

                if (File.Exists(dll))
                {
                    cachedDll = dll;
                    return dll;
                }

                // まだ無い場合の保険としてビルドする。
                string buildLog = "";
                try
                {
                    ProcessStartInfo psi = new()
                    {
                        FileName = "dotnet",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        WorkingDirectory = repo,
                    };
                    psi.ArgumentList.Add("build");
                    psi.ArgumentList.Add(Path.Combine(repo, "tau-cli.csproj"));
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("Release");
                    psi.ArgumentList.Add("-p:PublishSingleFile=false");
                    psi.ArgumentList.Add("-p:SelfContained=false");
                    // RuntimeIdentifiers を空で渡すと、Windows の SDK では
                    // NuGet が "Value cannot be null" で失敗する。
                    // テスト用ビルドでは RID を指定しない。
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add(outDir);
                    psi.ArgumentList.Add("--nologo");
                    psi.ArgumentList.Add("-v");
                    psi.ArgumentList.Add("quiet");

                    using Process p = Process.Start(psi);
                    System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(180_000)) throw new TimeoutException("dotnet build timed out");
                    p.WaitForExit();
                    buildLog = so.Result + se.Result;
                }
                catch (Exception ex)
                {
                    buildLog = ex.Message;
                }

                // テスト出力先 (AppContext.BaseDirectory) は使わない。
                // tau-cli.csproj は自己完結なので、dotnet test が参照した
                // 成果物は tau.runtimeconfig.json がフレームワークを指さず、
                // `dotnet tau.dll` では起動できない。
                if (!File.Exists(dll))
                {
                    throw new FileNotFoundException(
                        "tau.dll could not be built in " + outDir
                        + (buildLog.Length > 0 ? "\n--- dotnet build ---\n" + buildLog : ""));
                }

                cachedDll = dll;
                return dll;
            }
        }

        static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "tau-cli.csproj"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>tau を起動して、stdin に流し込む。出力を返す。</summary>
        public string Run(string stdin, string[] args = null, int timeoutSec = 60,
                          Dictionary<string, string> extraEnv = null)
        {
            using Process p = Start(args, extraEnv);
            try
            {
                if (stdin != null)
                {
                    p.StandardInput.Write(stdin);
                    p.StandardInput.Close();
                }

                StringBuilder sb = new();
                StringBuilder err = new();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(true); } catch { }
                    throw new TimeoutException("tau did not exit within " + timeoutSec + "s");
                }
                p.WaitForExit();   // 非同期読み取りの完了を待つ
                // 改行を LF に揃える。Windows では AppendLine が CRLF を
                // 入れるため、テストの期待値が環境で変わってしまう。
                return (sb.ToString() + err.ToString()).Replace("\r\n", "\n");
            }
            catch
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// tau を起動し、出力を読み捨てながら動かし続ける。
        ///
        /// リダイレクトした出力を誰も読まないと、Windows ではパイプが詰まって
        /// プロセスが書き込みで止まる。--web のように動き続けるプロセスは
        /// これを使って起動する。
        /// </summary>
        public BackgroundTau StartBackground(string[] args = null,
                                            Dictionary<string, string> extraEnv = null)
        {
            Process p = Start(args, extraEnv);
            BackgroundTau bg = new(p);
            p.OutputDataReceived += (s, e) => { if (e.Data != null) bg.Log.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) bg.Log.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return bg;
        }

        Process Start(string[] args, Dictionary<string, string> extraEnv)
        {
            List<string> cmd = new() { "dotnet", TauDll() };
            if (args != null) cmd.AddRange(args);

            ProcessStartInfo psi = new()
            {
                FileName = cmd[0],
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = WorkDir,
                // テストは UTF-8 で書き込み、tau も UTF-8 で読む。
                // Windows の既定コードページのままだと日本語が化ける。
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            for (int i = 1; i < cmd.Count; i++) psi.ArgumentList.Add(cmd[i]);

            // パイプ入力でも tau が UTF-8 で読むように指定する。
            psi.Environment["TAU_INPUT_ENCODING"] = "utf-8";

            foreach (KeyValuePair<string, string> kv in savedEnv)
            {
                string v = Environment.GetEnvironmentVariable(kv.Key);
                if (v != null) psi.Environment[kv.Key] = v;
            }
            // テストが設定した保存先は必ず渡す(Windows では %APPDATA% が
            // 差し替わらないため)。
            string dataDir = Environment.GetEnvironmentVariable("TAU_DATA_DIR");
            if (dataDir != null) psi.Environment["TAU_DATA_DIR"] = dataDir;
            string helpersDir = Environment.GetEnvironmentVariable("TAU_HELPERS_DIR");
            if (helpersDir != null) psi.Environment["TAU_HELPERS_DIR"] = helpersDir;
            if (extraEnv != null)
                foreach (KeyValuePair<string, string> kv in extraEnv)
                    psi.Environment[kv.Key] = kv.Value;

            return Process.Start(psi);
        }

        /// <summary>セッションのファイル一覧(新しい順)。</summary>
        public string[] SessionFiles()
        {
            if (!Directory.Exists(SessionsDir)) return Array.Empty<string>();
            string[] files = Directory.GetFiles(SessionsDir, "*.json");
            List<string> list = new();
            foreach (string f in files)
                if (!Path.GetFileName(f).StartsWith("_")) list.Add(f);
            list.Sort();
            return list.ToArray();
        }

        /// <summary>保存されているセッションを全部読む。</summary>
        public List<Dictionary<string, object>> LoadSessions()
        {
            List<Dictionary<string, object>> list = new();
            foreach (string f in SessionFiles())
            {
                try
                {
                    string text = File.ReadAllText(f, Encoding.UTF8);
                    if (Json.Parse(text) is Dictionary<string, object> d) list.Add(d);
                }
                catch { }
            }
            return list;
        }

        /// <summary>最後に保存されたセッション。</summary>
        public Dictionary<string, object> LastSession()
        {
            string[] files = SessionFiles();
            if (files.Length == 0) return null;
            try
            {
                string text = File.ReadAllText(files[files.Length - 1], Encoding.UTF8);
                return Json.Parse(text) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string> kv in savedEnv)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(WorkDir, true); } catch { }
        }
    }

    /// <summary>StartBackground で起動した tau のプロセス。</summary>
    public sealed class BackgroundTau : IDisposable
    {
        public Process Process { get; }
        public StringBuilder Log { get; } = new();

        internal BackgroundTau(Process process)
        {
            Process = process;
        }

        public void Stop()
        {
            try
            {
                if (!Process.HasExited) Process.Kill(true);
                Process.WaitForExit(5000);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { Process.Dispose(); } catch { }
        }
    }
}
