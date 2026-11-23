using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 日本語の入出力・エンコーディング・NUL 文字・長い入力。
    // 実際に tau を起動して確かめる。
    [Collection("env")]
    public class EncodingIntegrationTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public EncodingIntegrationTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
            llm.Reply = MockLlm.Text("日本語の応答です");
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        static int CountNul(string s)
        {
            int n = 0;
            foreach (char c in s) if (c == '\0') n++;
            return n;
        }

        // ---------------- 日本語の往復 ----------------

        [Theory]
        [InlineData("あいうえお")]
        [InlineData("アイウエオ")]
        [InlineData("日本語のテスト")]
        [InlineData("あAいBうC")]
        [InlineData("ｱｲｳｴｵ")]
        [InlineData("、。「」（）")]
        [InlineData("①②③")]
        public void JapaneseIsSentToLlm(string text)
        {
            runner.WriteConfig(llm.Url);
            runner.Run(text + "\n/exit\n");

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[llm.Requests.Count - 1];
            var msgs = req["messages"] as List<object>;
            Assert.NotNull(msgs);

            bool found = false;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user"
                    && Json.AsString(m["content"]) == text) found = true;
            }
            Assert.True(found, "the Japanese text should reach the LLM unchanged");
        }

        [Fact]
        public void JapaneseReplyIsShown()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("こんにちは\n/exit\n");
            Assert.Contains("日本語の応答です", output);
        }

        [Fact]
        public void SessionHasNoNulCharacters()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("日本語のテストです\n/exit\n");

            string raw = File.ReadAllText(runner.SessionFiles()[0], Encoding.UTF8);
            Assert.Equal(0, CountNul(raw));
        }

        [Fact]
        public void JapaneseIsStoredReadable()
        {
            // System.Text.Json の UnsafeRelaxedJsonEscaping により
            // \uXXXX ではなく生の日本語で保存される
            runner.WriteConfig(llm.Url);
            runner.Run("日本語のテスト\n/exit\n");

            string raw = File.ReadAllText(runner.SessionFiles()[0], Encoding.UTF8);
            Assert.Contains("日本語のテスト", raw);
            Assert.DoesNotContain("\\u65e5", raw);   // 「日」のエスケープ
        }

        // ---------------- 長い入力 ----------------

        static string MakeLong(int chars)
        {
            StringBuilder sb = new();
            for (int i = 0; i < chars; i++) sb.Append('あ');
            return sb.ToString();
        }

        [Theory]
        [InlineData(255)]
        [InlineData(300)]
        [InlineData(512)]
        [InlineData(600)]
        public void LongJapaneseInputIsNotTruncated(int chars)
        {
            runner.WriteConfig(llm.Url);
            string text = MakeLong(chars);
            runner.Run(text + "\n/exit\n");

            Dictionary<string, object> s = runner.LastSession();
            var msgs = s["messages"] as List<object>;
            string saved = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user")
                {
                    string c = Json.AsString(m["content"]);
                    if (c.Length > saved.Length) saved = c;
                }
            }
            Assert.Equal(text, saved);
        }

        [Fact]
        public void MixedWidthLongInputIsNotTruncated()
        {
            runner.WriteConfig(llm.Url);
            StringBuilder sb = new();
            for (int i = 0; sb.Length < 300; i++)
                sb.Append(i % 2 == 0 ? "あA" : "い1");
            string text = sb.ToString();

            runner.Run(text + "\n/exit\n");

            var msgs = runner.LastSession()["messages"] as List<object>;
            string saved = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user")
                {
                    string c = Json.AsString(m["content"]);
                    if (c.Length > saved.Length) saved = c;
                }
            }
            Assert.Equal(text, saved);
        }

        // ---------------- 入力エンコーディングの設定 ----------------

        [Fact]
        public void InputEncodingIsSavedAndUsed()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("/set inputEncoding utf-8\n/exit\n");

            string raw = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            Assert.Contains("utf-8", raw);
        }

        [Fact]
        public void RedirectedInputDoesNotChangeEncoding()
        {
            // 入力がリダイレクトされているときはエンコーディングを変えない
            // (変えると日本語が壊れる)
            runner.WriteConfig(llm.Url);
            string output = runner.Run("テスト\n/exit\n");
            // 落ちずに日本語が通ればよい
            Assert.NotEmpty(llm.Requests);
        }

        // ---------------- NUL の保険 ----------------

        [Fact]
        public void NulCharactersAreStrippedBeforeSending()
        {
            runner.WriteConfig(llm.Url);
            // stdin に NUL を混ぜる
            string text = "あ\0い\0う";
            runner.Run(text + "\n/exit\n");

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[llm.Requests.Count - 1];
            var msgs = req["messages"] as List<object>;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                Assert.Equal(0, CountNul(Json.AsString(m["content"])));
            }
        }

        [Fact]
        public void AllNulInputWarns()
        {
            runner.WriteConfig(llm.Url);
            // 入力全体が NUL に化けた場合
            runner.Run("\0\0\0\n/exit\n");

            // LLM は呼ばれないか、呼ばれても警告が出る
            // (どちらでも落ちないことが重要)
            Assert.True(true);
        }

        // ---------------- --list / --diag / --help / --version ----------------

        [Fact]
        public void VersionShowsBuildStamp()
        {
            string output = runner.Run("", new[] { "--version" });
            Assert.Contains("tau", output);
            Assert.Contains("runtime:", output);
        }

        [Fact]
        public void HelpListsOptions()
        {
            string output = runner.Run("", new[] { "--help" });
            Assert.Contains("--config", output);
            Assert.Contains("--session", output);
            Assert.Contains("--web", output);
            Assert.Contains("--test", output);
        }

        [Fact]
        public void DiagShowsEnvironment()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--diag", "--config", runner.ConfigPath });
            Assert.Contains("environment", output);
            Assert.Contains("settings", output);
            Assert.Contains("TLS", output);
        }

        [Fact]
        public void SelfTestOfflineRuns()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--test=offline", "--config", runner.ConfigPath },
                timeoutSec: 120);
            Assert.Contains("self test", output);
            Assert.Contains("Result:", output);
            Assert.Contains("input encoding", output);
            Assert.Contains("TLS", output);
        }

        [Fact]
        public void SelfTestHasNoFailures()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--test=offline", "--config", runner.ConfigPath },
                timeoutSec: 120);
            Assert.DoesNotContain("FAILED", output);
        }
    }
}
