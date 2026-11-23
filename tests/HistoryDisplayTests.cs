using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 再起動したときの履歴表示。
    // ツール実行が実行時と同じ「▶ Tool: 名前  詳細」で出ることを確認する。
    [Collection("env")]
    public class HistoryDisplayTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public HistoryDisplayTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        // 1 回ツールを呼んでから最終回答を返す。
        static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            OneTool(string name, string argsJson, string final)
        {
            return req =>
            {
                List<Dictionary<string, object>> list = new();
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", name, argsJson));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = final });
                return list;
            };
        }

        // 履歴部分だけを取り出す。
        static string History(string output)
        {
            int a = output.IndexOf("---- conversation so far ----");
            int b = output.IndexOf("---- continue below ----");
            Assert.True(a >= 0 && b > a, "history markers missing:\n" + output);
            return output.Substring(a, b - a);
        }

        string Replay(string messagesJson)
        {
            runner.WriteConfig(llm.Url);
            var session = new Dictionary<string, object>
            {
                ["id"] = "s_history_fixture",
                ["title"] = "History fixture",
                ["messages"] = Json.Parse(messagesJson),
            };
            File.WriteAllText(Path.Combine(runner.SessionsDir, "s_history_fixture.json"),
                Json.ToString(session), Encoding.UTF8);
            string history = History(runner.Run("/exit\n", new[] { "--open", "s_history_fixture" }));
            Assert.Empty(llm.Requests);
            return history;
        }

        [Fact]
        public void LegacyStepsSurviveAlongsideModernTurns()
        {
            string history = Replay("""
                [
                  {"role":"user","content":"old"},
                  {"role":"assistant","content":"old answer","steps":["Tool: shell  echo OLD"]},
                  {"role":"user","content":"new"},
                  {"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"shell","arguments":"{\"cmd\":\"echo NEW\"}"}}]},
                  {"role":"tool","tool_call_id":"c1","name":"shell","content":"{}"},
                  {"role":"assistant","content":"new answer","steps":["Tool: shell  echo NEW"]}
                ]
                """);
            Assert.Contains("▶ Tool: shell  echo OLD", history);
            Assert.Equal(1, history.Split('\n').Count(line => line.Contains("echo NEW")));
        }

        [Fact]
        public void OnlyMatchingStepsAreSuppressed()
        {
            string history = Replay("""
                [
                  {"role":"user","content":"partial history"},
                  {"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"shell","arguments":{"cmd":"echo SAME"}}}]},
                  {"role":"tool","tool_call_id":"c1","name":"shell","content":"{}"},
                  {"role":"assistant","content":"done","steps":["Tool: shell  echo SAME","Tool: shell  echo SAME","Tool: read_file  missing.txt","Other progress"]}
                ]
                """);
            Assert.Equal(2, history.Split('\n').Count(line => line.Contains("echo SAME")));
            Assert.Contains("▶ Tool: read_file  missing.txt", history);
            Assert.Contains("▶ Other progress", history);
        }

        [Theory]
        [InlineData("{\"role\":\"user\",\"content\":\"next turn\"},")]
        [InlineData("{\"role\":\"assistant\",\"tool_calls\":[]},")]
        public void OrphanToolDoesNotReuseArgumentsFromAnEarlierBatch(string boundary)
        {
            string history = Replay("""
                [
                  {"role":"user","content":"first"},
                  {"role":"assistant","tool_calls":[{"id":"reused","function":{"name":"shell","arguments":{"cmd":"echo FIRST"}}}]},
                  {"role":"tool","tool_call_id":"reused","name":"shell","content":"{}"},
                """ + boundary + """
                  {"role":"tool","tool_call_id":"reused","name":"shell","content":"{}"}
                ]
                """);
            Assert.Equal(1, history.Split('\n').Count(line => line.Contains("echo FIRST")));
            Assert.Contains("  ▶ Tool: shell\n", history.Replace("\r\n", "\n"));
        }

        [Fact]
        public void IncompleteHistoryDoesNotPreventReplay()
        {
            string history = Replay("""
                [
                  null, 123, {},
                  {"role":"user","content":"question"},
                  {"role":"assistant","tool_calls":[null,{}, {"id":"broken","function":{"arguments":"not json"}}]},
                  {"role":"tool","name":"read_file","tool_call_id":"broken"},
                  {"role":"assistant","content":"answer"}
                ]
                """);
            Assert.Contains("▶ Tool: read_file", history);
            Assert.Contains("answer", history);
        }

        [Fact]
        public void ToolDetailIsShownAfterRestart()
        {
            llm.Reply = OneTool("shell", "{\"cmd\":\"echo HELLO\"}", "完了です");
            runner.WriteConfig(llm.Url, allowTools: true);

            // 1 回目の実行(セッションを作る)
            runner.Run("調査して\n/exit\n");

            // 2 回目は再起動して履歴を表示するだけ
            llm.ClearRequests();
            string output = runner.Run("/exit\n");

            string h = History(output);
            Assert.Contains("▶ Tool: shell", h);
            Assert.Contains("echo HELLO", h);
        }

        [Fact]
        public void ToolDetailUsesRuntimeFormat()
        {
            llm.Reply = OneTool("read_file", "{\"path\":\"src/Chat.cs\"}", "読んだ");
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("読んで\n/exit\n");
            string output = runner.Run("/exit\n");

            string h = History(output);
            // 実行時と同じ形式(名前 + 2 スペース + 詳細)
            Assert.Contains("▶ Tool: read_file", h);
            Assert.Contains("src/Chat.cs", h);
        }

        [Fact]
        public void ToolIsNotShownTwice()
        {
            // tool メッセージと steps の両方から出ると二重になる。
            llm.Reply = OneTool("shell", "{\"cmd\":\"echo ONE\"}", "完了");
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("調査\n/exit\n");
            string output = runner.Run("/exit\n");

            string h = History(output);
            int count = 0;
            foreach (string line in h.Split('\n'))
                if (line.Contains("echo ONE")) count++;

            Assert.Equal(1, count);
        }

        [Fact]
        public void MultipleToolsAreShownInOrder()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                int n = MockLlm.ToolResultCount(req);
                if (n == 0)
                {
                    // 2 つの呼び出しを別々の delta として送る
                    list.Add(MockLlm.ToolCall("a1", "shell", "{\"cmd\":\"ls -la\"}", 0));
                    list.Add(MockLlm.ToolCall("a2", "read_file", "{\"path\":\"a.txt\"}", 1));
                }
                else
                {
                    list.Add(new Dictionary<string, object> { ["content"] = "完了" });
                }
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("調査\n/exit\n");
            string output = runner.Run("/exit\n");

            string h = History(output);
            int shellAt = h.IndexOf("ls -la");
            int readAt = h.IndexOf("a.txt");
            Assert.True(shellAt >= 0, "shell detail missing:\n" + h);
            Assert.True(readAt >= 0, "read_file detail missing:\n" + h);
            Assert.True(shellAt < readAt, "tools should be shown in call order");
        }

        [Fact]
        public void ThinkingIsStillShown()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                list.Add(new Dictionary<string, object> { ["reasoning_content"] = "考え中" });
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", "shell", "{\"cmd\":\"echo T\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "完了" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true, emitReasoning: true);

            runner.Run("調査\n/exit\n");
            string output = runner.Run("/exit\n");

            Assert.Contains("[thinking]", History(output));
        }

        [Fact]
        public void HistoryDoesNotCallTheLlm()
        {
            llm.Reply = OneTool("shell", "{\"cmd\":\"echo X\"}", "完了");
            runner.WriteConfig(llm.Url, allowTools: true);
            runner.Run("調査\n/exit\n");

            llm.ClearRequests();
            runner.Run("/exit\n");
            Assert.Empty(llm.Requests);
        }

        [Fact]
        public void NoToolMessagesFallsBackToSteps()
        {
            string history = Replay("""
                [
                  {"role":"user","content":"古い質問"},
                  {"role":"assistant","content":"古い回答","steps":["Tool: shell  ls"]}
                ]
                """);
            Assert.Contains("▶ Tool: shell  ls", history);
            Assert.Contains("古い回答", history);
        }
    }
}
