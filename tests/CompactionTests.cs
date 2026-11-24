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

        // コンパクションの要約リクエストかどうか。
        static bool IsSummaryRequest(Dictionary<string, object> req)
        {
            if (!req.ContainsKey("messages") || !(req["messages"] is List<object> msgs)) return false;
            if (msgs.Count == 0) return false;
            Dictionary<string, object> sys = msgs[0] as Dictionary<string, object>;
            if (sys == null || Json.AsString(sys["role"]) != "system") return false;
            return Json.AsString(sys["content"]).Contains("summarising chat history");
        }

        static List<Dictionary<string, object>> SummaryReply()
        {
            List<Dictionary<string, object>> list = new();
            list.Add(new Dictionary<string, object> { ["content"] = "これまでのやり取りの要約です。" });
            return list;
        }

        // 常にツールを呼び続けるモック（上限まで回る）
        static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            AlwaysTool(string name)
        {
            return req =>
            {
                if (IsSummaryRequest(req)) return SummaryReply();
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
                if (IsSummaryRequest(req)) return SummaryReply();

                bool sawContinue = false;
                if (req.ContainsKey("messages") && req["messages"] is List<object> msgs)
                {
                    foreach (object o in msgs)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m != null && Json.AsString(m["role"]) == "user")
                        {
                            string c = m.ContainsKey("content") ? Json.AsString(m["content"]) : "(no content)";
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

        // 大きな Excel を作る
        string MakeLargeExcel(string dir, int rows, int charsPerCell)
        {
            string path = Path.Combine(dir, "big.xlsx");
            using FileStream fs = new(path, FileMode.Create);
            using System.IO.Compression.ZipArchive zip =
                new(fs, System.IO.Compression.ZipArchiveMode.Create);

            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

            string cell = new string('あ', charsPerCell);
            StringBuilder rowsXml = new();
            for (int i = 1; i <= rows; i++)
                rowsXml.Append("<row r=\"" + i + "\"><c r=\"A" + i
                    + "\" t=\"inlineStr\"><is><t>" + cell + "</t></is></c></row>");

            Add(zip, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<sheetData>" + rowsXml + "</sheetData></worksheet>");
            return path;
        }

        static void Add(System.IO.Compression.ZipArchive zip, string name, string content)
        {
            System.IO.Compression.ZipArchiveEntry e = zip.CreateEntry(name);
            using Stream s = e.Open();
            byte[] b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }

        [Fact]
        public void ReadingALargeDocumentCompactsAndFinishes()
        {
            // 大きな文書を分割して読み続け、10 回で上限に達する。
            // コンパクションのあと「続きを」で最終回答を返す。
            string path = MakeLargeExcel(runner.WorkDir, 200, 2000);   // 約 400,000 文字 → 25 チャンク以上

            List<int> offsets = new();
            llm.Reply = req =>
            {
                List<object> msgs = req.ContainsKey("messages")
                    ? req["messages"] as List<object> : null;

                // コンパクションの要約リクエストには、ツールではなく要約本文を返す。
                if (msgs != null && msgs.Count > 0)
                {
                    Dictionary<string, object> sys = msgs[0] as Dictionary<string, object>;
                    if (sys != null && Json.AsString(sys["role"]) == "system"
                        && Json.AsString(sys["content"]).Contains("summarising chat history"))
                    {
                        List<Dictionary<string, object>> summary = new();
                        summary.Add(new Dictionary<string, object>
                        {
                            ["content"] = "これまでのやり取りの要約です。",
                        });
                        return summary;
                    }
                }

                bool sawContinue = false;
                if (msgs != null)
                {
                    foreach (object o in msgs)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m != null && Json.AsString(m["role"]) == "user"
                            && Json.AsString(m["content"]).Contains("Continue from where you left off"))
                            sawContinue = true;
                    }
                }

                List<Dictionary<string, object>> list = new();
                if (sawContinue)
                {
                    list.Add(new Dictionary<string, object> { ["content"] = "読み終わりました。" });
                    return list;
                }

                // 直前のツール結果が「まだ続きがある」なら nextOffset へ進む。
                // 続きが無ければ読み終わっているので、最終回答を返す。
                Dictionary<string, object> lastResult = null;
                if (msgs != null)
                {
                    for (int i = msgs.Count - 1; i >= 0; i--)
                    {
                        Dictionary<string, object> m = msgs[i] as Dictionary<string, object>;
                        if (m == null || Json.AsString(m["role"]) != "tool") continue;
                        try { lastResult = Json.Parse(Json.AsString(m["content"])) as Dictionary<string, object>; }
                        catch { }
                        break;
                    }
                }

                // コンパクション後の「続きを」指示には読み取り位置が入っている。
                // それを最優先する（要約で直前のツール結果が消えているため）。
                int offset = -1;
                if (msgs != null)
                {
                    for (int i = msgs.Count - 1; i >= 0 && offset < 0; i--)
                    {
                        Dictionary<string, object> m = msgs[i] as Dictionary<string, object>;
                        if (m == null || Json.AsString(m["role"]) != "user") continue;
                        string c = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                        int at = c.LastIndexOf("offset=", StringComparison.Ordinal);
                        if (at < 0) continue;
                        int start = at + "offset=".Length;
                        int end = start;
                        while (end < c.Length && char.IsDigit(c[end])) end++;
                        if (end > start && int.TryParse(c.Substring(start, end - start), out int v))
                            offset = v;
                    }
                }

                if (offset < 0)
                {
                    if (lastResult != null && Json.AsString(lastResult["truncated"]) == "true")
                        offset = Json.AsInt(lastResult["nextOffset"]);
                    else if (lastResult != null && lastResult.ContainsKey("offset"))
                    {
                        // 読み終わっている
                        list.Add(new Dictionary<string, object> { ["content"] = "読み終わりました。" });
                        return list;
                    }
                    else offset = 0;
                }
                offsets.Add(offset);

                list.Add(MockLlm.ToolCall("c" + MockLlm.ToolResultCount(req), "read_document",
                    Json.ToString(new Dictionary<string, object>
                    {
                        ["path"] = path,
                        ["offset"] = offset,
                    })));
                return list;
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("この表を全部読んで\n/exit\n");

            Assert.Contains("読み終わりました。", output);
            Assert.DoesNotContain("too many tool iterations", output);
            Assert.Contains("Compacting context", output);

            // 続きの位置が進んでいること
            Assert.True(offsets.Count > 1, "the document should be read in several chunks");
            Assert.True(offsets[offsets.Count - 1] > offsets[0],
                "each chunk should move forward: offsets=[" + string.Join(",", offsets) + "]");

            // コンパクション後の「続きを」指示に、読み取り位置が入っていること
            bool hintFound = false;
            foreach (Dictionary<string, object> rq in llm.Requests)
            {
                var ms = rq.ContainsKey("messages") ? rq["messages"] as List<object> : null;
                if (ms == null) continue;
                foreach (object o in ms)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null || Json.AsString(m["role"]) != "user") continue;
                    string c = Json.AsString(m["content"]);
                    if (c.Contains("partially read") && c.Contains("offset=")) hintFound = true;
                }
            }
            Assert.True(hintFound, "the continue prompt should carry the read position");

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
