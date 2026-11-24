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
            var tool = Assert.Single(messages.OfType<Dictionary<string, object>>(),
                m => Json.AsString(m["role"]) == "tool");
            Assert.Equal("original-call-id", tool["tool_call_id"]);
            Assert.Equal(name, tool["name"]);
            var result = Assert.IsType<Dictionary<string, object>>(Json.Parse(Json.AsString(tool["content"])));
            Assert.False(result.ContainsKey("error"), Json.ToString(result));
            Assert.Equal("true", Json.AsString(result["ok"]));

            var assistant = Assert.Single(messages.OfType<Dictionary<string, object>>(),
                m => m.ContainsKey("tool_calls"));
            var call = Assert.IsType<Dictionary<string, object>>(
                Assert.Single(Assert.IsType<List<object>>(assistant["tool_calls"])));
            Assert.Equal("original-call-id", call["id"]);
            Assert.Equal(name, Assert.IsType<Dictionary<string, object>>(call["function"])["name"]);
        }

        // ---------------- 文書の読み取り ----------------

        [Fact]
        public void ReadDocumentToolSendsTextToTheLlm()
        {
            // 日本語と半角英数字が混在する Excel を作り、
            // エージェントが read_document で読んだ内容を LLM に返すことを確認する。
            string path = Path.Combine(runner.WorkDir, "mixed.xlsx");
            using (FileStream fs = new(path, FileMode.Create))
            using (System.IO.Compression.ZipArchive zip =
                new(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                AddEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + "<sheets><sheet name=\"2026年度 売上\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                AddEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                AddEntry(zip, "xl/sharedStrings.xml",
                    "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<si><t>商品コード</t></si><si><t>商品名</t></si>"
                    + "<si><t>A-1024</t></si><si><t>りんご L サイズ</t></si></sst>");
                AddEntry(zip, "xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<sheetData>"
                    + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
                    + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\" t=\"s\"><v>3</v></c></row>"
                    + "</sheetData></worksheet>");
            }

            llm.Reply = req => MockLlm.ToolResultCount(req) == 0
                ? new[] { MockLlm.ToolCall("c1", "read_document",
                    Json.ToString(new Dictionary<string, object> { ["path"] = path })) }
                : new[] { new Dictionary<string, object> { ["content"] = "読みました。" } };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("この表を読んで\n/exit\n");

            Assert.Contains("読みました。", output);
            // 実行時と同じ表示になること
            Assert.Contains("▶ Tool: read_document", output);

            Dictionary<string, object> last = llm.Requests[llm.Requests.Count - 1];
            var messages = last["messages"] as List<object>;
            string toolContent = null;
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "tool")
                    toolContent = Json.AsString(m["content"]);
            }
            Assert.NotNull(toolContent);

            Dictionary<string, object> d = Json.Parse(toolContent) as Dictionary<string, object>;
            Assert.Equal("true", Json.AsString(d["ok"]));
            Assert.Equal("excel", Json.AsString(d["type"]));
            string text = Json.AsString(d["content"]);
            Assert.Contains("## 2026年度 売上", text);
            Assert.Contains("A-1024\tりんご L サイズ", text);
        }

        [Fact]
        public void AgentReadsALargeDocumentInChunks()
        {
            // 大きな Excel を作る。1 回の read_document では収まらない。
            string path = Path.Combine(runner.WorkDir, "big.xlsx");
            using (FileStream fs = new(path, FileMode.Create))
            using (System.IO.Compression.ZipArchive zip =
                new(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                AddEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + "<sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                AddEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

                string cell = new string('あ', 1000);
                StringBuilder rows = new();
                for (int i = 1; i <= 40; i++)
                    rows.Append("<row r=\"" + i + "\"><c r=\"A" + i
                        + "\" t=\"inlineStr\"><is><t>" + cell + "</t></is></c></row>");

                AddEntry(zip, "xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<sheetData>" + rows + "</sheetData></worksheet>");
            }

            // 1 回目は先頭から、2 回目以降は nextOffset を渡して続きを読む。
            List<int> offsets = new();
            llm.Reply = req =>
            {
                int n = MockLlm.ToolResultCount(req);
                if (n == 0)
                    return new[] { MockLlm.ToolCall("c0", "read_document",
                        Json.ToString(new Dictionary<string, object> { ["path"] = path })) };

                // 直前のツール結果から nextOffset を取り出す
                Dictionary<string, object> last = null;
                foreach (object o in (List<object>)req["messages"])
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (Json.AsString(m["role"]) == "tool")
                        last = Json.Parse(Json.AsString(m["content"])) as Dictionary<string, object>;
                }

                bool more = last != null && Json.AsString(last["truncated"]) == "true";
                if (more && n <= 4)
                {
                    int next = Json.AsInt(last["nextOffset"]);
                    offsets.Add(next);
                    return new[] { MockLlm.ToolCall("c" + n, "read_document",
                        Json.ToString(new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["offset"] = next,
                        })) };
                }
                return new[] { new Dictionary<string, object> { ["content"] = "全部読みました。" } };
            };
            runner.WriteConfig(llm.Url, allowTools: true);

            string output = runner.Run("この表を全部読んで\n/exit\n");

            Assert.Contains("全部読みました。", output);
            Assert.True(offsets.Count >= 1, "the agent should ask for the next chunk");
            // 続きの読み取りも実行時と同じ表示になる
            Assert.Contains("▶ Tool: read_document", output);
            Assert.Contains("@" + offsets[0], output);
        }

        static void AddEntry(System.IO.Compression.ZipArchive zip, string name, string content)
        {
            System.IO.Compression.ZipArchiveEntry e = zip.CreateEntry(name);
            using Stream s = e.Open();
            byte[] b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
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
