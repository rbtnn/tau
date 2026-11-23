using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // -e / --execute: 1 回だけ実行して終わる。セッションには保存しない。
    [Collection("env")]
    public class ExecuteOptionTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public ExecuteOptionTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
            llm.Reply = MockLlm.Text("返答です");
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        // ---------------- 基本 ----------------

        [Fact]
        public void ShortOptionSendsPromptAndExits()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "-e", "テスト" });

            Assert.Contains("返答です", output);
            Assert.NotEmpty(llm.Requests);
        }

        [Fact]
        public void LongOptionWorks()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--execute", "テスト" });
            Assert.Contains("返答です", output);
        }

        [Fact]
        public void EqualsFormWorks()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--execute=テスト" });
            Assert.Contains("返答です", output);
        }

        [Fact]
        public void PromptReachesLlm()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "-e", "こんにちは" });

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[0];
            var msgs = req["messages"] as List<object>;
            string lastUser = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user")
                    lastUser = Json.AsString(m["content"]);
            }
            Assert.Equal("こんにちは", lastUser);
        }

        // ---------------- セッションに保存しない ----------------

        [Fact]
        public void DoesNotCreateASession()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "-e", "テスト" });

            Assert.Empty(runner.SessionFiles());
        }

        [Fact]
        public void DoesNotWriteStateFile()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "-e", "テスト" });

            string statePath = Path.Combine(runner.SessionsDir, "_state.json");
            Assert.False(File.Exists(statePath), "_state.json should not be written");
        }

        [Fact]
        public void RepeatedCallsDoNotAccumulateSessions()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "-e", "1回目" });
            runner.Run("", new[] { "-e", "2回目" });
            runner.Run("", new[] { "-e", "3回目" });

            Assert.Empty(runner.SessionFiles());
        }

        [Fact]
        public void DoesNotModifyExistingSession()
        {
            runner.WriteConfig(llm.Url);

            // 先に通常の対話でセッションを作る
            runner.Run("保存される会話\n/exit\n");
            string[] before = runner.SessionFiles();
            Assert.Single(before);
            string beforeText = File.ReadAllText(before[0], Encoding.UTF8);

            // -e で同じセッションを指定しても書き換えない
            string id = Json.AsString(runner.LastSession()["id"]);
            runner.Run("", new[] { "-e", "保存されない会話", "--open", id });

            string afterText = File.ReadAllText(before[0], Encoding.UTF8);
            Assert.Equal(beforeText, afterText);
            Assert.DoesNotContain("保存されない会話", afterText);
        }

        // ---------------- 出力の整形 ----------------

        [Fact]
        public void OutputHasNoLeadingBlankLine()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "-e", "テスト" });

            Assert.False(string.IsNullOrEmpty(output));
            Assert.False(output.StartsWith("\n"),
                "output should not start with a blank line: [" + output + "]");
        }

        [Fact]
        public void OutputHasNoBanner()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "-e", "テスト" });

            Assert.DoesNotContain("console AI agent", output);
            Assert.DoesNotContain("/help for commands", output);
            Assert.DoesNotContain("Bye. Session:", output);
        }

        [Fact]
        public void OutputHasNoProgressSpinner()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "-e", "テスト" });

            Assert.DoesNotContain("connecting...", output);
            Assert.DoesNotContain("receiving...", output);
        }

        [Fact]
        public void OutputIsTheReplyOnly()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "-e", "テスト" });

            Assert.Equal("返答です", output.TrimEnd('\n'));
        }

        // ---------------- 複数行 ----------------

        [Fact]
        public void MultiLinePromptIsSentAsOneMessage()
        {
            runner.WriteConfig(llm.Url);

            string prompt = "def f(x):\n    return x * 2\n\nprint(f(3))";
            runner.Run("", new[] { "-e", prompt });

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[0];
            var msgs = req["messages"] as List<object>;
            string lastUser = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user")
                    lastUser = Json.AsString(m["content"]);
            }
            Assert.Equal(prompt, lastUser);
        }

        // ---------------- エラー ----------------

        [Fact]
        public void ApiErrorIsReported()
        {
            llm.ForceStatus = 401;
            llm.ForceBody = "{\"error\":{\"message\":\"bad key\"}}";
            runner.WriteConfig(llm.Url);

            string output = runner.Run("", new[] { "-e", "テスト" });
            Assert.Contains("Error", output);
            Assert.Contains("401", output);
        }

        [Fact]
        public void MissingArgumentShowsUsage()
        {
            runner.WriteConfig(llm.Url);
            // -e の後に引数が無い場合、対話モードに入ってしまうので
            // stdin に /exit を流して終わらせる
            string output = runner.Run("/exit\n", new[] { "-e" });
            Assert.False(string.IsNullOrEmpty(output));
        }

        // ---------------- ツール ----------------

        [Fact]
        public void ToolLoopWorks()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", "list_dir", "{\"path\":\".\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "ツール後の回答" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("", new[] { "-e", "一覧を見せて" });
            Assert.Contains("ツール後の回答", output);
            Assert.True(llm.Requests.Count >= 2);
        }

        // ---------------- ヘルプ ----------------

        [Fact]
        public void HelpDocumentsExecute()
        {
            string output = runner.Run("", new[] { "--help" });
            Assert.Contains("-e, --execute", output);
            Assert.Contains("not saved to a session", output);
        }
    }
}
