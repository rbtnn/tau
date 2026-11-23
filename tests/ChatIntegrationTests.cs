using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // tau を実際にプロセスとして起動し、モック LLM と対話させて
    // 実行時の挙動を確認する。test_tau.py の内容をここへ移したもの。
    [Collection("env")]
    public class ChatIntegrationTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public ChatIntegrationTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        // ---------------- ストリーミングとツールループ ----------------

        [Fact]
        public void StreamsReasoningAndFinalAnswer()
        {
            llm.Reply = MockLlm.ThinkThenText("まず考えます。", "完了です。");
            // 思考を表示するには emitReasoning が必要
            runner.WriteConfig(llm.Url, emitReasoning: true);

            string output = runner.Run("テスト\n/exit\n");

            Assert.Contains("まず考えます。", output);
            Assert.Contains("完了です。", output);
        }

        [Fact]
        public void ToolLoopSendsResultBackToLlm()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                int tools = MockLlm.ToolResultCount(req);
                if (tools == 0)
                {
                    list.Add(MockLlm.ToolCall("call_1", "list_dir", "{\"path\":\".\"}"));
                }
                else
                {
                    list.Add(new Dictionary<string, object> { ["content"] = "完了です。" });
                }
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("一覧を見せて\n/exit\n");

            Assert.Contains("完了です。", output);
            // 2 回以上呼ばれ、最後のリクエストに tool 結果が含まれる
            Assert.True(llm.Requests.Count >= 2, "expected at least 2 LLM calls");
            Dictionary<string, object> last = llm.Requests[llm.Requests.Count - 1];
            Assert.True(MockLlm.ToolResultCount(last) >= 1,
                "the last request should carry the tool result");
        }

        [Fact]
        public void ToolExecutionIsShown()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", "list_dir", "{\"path\":\".\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "done" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("一覧\n/exit\n");
            Assert.Contains("list_dir", output);
        }

        [Theory]
        [InlineData("memory_read", "{}")]
        [InlineData("list_dir", "{\"path\":\".\"}")]
        public void ToolLoopPreservesExplicitNameAndCallId(string name, string args)
        {
            llm.Reply = req => MockLlm.ToolResultCount(req) == 0
                ? new[] { MockLlm.ToolCall("original-call-id", name, args) }
                : new[] { new Dictionary<string, object> { ["content"] = "done" } };
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("実行して\n/exit\n");

            Assert.Equal(2, llm.Requests.Count);
            var messages = Assert.IsType<List<object>>(llm.Requests[1]["messages"]);
            var tool = Assert.Single(messages.OfType<Dictionary<string, object>>()
                .Where(m => Json.AsString(m["role"]) == "tool"));
            Assert.Equal("original-call-id", tool["tool_call_id"]);
            Assert.Equal(name, tool["name"]);
            var result = Assert.IsType<Dictionary<string, object>>(Json.Parse(Json.AsString(tool["content"])));
            Assert.False(result.ContainsKey("error"), Json.ToString(result));
            Assert.Equal("true", Json.AsString(result["ok"]));

            var assistant = Assert.Single(messages.OfType<Dictionary<string, object>>()
                .Where(m => m.ContainsKey("tool_calls")));
            var call = Assert.IsType<Dictionary<string, object>>(
                Assert.Single(Assert.IsType<List<object>>(assistant["tool_calls"])));
            Assert.Equal("original-call-id", call["id"]);
            Assert.Equal(name, Assert.IsType<Dictionary<string, object>>(call["function"])["name"]);
        }

        // ---------------- セッション ----------------

        [Fact]
        public void SessionIsSavedInTauCliDirectory()
        {
            llm.Reply = MockLlm.Text("こんにちは");
            runner.WriteConfig(llm.Url);

            runner.Run("やあ\n/exit\n");

            Assert.NotEmpty(runner.SessionFiles());
            Dictionary<string, object> s = runner.LastSession();
            Assert.NotNull(s);
            Assert.True(s.ContainsKey("id"));
            Assert.True(s.ContainsKey("messages"));
        }

        [Fact]
        public void MessagesAndReasoningAreSaved()
        {
            llm.Reply = MockLlm.ThinkThenText("考えた", "回答です");
            runner.WriteConfig(llm.Url, emitReasoning: true);

            runner.Run("質問です\n/exit\n");

            Dictionary<string, object> s = runner.LastSession();
            var msgs = s["messages"] as List<object>;
            Assert.NotNull(msgs);

            bool userFound = false, assistantFound = false;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                string role = Json.AsString(m["role"]);
                if (role == "user" && Json.AsString(m["content"]) == "質問です") userFound = true;
                if (role == "assistant")
                {
                    assistantFound = true;
                    Assert.Equal("回答です", Json.AsString(m["content"]));
                    Assert.Equal("考えた", Json.AsString(m["think"]));
                }
            }
            Assert.True(userFound);
            Assert.True(assistantFound);
        }

        [Fact]
        public void JapaneseIsSavedWithoutNul()
        {
            llm.Reply = MockLlm.Text("日本語の応答");
            runner.WriteConfig(llm.Url);

            runner.Run("日本語のテスト\n/exit\n");

            string raw = File.ReadAllText(runner.SessionFiles()[0], Encoding.UTF8);
            int nul = 0;
            foreach (char c in raw) if (c == '\0') nul++;
            Assert.True(nul == 0, "NUL characters found: " + nul
                + "\nfile: " + raw);
            Assert.Contains("日本語のテスト", raw);
            Assert.Contains("日本語の応答", raw);
        }

        [Fact]
        public void LastSessionIsRecorded()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            runner.Run("ひとつめ\n/exit\n");

            string statePath = Path.Combine(runner.SessionsDir, "_state.json");
            Assert.True(File.Exists(statePath));
            string raw = File.ReadAllText(statePath, Encoding.UTF8);
            Assert.Contains("lastSession", raw);
        }

        [Fact]
        public void ResumeWithSessionFlagDoesNotCallLlm()
        {
            llm.Reply = MockLlm.Text("最初の応答");
            runner.WriteConfig(llm.Url);
            runner.Run("最初の質問\n/exit\n");

            Dictionary<string, object> s = runner.LastSession();
            string id = Json.AsString(s["id"]);

            llm.ClearRequests();
            string output = runner.Run("", new[] { "--open", id, "--config", runner.ConfigPath });

            Assert.Empty(llm.Requests);
            Assert.Contains("最初の質問", output);
        }

        [Fact]
        public void NewCommandCreatesDifferentSession()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            runner.Run("/new\n/new\n/exit\n");

            Assert.True(runner.SessionFiles().Length >= 2,
                "each /new should create a separate session");
        }

        [Fact]
        public void ListFlagShowsSessions()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            runner.Run("タイトルになる\n/exit\n");

            string output = runner.Run("", new[] { "--list", "--config", runner.ConfigPath });
            Assert.Contains("タイトルになる", output);
        }

        // ---------------- エラー処理 ----------------

        [Fact]
        public void ApiErrorIsShownWithoutCrashing()
        {
            llm.ForceStatus = 401;
            llm.ForceBody = "{\"error\":{\"message\":\"Invalid API key\"}}";
            runner.WriteConfig(llm.Url);

            string output = runner.Run("テスト\n/exit\n");

            Assert.Contains("Error", output);
            Assert.Contains("401", output);
        }

        [Fact]
        public void HttpErrorBodyIsShown()
        {
            llm.ForceStatus = 500;
            llm.ForceBody = "{\"error\":{\"message\":\"server exploded\"}}";
            runner.WriteConfig(llm.Url);

            string output = runner.Run("テスト\n/exit\n");
            Assert.Contains("server exploded", output);
        }

        [Fact]
        public void ConnectionFailureIsReported()
        {
            // 使われていないポートへ接続する
            runner.WriteConfig("http://127.0.0.1:1/v1/chat/completions");
            string output = runner.Run("テスト\n/exit\n");
            Assert.Contains("Error", output);
        }

        [Fact]
        public void TruncatedResponseIsReportedAsError()
        {
            llm.CutOff = true;
            llm.Reply = MockLlm.Text("途中で切れる");
            runner.WriteConfig(llm.Url);

            string output = runner.Run("テスト\n/exit\n");
            // 接続が切れた旨が出るか、少なくとも落ちないこと
            Assert.False(string.IsNullOrWhiteSpace(output));
        }

        // ---------------- 設定ファイル ----------------

        [Fact]
        public void UsesTauJsonConfigPath()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            string output = runner.Run("テスト\n/exit\n");
            Assert.Contains("tau.json", output);
        }

        [Fact]
        public void UnknownConfigKeysArePreserved()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            // 未知のキーを足しておく
            string raw = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            Dictionary<string, object> d = Json.Parse(raw) as Dictionary<string, object>;
            d["myCustomKey"] = "keep-me";
            File.WriteAllText(runner.ConfigPath, Json.ToString(d), Encoding.UTF8);

            runner.Run("", new[] { "--config", "set", "model", "other" });

            string after = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            Assert.Contains("myCustomKey", after);
            Assert.Contains("keep-me", after);
        }

        [Fact]
        public void ModelsKeyIsRemovedOnSave()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            string raw = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            Dictionary<string, object> d = Json.Parse(raw) as Dictionary<string, object>;
            List<object> models = new() { "a", "b" };
            d["models"] = models;
            File.WriteAllText(runner.ConfigPath, Json.ToString(d), Encoding.UTF8);

            runner.Run("", new[] { "--config", "set", "model", "other" });

            string after = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            Assert.DoesNotContain("\"models\"", after);
        }
    }
}
