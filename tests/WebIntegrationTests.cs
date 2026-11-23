using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // tau --web を実際に起動し、HTTP で叩いて確認する。
    [Collection("env")]
    public class WebIntegrationTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public WebIntegrationTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        static int FreePort()
        {
            System.Net.Sockets.TcpListener probe =
                new(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// tau --web を起動して、応答するまで待つ。
        ///
        /// リダイレクトした出力を読まないと、Windows ではパイプが詰まって
        /// サーバが応答しなくなる。StartBackground は出力を読み捨てる。
        /// </summary>
        (BackgroundTau proc, string baseUrl) StartWeb(int port)
        {
            Dictionary<string, string> env = new()
            {
                ["HOME"] = runner.WorkDir,
                ["XDG_CONFIG_HOME"] = Path.Combine(runner.WorkDir, ".config"),
            };

            BackgroundTau p = runner.StartBackground(new[]
            {
                "--web",
                "--port",
                port.ToString(),
                "--config",
                runner.ConfigPath,
            }, env);

            string baseUrl = "http://127.0.0.1:" + port;
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < 80; i++)
            {
                try
                {
                    HttpResponseMessage r = client.GetAsync(baseUrl + "/api/health").Result;
                    if (r.IsSuccessStatusCode) return (p, baseUrl);
                }
                catch { }
                if (p.Process.HasExited) break;
                Thread.Sleep(200);
            }
            string log = p.Log.ToString();
            p.Dispose();
            throw new Exception("the web server did not start on " + baseUrl
                + (log.Length > 0 ? "\n--- tau output ---\n" + log : ""));
        }

        static void StopWeb(BackgroundTau p)
        {
            p.Dispose();
        }

        // ---------------- 画面と API ----------------

        [Fact]
        public void ServesThePage()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                string html = c.GetStringAsync(url + "/").Result;
                Assert.Contains("id=\"composer\"", html);
                Assert.Contains("id=\"sessionList\"", html);
                Assert.Contains("/api/chat/stream", html);
                // 設定画面は要件外
                Assert.DoesNotContain("/api/config", html);
                Assert.DoesNotContain("/api/model", html);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void HealthEndpointResponds()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                string body = c.GetStringAsync(url + "/api/health").Result;
                Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                Assert.Equal("true", Json.AsString(d["ok"]));
                Assert.Equal("false", Json.AsString(d["busy"]));
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void SessionsListIsJson()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                string body = c.GetStringAsync(url + "/api/sessions").Result;
                Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
                Assert.Equal("true", Json.AsString(d["ok"]));
                Assert.IsType<List<object>>(d["sessions"]);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void ChatStreamsOverSse()
        {
            llm.Reply = MockLlm.ThinkThenText("考えた", "こんにちは");
            runner.WriteConfig(llm.Url, emitReasoning: true);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                Dictionary<string, object> body = new()
                {
                    ["message"] = "テストです",
                    ["session"] = "",
                };
                StringContent content = new(Json.ToString(body), Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Origin", url);

                string sse = c.PostAsync(url + "/api/chat/stream", content).Result
                    .Content.ReadAsStringAsync().Result;

                Assert.Contains("event: delta", sse);
                Assert.Contains("event: done", sse);
                Assert.Contains("こんにちは", sse);
                Assert.Contains("event: think", sse);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void ChatCreatesSessionAndHistoryIsReadable()
        {
            llm.Reply = MockLlm.Text("Web応答");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                Dictionary<string, object> body = new()
                {
                    ["message"] = "Webからの質問",
                    ["session"] = "",
                };
                StringContent content = new(Json.ToString(body), Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Origin", url);
                string sse = c.PostAsync(url + "/api/chat/stream", content).Result
                    .Content.ReadAsStringAsync().Result;

                // done からセッション ID を取り出す
                string sid = "";
                foreach (string line in sse.Split('\n'))
                {
                    if (!line.StartsWith("data:")) continue;
                    string data = line.Substring(5).Trim();
                    if (data.Length == 0 || data == "{}") continue;
                    try
                    {
                        Dictionary<string, object> d = Json.Parse(data) as Dictionary<string, object>;
                        if (d != null && d.ContainsKey("session"))
                            sid = Json.AsString(d["session"]);
                    }
                    catch { }
                }
                Assert.False(string.IsNullOrEmpty(sid), "done should carry a session id:\n" + sse);

                string hist = c.GetStringAsync(url + "/api/sessions/" + sid).Result;
                Dictionary<string, object> h = Json.Parse(hist) as Dictionary<string, object>;
                Assert.Equal("true", Json.AsString(h["ok"]));

                var msgs = h["messages"] as List<object>;
                Assert.NotNull(msgs);
                bool user = false, assistant = false;
                foreach (object o in msgs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (Json.AsString(m["role"]) == "user") user = true;
                    if (Json.AsString(m["role"]) == "assistant") assistant = true;
                }
                Assert.True(user);
                Assert.True(assistant);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void CrossOriginPostIsRefused()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                Dictionary<string, object> body = new() { ["message"] = "x" };
                StringContent content = new(Json.ToString(body), Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Origin", "http://evil.example");

                HttpResponseMessage r = c.PostAsync(url + "/api/chat/stream", content).Result;
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, r.StatusCode);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void UnknownPathReturns404()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                HttpResponseMessage r = c.GetAsync(url + "/api/nope").Result;
                Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void SettingsEndpointsDoNotExist()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                foreach (string path in new[] { "/api/config", "/api/model", "/api/tools", "/api/ui" })
                {
                    Dictionary<string, object> body = new();
                    StringContent content = new(Json.ToString(body), Encoding.UTF8,
                        "application/json");
                    content.Headers.Add("Origin", url);
                    HttpResponseMessage r = c.PostAsync(url + path, content).Result;
                    Assert.True(
                        r.StatusCode == System.Net.HttpStatusCode.NotFound
                        || r.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed,
                        path + " should not exist, got " + r.StatusCode);
                }
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void EmptyMessageIsRejected()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                Dictionary<string, object> body = new() { ["message"] = "   " };
                StringContent content = new(Json.ToString(body), Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Origin", url);
                string sse = c.PostAsync(url + "/api/chat/stream", content).Result
                    .Content.ReadAsStringAsync().Result;
                Assert.Contains("bad_request", sse);
            }
            finally { StopWeb(p); }
        }

        [Fact]
        public void WebDoesNotCreateTauWebConfig()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            int port = FreePort();
            (BackgroundTau p, string url) = StartWeb(port);
            try
            {
                using HttpClient c = new();
                c.GetStringAsync(url + "/api/sessions").Wait();
            }
            finally { StopWeb(p); }

            Assert.False(File.Exists(Path.Combine(runner.DataDir, "tau-web.json")),
                "tau --web must not create a separate config");
        }
    }
}
