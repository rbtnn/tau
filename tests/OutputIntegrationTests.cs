using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 出力の整形（改行・進行状況）とデータ移行。
    [Collection("env")]
    public class OutputIntegrationTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public OutputIntegrationTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        // ---------------- 思考・ツール・本文が独立した行になる ----------------

        [Fact]
        public void ThinkToolAndReplyAreOnSeparateLines()
        {
            // 思考 → ツール → 思考 → ツール → 本文 と返す
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                int tools = MockLlm.ToolResultCount(req);
                list.Add(new Dictionary<string, object> { ["reasoning_content"] = "考え中です。" });
                if (tools == 0)
                {
                    list.Add(MockLlm.ToolCall("c1", "list_dir", "{\"path\":\".\"}"));
                }
                else if (tools == 1)
                {
                    list.Add(new Dictionary<string, object> { ["reasoning_content"] = "もう一度考えます。" });
                    list.Add(MockLlm.ToolCall("c2", "list_dir", "{\"path\":\"/tmp\"}"));
                }
                else
                {
                    list.Add(new Dictionary<string, object> { ["content"] = "最終的な回答です。" });
                }
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true, emitReasoning: true);

            string output = runner.Run("テスト\n/exit\n");
            string[] lines = output.Split('\n');

            int think = 0, tool = 0;
            bool reply = false;
            foreach (string l in lines)
            {
                if (l.Contains("[thinking]")) think++;
                if (l.Contains("▶ Tool:")) tool++;
                if (l.Contains("最終的な回答です。")) reply = true;
            }
            Assert.True(think >= 2, "expected at least 2 thinking lines, got " + think);
            Assert.True(tool >= 2, "expected at least 2 tool lines, got " + tool);
            Assert.True(reply, "final reply not found");
        }

        [Fact]
        public void DifferentKindsAreNotJoinedOnOneLine()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                list.Add(new Dictionary<string, object> { ["reasoning_content"] = "考え中" });
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", "list_dir", "{\"path\":\".\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "最終的な回答です。" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true, emitReasoning: true);

            string output = runner.Run("テスト\n/exit\n");
            foreach (string line in output.Split('\n'))
            {
                bool think = line.Contains("[thinking]");
                bool tool = line.Contains("▶");
                bool reply = line.Contains("最終的な回答です。");
                int kinds = (think ? 1 : 0) + (tool ? 1 : 0) + (reply ? 1 : 0);
                Assert.True(kinds <= 1, "line mixes output kinds: " + line);
            }
        }

        [Fact]
        public void ProgressLineIsNotJoinedWithThinkingOrReply()
        {
            // 進行状況は \r で上書きするので、消してから改行する必要がある。
            llm.Reply = MockLlm.ThinkThenText("考え中", "回答です");
            runner.WriteConfig(llm.Url, emitReasoning: true);

            string output = runner.Run("テスト\n/exit\n");
            foreach (string line in output.Split('\n'))
            {
                bool progress = line.Contains("connecting...") || line.Contains("receiving...");
                bool other = line.Contains("[thinking]") || line.Contains("▶ Tool:")
                             || line.Contains("回答です");
                Assert.False(progress && other,
                    "progress line swallowed output: " + line);
            }
        }

        [Fact]
        public void ReplyStartsOnItsOwnLine()
        {
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                list.Add(new Dictionary<string, object> { ["reasoning_content"] = "考え中" });
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("c1", "list_dir", "{\"path\":\".\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "最終的な回答です。" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true, emitReasoning: true);

            string output = runner.Run("テスト\n/exit\n");
            foreach (string line in output.Split('\n'))
            {
                if (!line.Contains("最終的な回答です。")) continue;
                Assert.DoesNotContain("[thinking]", line);
                Assert.DoesNotContain("▶", line);
            }
        }

        // ---------------- 旧データディレクトリからの移行 ----------------

        [Fact]
        public void LegacyDataIsMigrated()
        {
            // %APPDATA%\tau (旧) -> %APPDATA%\tau-cli (新)
            // Linux では ~/.config/tau -> ~/.config/tau-cli
            string oldDir = Path.Combine(runner.WorkDir, ".config", "tau");
            string oldSessions = Path.Combine(oldDir, "sessions");
            Directory.CreateDirectory(oldSessions);

            Dictionary<string, object> cfg = new();
            cfg["llmApiUrl"] = llm.Url;
            cfg["llmApiKey"] = "k";
            cfg["model"] = "migrated-model";
            cfg["allowTools"] = false;
            cfg["useSystemProxy"] = false;
            File.WriteAllText(Path.Combine(oldDir, "tau.json"),
                Json.ToString(cfg), Encoding.UTF8);

            Dictionary<string, object> sess = new();
            sess["id"] = "s_old";
            sess["title"] = "旧セッション";
            List<object> msgs = new();
            msgs.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = "移行テスト",
            });
            sess["messages"] = msgs;
            File.WriteAllText(Path.Combine(oldSessions, "s_old.json"),
                Json.ToString(sess), Encoding.UTF8);

            // 新ディレクトリは TauRunner が既に作っているので、
            // 中身が空の状態で起動する
            Directory.Delete(runner.SessionsDir, true);

            string output = runner.Run("/exit\n");

            Assert.Contains("Migrated data from", output);
            Assert.True(File.Exists(Path.Combine(runner.DataDir, "tau.json")),
                "config should be copied");
            Assert.True(File.Exists(Path.Combine(runner.SessionsDir, "s_old.json")),
                "session should be copied");

            // 旧ディレクトリは残す(安全側)
            Assert.True(Directory.Exists(oldDir), "the old directory must be left in place");
        }

        [Fact]
        public void MigrationDoesNotOverwriteExistingData()
        {
            // 新ディレクトリにデータがあれば移行しない
            string oldDir = Path.Combine(runner.WorkDir, ".config", "tau");
            Directory.CreateDirectory(Path.Combine(oldDir, "sessions"));
            Dictionary<string, object> oldCfg = new();
            oldCfg["model"] = "old-model";
            File.WriteAllText(Path.Combine(oldDir, "tau.json"),
                Json.ToString(oldCfg), Encoding.UTF8);

            // 新ディレクトリに既に設定を置く
            Dictionary<string, object> newCfg = new();
            newCfg["llmApiUrl"] = llm.Url;
            newCfg["llmApiKey"] = "k";
            newCfg["model"] = "new-model";
            newCfg["allowTools"] = false;
            newCfg["useSystemProxy"] = false;
            File.WriteAllText(runner.ConfigPath, Json.ToString(newCfg), Encoding.UTF8);

            string output = runner.Run("/exit\n");

            Assert.DoesNotContain("Migrated data from", output);
            Dictionary<string, object> cfg =
                Json.Parse(File.ReadAllText(runner.ConfigPath, Encoding.UTF8))
                as Dictionary<string, object>;
            Assert.Equal("new-model", Json.AsString(cfg["model"]));
        }

        // ---------------- デバッグ出力 ----------------

        [Fact]
        public void DebugFlagProducesDebugOutput()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            string output = runner.Run("テスト\n/exit\n", new[] { "--debug" });
            Assert.Contains("[debug]", output);
        }

        [Fact]
        public void DebugEnvVarAlsoWorks()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            Dictionary<string, string> env = new() { ["TAU_DEBUG"] = "1" };
            string output = runner.Run("テスト\n/exit\n", extraEnv: env);
            Assert.Contains("[debug]", output);
        }
    }
}
