using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // --web のうち、HTTP を介さずに確かめられる部分を検証する。
    // 実際の HTTP は test_tau.py が担当する。
    [Collection("env")]
    public class WebServerTests : IDisposable
    {
        private readonly string _dir;
        private readonly Dictionary<string, string> _savedEnv = new();
        private static readonly string[] EnvKeys = { "HOME", "XDG_CONFIG_HOME", "APPDATA" };

        public WebServerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "tau-web-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
            foreach (string k in EnvKeys)
            {
                _savedEnv[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, null);
            }
            Environment.SetEnvironmentVariable("HOME", _dir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_dir, ".config"));
        }

        public void Dispose()
        {
            foreach (var kv in _savedEnv)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---------------- 埋め込み画面 ----------------

        [Fact]
        public void IndexHtmlIsEmbedded()
        {
            Assembly asm = typeof(WebServer).Assembly;
            bool found = false;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("www.index.html", StringComparison.Ordinal)) found = true;
            }
            Assert.True(found, "index.html must be embedded in the assembly");
        }

        [Fact]
        public void IndexHtmlHasComposerAndSessionList()
        {
            Assembly asm = typeof(WebServer).Assembly;
            string name = null;
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith("www.index.html", StringComparison.Ordinal)) { name = n; break; }
            }
            Assert.NotNull(name);

            using Stream s = asm.GetManifestResourceStream(name);
            using StreamReader r = new(s, System.Text.Encoding.UTF8);
            string html = r.ReadToEnd();

            Assert.Contains("id=\"composer\"", html);
            Assert.Contains("id=\"sessionList\"", html);
            Assert.Contains("/api/chat/stream", html);
            Assert.Contains("/api/sessions", html);
            // 設定画面は要件外なので、設定系の API を呼ばないこと
            Assert.DoesNotContain("/api/config", html);
            Assert.DoesNotContain("/api/model", html);
            Assert.DoesNotContain("/api/tools", html);
        }

        // ---------------- サーバの起動と停止 ----------------

        [Fact]
        public void StartAndStopOnFreePort()
        {
            var cfg = new Config("");
            var server = new WebServer(cfg);
            server.Start(0);   // 0 は「空きポートを選ぶ」
            try
            {
                Assert.True(server.Port > 0);
                Assert.StartsWith("http://127.0.0.1:", server.Url);
                Assert.EndsWith("/", server.Url);
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public void PortIsReportedInUrl()
        {
            var cfg = new Config("");
            var server = new WebServer(cfg);
            server.Start(0);
            try
            {
                Assert.Contains(server.Port.ToString(), server.Url);
            }
            finally { server.Stop(); }
        }

        // ---------------- 同時実行の制御 ----------------

        [Fact]
        public void BusyStartsFalse()
        {
            var cfg = new Config("");
            var server = new WebServer(cfg);
            server.Start(0);
            try
            {
                // 起動直後は空いている
                Assert.True(server.Port > 0);
            }
            finally { server.Stop(); }
        }

        // ---------------- イベント名 ----------------

        [Fact]
        public void EventNamesAreStable()
        {
            // 画面側 (index.html) がこの名前で分岐しているので、変えると壊れる
            Assert.Equal("think", WebEvents.Think);
            Assert.Equal("tool", WebEvents.Tool);
            Assert.Equal("delta", WebEvents.Delta);
            Assert.Equal("done", WebEvents.Done);
            Assert.Equal("error", WebEvents.Error);
            Assert.Equal("log", WebEvents.Log);
        }

        [Fact]
        public void IndexHtmlListensToThoseEvents()
        {
            Assembly asm = typeof(WebServer).Assembly;
            string name = null;
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.EndsWith("www.index.html", StringComparison.Ordinal)) { name = n; break; }
            }
            using Stream s = asm.GetManifestResourceStream(name);
            using StreamReader r = new(s, System.Text.Encoding.UTF8);
            string html = r.ReadToEnd();

            Assert.Contains("\"delta\"", html);
            Assert.Contains("\"think\"", html);
            Assert.Contains("\"tool\"", html);
            Assert.Contains("\"done\"", html);
            Assert.Contains("\"error\"", html);
        }

        // ---------------- ConsoleUi の差し替え ----------------

        [Fact]
        public void ConsoleUiSinkCapturesOutput()
        {
            var captured = new List<string>();
            try
            {
                ConsoleUi.SetSink(captured.Add, false);
                ConsoleUi.Info("hello");
                ConsoleUi.Dim("dim");
                ConsoleUi.Danger("bad");

                string all = string.Concat(captured);
                Assert.Contains("hello", all);
                Assert.Contains("dim", all);
                Assert.Contains("bad", all);
                // 色は付けない設定なので ANSI エスケープが入らないこと
                Assert.DoesNotContain("\u001b[", all);
            }
            finally
            {
                ConsoleUi.ResetSink();
            }
        }

        [Fact]
        public void ConsoleUiToolGoesToSink()
        {
            var captured = new List<string>();
            try
            {
                ConsoleUi.SetSink(captured.Add, false);
                ConsoleUi.Tool("shell", "echo hi");
                string all = string.Concat(captured);
                Assert.Contains("▶ Tool:", all);
                Assert.Contains("shell", all);
                Assert.Contains("echo hi", all);
            }
            finally { ConsoleUi.ResetSink(); }
        }

        [Fact]
        public void ResetSinkRestoresConsole()
        {
            var captured = new List<string>();
            ConsoleUi.SetSink(captured.Add, false);
            ConsoleUi.ResetSink();
            // ResetSink 後は捕捉されない
            int before = captured.Count;
            ConsoleUi.Dim("after-reset");
            Assert.Equal(before, captured.Count);
        }
    }
}
