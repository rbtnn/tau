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
            if (a < 0 || b < 0) return output;
            return output.Substring(a, b - a);
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
            // tool メッセージが無い古いセッションでも steps で表示できること。
            runner.WriteConfig(llm.Url);

            Dictionary<string, object> sess = new();
            sess["id"] = "s_old_steps";
            sess["title"] = "古いセッション";
            List<object> msgs = new();

            Dictionary<string, object> u = new();
            u["role"] = "user";
            u["content"] = "古い質問";
            msgs.Add(u);

            Dictionary<string, object> a = new();
            a["role"] = "assistant";
            a["content"] = "古い回答";
            List<object> steps = new() { "Tool: shell  ls" };
            a["steps"] = steps;
            msgs.Add(a);

            sess["messages"] = msgs;
            File.WriteAllText(Path.Combine(runner.SessionsDir, "s_old_steps.json"),
                Json.ToString(sess), Encoding.UTF8);

            string output = runner.Run("/exit\n", new[] { "--open", "s_old_steps" });
            Assert.Contains("ls", output);
        }
    }
}
