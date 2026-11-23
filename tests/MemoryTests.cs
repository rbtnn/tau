using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // メモリ機能。
    // セッションをまたいで共通の覚え書きを、エージェントが自分で読み書きする。
    [Collection("env")]
    public class MemoryTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public MemoryTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        string MemoryPath()
        {
            return Path.Combine(runner.DataDir, "memory.json");
        }

        Dictionary<string, object> ReadMemoryFile()
        {
            string path = MemoryPath();
            if (!File.Exists(path)) return null;
            return Json.Parse(File.ReadAllText(path, Encoding.UTF8))
                as Dictionary<string, object>;
        }

        // ---------------- Memory クラス単体 ----------------

        [Fact]
        public void SaveAndLoad()
        {
            var cfg = new Config("");
            string error;
            Assert.True(Memory.Save("覚え書き", cfg, out error), error);
            Assert.Equal("覚え書き", Memory.Load());
        }

        [Fact]
        public void LoadReturnsEmptyWhenMissing()
        {
            Assert.Equal("", Memory.Load());
        }

        [Fact]
        public void ClearEmptiesMemory()
        {
            var cfg = new Config("");
            string error;
            Memory.Save("何か", cfg, out error);
            Memory.Clear(cfg, out error);
            Assert.Equal("", Memory.Load());
        }

        [Fact]
        public void RejectsOversizedText()
        {
            var cfg = new Config("");
            cfg.MemoryMaxChars = 10;
            string error;
            Assert.False(Memory.Save("12345678901", cfg, out error));
            Assert.Contains("too long", error);
        }

        [Fact]
        public void AcceptsTextAtTheLimit()
        {
            var cfg = new Config("");
            cfg.MemoryMaxChars = 10;
            string error;
            Assert.True(Memory.Save("1234567890", cfg, out error), error);
        }

        [Fact]
        public void ComposeAddsMemoryToSystemPrompt()
        {
            string sys = "You are tau.";
            string composed = Memory.Compose(sys, "ユーザーは tau を開発中");
            Assert.Contains("You are tau.", composed);
            Assert.Contains("[Memory]", composed);
            Assert.Contains("ユーザーは tau を開発中", composed);
        }

        [Fact]
        public void ComposeLeavesPromptUnchangedWhenEmpty()
        {
            string sys = "You are tau.";
            Assert.Equal(sys, Memory.Compose(sys, ""));
            Assert.Equal(sys, Memory.Compose(sys, null));
            Assert.Equal(sys, Memory.Compose(sys, "   "));
        }

        [Fact]
        public void UsageShowsCountAndLimit()
        {
            var cfg = new Config("");
            cfg.MemoryMaxChars = 100;
            Assert.Equal("5/100 chars", Memory.Usage("12345", cfg));
        }

        // ---------------- ツール経由 ----------------

        [Fact]
        public void MemoryToolsAreDefined()
        {
            List<object> defs = ToolExecutor.Definitions();
            List<string> names = new();
            foreach (object o in defs)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                Dictionary<string, object> fn = d["function"] as Dictionary<string, object>;
                names.Add(Json.AsString(fn["name"]));
            }
            Assert.Contains("memory_read", names);
            Assert.Contains("memory_write", names);
            Assert.Contains("memory_append", names);
            Assert.Contains("memory_clear", names);
        }

        [Fact]
        public void MemoryWriteToolStoresText()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            string result = ToolExecutor.Execute("memory_write",
                "{\"text\":\"ツールから書いた\"}", cfg);

            Dictionary<string, object> d = Json.Parse(result) as Dictionary<string, object>;
            Assert.Equal("true", Json.AsString(d["ok"]));
            Assert.Equal("ツールから書いた", Memory.Load());
        }

        [Fact]
        public void MemoryAppendKeepsExistingText()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            ToolExecutor.Execute("memory_write", "{\"text\":\"1行目\"}", cfg);
            ToolExecutor.Execute("memory_append", "{\"text\":\"2行目\"}", cfg);

            Assert.Equal("1行目\n2行目", Memory.Load());
        }

        [Fact]
        public void MemoryReadToolReturnsContents()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            ToolExecutor.Execute("memory_write", "{\"text\":\"読むべき内容\"}", cfg);

            string result = ToolExecutor.Execute("memory_read", "{}", cfg);
            Dictionary<string, object> d = Json.Parse(result) as Dictionary<string, object>;
            Assert.Equal("読むべき内容", Json.AsString(d["memory"]));
        }

        [Fact]
        public void MemoryClearToolErases()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            ToolExecutor.Execute("memory_write", "{\"text\":\"消す\"}", cfg);
            ToolExecutor.Execute("memory_clear", "{}", cfg);
            Assert.Equal("", Memory.Load());
        }

        [Fact]
        public void MemoryWriteToolRejectsOversized()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            cfg.MemoryMaxChars = 10;

            StringBuilder sb = new();
            for (int i = 0; i < 20; i++) sb.Append('x');
            string result = ToolExecutor.Execute("memory_write",
                "{\"text\":\"" + sb + "\"}", cfg);

            Dictionary<string, object> d = Json.Parse(result) as Dictionary<string, object>;
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("too long", Json.AsString(d["error"]));
            Assert.Equal("", Memory.Load());
        }

        [Fact]
        public void MemoryAppendToolRejectsOversized()
        {
            var cfg = new Config("");
            cfg.AllowTools = true;
            cfg.MemoryMaxChars = 20;
            ToolExecutor.Execute("memory_write", "{\"text\":\"123456789012345\"}", cfg);

            string result = ToolExecutor.Execute("memory_append",
                "{\"text\":\"6789012345\"}", cfg);
            Dictionary<string, object> d = Json.Parse(result) as Dictionary<string, object>;
            Assert.True(d.ContainsKey("error"));
        }

        // ---------------- エージェント経由 ----------------

        [Fact]
        public void MemoryIsIncludedInSystemPrompt()
        {
            // メモリを先に保存しておき、リクエストの system に含まれるか見る
            var cfg = new Config("");
            cfg.MemoryMaxChars = 8000;
            string error;
            Memory.Save("以前の会話で決めたこと", cfg, out error);

            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);
            runner.Run("テスト\n/exit\n");

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[0];
            var msgs = req["messages"] as List<object>;
            string sys = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "system") sys = Json.AsString(m["content"]);
            }
            Assert.Contains("[Memory]", sys);
            Assert.Contains("以前の会話で決めたこと", sys);
        }

        [Fact]
        public void MemoryIsSharedAcrossSessions()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            // 1 つ目のセッションで保存
            var cfg = new Config("");
            string error;
            Memory.Save("全セッション共通のメモ", cfg, out error);
            runner.Run("セッション1\n/exit\n");

            // 2 つ目のセッションでも system に含まれる
            llm.ClearRequests();
            runner.Run("/new\nセッション2\n/exit\n");

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[llm.Requests.Count - 1];
            var msgs = req["messages"] as List<object>;
            string sys = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "system") sys = Json.AsString(m["content"]);
            }
            Assert.Contains("全セッション共通のメモ", sys);
        }

        [Fact]
        public void MemoryIsNotStoredInSessions()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            var cfg = new Config("");
            string error;
            Memory.Save("セッションには入れない", cfg, out error);
            runner.Run("テスト\n/exit\n");

            Dictionary<string, object> s = runner.LastSession();
            var msgs = s["messages"] as List<object>;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                Assert.DoesNotContain("セッションには入れない",
                    Json.AsString(m.ContainsKey("content") ? m["content"] : ""));
            }
        }

        [Fact]
        public void AgentCanWriteMemoryThroughTool()
        {
            // モックが memory_write を呼び、実際に保存されること
            llm.Reply = req =>
            {
                List<Dictionary<string, object>> list = new();
                if (MockLlm.ToolResultCount(req) == 0)
                    list.Add(MockLlm.ToolCall("m1", "memory_write", "{\"text\":\"エージェントが書いた\"}"));
                else
                    list.Add(new Dictionary<string, object> { ["content"] = "覚えました" });
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("覚えて\n/exit\n");
            Assert.Contains("覚えました", output);
            Assert.Equal("エージェントが書いた", Memory.Load());
        }

        [Fact]
        public void MemoryFileIsUnderTheDataDirectory()
        {
            llm.Reply = MockLlm.Text("ok");
            runner.WriteConfig(llm.Url);

            var cfg = new Config("");
            string error;
            Memory.Save("保存先の確認", cfg, out error);

            Assert.True(File.Exists(MemoryPath()),
                "memory.json should live in the tau-cli data directory: " + MemoryPath());
        }

        [Fact]
        public void MemoryJsonContainsTextAndTimestamp()
        {
            var cfg = new Config("");
            string error;
            Memory.Save("タイムスタンプ確認", cfg, out error);

            Dictionary<string, object> d = ReadMemoryFile();
            Assert.NotNull(d);
            Assert.Equal("タイムスタンプ確認", Json.AsString(d["text"]));
            Assert.False(string.IsNullOrEmpty(Json.AsString(d["updatedAt"])));
        }

        // ---------------- 設定 ----------------

        [Fact]
        public void MemoryMaxCharsIsInSettings()
        {
            Settings.Field f = Settings.Find("memoryMaxChars");
            Assert.NotNull(f);
            Assert.Equal("int", f.Kind);
        }

        [Fact]
        public void MemoryMaxCharsCanBeSetFromCommandLine()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "--config", "set", "memoryMaxChars", "500" });

            Dictionary<string, object> cfg =
                Json.Parse(File.ReadAllText(runner.ConfigPath, Encoding.UTF8))
                as Dictionary<string, object>;
            Assert.Equal(500, Json.AsInt(cfg["memoryMaxChars"]));
        }

        [Fact]
        public void DefaultLimitIs8000()
        {
            var cfg = new Config("");
            Assert.Equal(8000, cfg.MemoryMaxChars);
        }
    }
}
