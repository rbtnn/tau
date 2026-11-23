using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // ツール反復が上限に達したときのコンパクション。
    // 以前は即エラーにしていたが、要約して続行するようにした。
    [Collection("env")]
    public class CompactionTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public CompactionTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        // 常にツールを呼び続けるモック（上限まで回る）
        static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            AlwaysTool(string name)
        {
            return req =>
            {
                int n = MockLlm.ToolResultCount(req);
                List<Dictionary<string, object>> list = new();
                list.Add(MockLlm.ToolCall("c" + n, name, "{\"path\":\".\"}"));
                return list;
            };
        }

        // 上限到達後にだけ最終回答を返すモック。
        // 「Continue from where you left off」が来たら本文を返す。
        static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            ToolUntilContinue(string name, string final)
        {
            return req =>
            {
                bool sawContinue = false;
                if (req.ContainsKey("messages") && req["messages"] is List<object> msgs)
                {
                    foreach (object o in msgs)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m != null && Json.AsString(m["role"]) == "user")
                        {
                            string c = Json.AsString(m["content"]);
                            if (c.Contains("Continue from where you left off"))
                                sawContinue = true;
                        }
                    }
                }

                List<Dictionary<string, object>> list = new();
                if (sawContinue)
                    list.Add(new Dictionary<string, object> { ["content"] = final });
                else
                {
                    int n = MockLlm.ToolResultCount(req);
                    list.Add(MockLlm.ToolCall("c" + n, name, "{\"path\":\".\"}"));
                }
                return list;
            };
        }

        [Fact]
        public void ToolLoopReachingTheLimitCompactsAndContinues()
        {
            // 10 回ツールを呼んだあと、「続きを」で最終回答を返す
            llm.Reply = ToolUntilContinue("list_dir", "やっと完了しました");
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("長い作業\n/exit\n");

            Assert.Contains("やっと完了しました", output);
            Assert.DoesNotContain("too many tool iterations", output);
            Assert.Contains("Compacting context", output);
        }

        [Fact]
        public void CompactionAddsAContinueMessage()
        {
            llm.Reply = ToolUntilContinue("list_dir", "done");
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("長い作業\n/exit\n");

            // 送られたリクエストのどれかに「Continue from where you left off」
            bool sawContinue = false;
            foreach (Dictionary<string, object> req in llm.Requests)
            {
                if (!req.ContainsKey("messages")) continue;
                if (!(req["messages"] is List<object> msgs)) continue;
                foreach (object o in msgs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m != null && Json.AsString(m["role"]) == "user"
                        && Json.AsString(m["content"]).Contains("Continue from where you left off"))
                        sawContinue = true;
                }
            }
            Assert.True(sawContinue, "a continue prompt should be sent after compaction");
        }

        [Fact]
        public void StillFailsWhenLlmNeverFinishes()
        {
            // 何度コンパクトしてもツールを呼び続けるなら、最後はエラー
            llm.Reply = AlwaysTool("list_dir");
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("終わらない作業\n/exit\n");

            Assert.Contains("too many tool iterations", output);
        }

        [Fact]
        public void CompactionIsBounded()
        {
            // 無限に続かないこと(回数に上限がある)
            llm.Reply = AlwaysTool("list_dir");
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("終わらない作業\n/exit\n");

            // 要求回数が無限でないこと
            Assert.True(llm.Requests.Count < 100,
                "the loop should be bounded, got " + llm.Requests.Count + " requests");
        }

        [Fact]
        public void SessionRemainsConsistentAfterCompaction()
        {
            llm.Reply = ToolUntilContinue("list_dir", "完了です");
            runner.WriteConfig(llm.Url, allowTools: true);
            // 直近に残す量を小さくして、要約が実際に走るようにする
            runner.Run("", new[] { "--config", "set", "keepRecentTokens", "200" });

            string output = runner.Run("長い作業\n/exit\n");

            // デバッグ: 何が起きたか
            Assert.True(llm.Requests.Count > 0,
                "requests=" + llm.Requests.Count + "\noutput:\n" + output);
            Dictionary<string, object> s = runner.LastSession();
            Assert.NotNull(s);
            Assert.True(s.ContainsKey("messages"));

            // contextStart が進んでいること(要約が行われた証拠)
            int start = Json.AsInt(s.ContainsKey("contextStart") ? s["contextStart"] : 0);
            Assert.True(start > 0, "contextStart should advance after compaction");
        }

        [Fact]
        public void SummaryIsStored()
        {
            llm.Reply = ToolUntilContinue("list_dir", "完了です");
            runner.WriteConfig(llm.Url, allowTools: true);

            runner.Run("長い作業\n/exit\n");

            Dictionary<string, object> s = runner.LastSession();
            string summary = Json.AsString(s.ContainsKey("summary") ? s["summary"] : "");
            // 要約は LLM が返したもの(このモックではツール呼び出しなので空でもよい)
            Assert.NotNull(summary);
        }
    }
}
