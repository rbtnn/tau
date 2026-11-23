using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 対話中のコマンド(/tools /new /list /open /delete /clear など)。
    [Collection("env")]
    public class CommandIntegrationTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public CommandIntegrationTests()
        {
            runner = new TauRunner();
            llm = new MockLlm();
            llm.Reply = MockLlm.Text("ok");
        }

        public void Dispose()
        {
            llm.Dispose();
            runner.Dispose();
        }

        Dictionary<string, object> ReadConfig()
        {
            string raw = File.ReadAllText(runner.ConfigPath, Encoding.UTF8);
            return Json.Parse(raw) as Dictionary<string, object>;
        }

        // ---------------- /tools ----------------

        [Fact]
        public void ToolsCommandTogglesAndSaves()
        {
            runner.WriteConfig(llm.Url, allowTools: false);
            runner.Run("/tools\n/exit\n");

            Dictionary<string, object> cfg = ReadConfig();
            Assert.True(Json.AsString(cfg["allowTools"]) == "true",
                "allowTools should be turned on");
        }

        [Fact]
        public void ToolsCommandTogglesBack()
        {
            runner.WriteConfig(llm.Url, allowTools: true);
            runner.Run("/tools\n/exit\n");
            Assert.Equal("false", Json.AsString(ReadConfig()["allowTools"]));
        }

        // ---------------- 削除したコマンド ----------------
        //
        // /model /set /config は廃止し、--config に一本化した。

        [Theory]
        [InlineData("/model")]
        [InlineData("/model gpt-4o")]
        [InlineData("/set model x")]
        [InlineData("/config")]
        [InlineData("/config edit")]
        public void RemovedCommandsAreRejected(string cmd)
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run(cmd + "\n/exit\n");
            Assert.Contains("Unknown command", output);
        }

        // ---------------- /new /list /open /delete /clear ----------------

        [Fact]
        public void NewCreatesSeparateSessions()
        {
            runner.Run("/new\n/exit\n");
            runner.Run("/new\n/exit\n");
            Assert.True(runner.SessionFiles().Length >= 2);
        }

        [Fact]
        public void SessionsShowsNumbersAndHint()
        {
            runner.Run("会話\n/exit\n");
            string output = runner.Run("/list\n/exit\n");
            Assert.Contains("1.", output);
            Assert.Contains("/delete", output);
            Assert.Contains("/open", output);
        }

        [Fact]
        public void OpenAcceptsNumber()
        {
            runner.Run("会話1\n/exit\n");
            runner.Run("/new\n会話2\n/exit\n");

            string sessions = runner.Run("/list\n/exit\n");
            // 一覧の 1 番目の ID を取り出す
            string firstId = "";
            foreach (string line in sessions.Split('\n'))
            {
                string t = line.Trim().TrimStart('*').Trim();
                int dot = t.IndexOf('.');
                if (dot > 0 && dot < 4 && t.Length > dot + 2)
                {
                    string rest = t.Substring(dot + 1).Trim();
                    int sp = rest.IndexOf(' ');
                    firstId = sp > 0 ? rest.Substring(0, sp) : rest;
                    break;
                }
            }
            Assert.False(string.IsNullOrWhiteSpace(firstId), "no session id in listing:\n" + sessions);

            string output = runner.Run("/open 1\n/exit\n");
            Assert.Contains("Opened session: " + firstId, output);
        }

        [Fact]
        public void OpenAcceptsId()
        {
            runner.Run("会話\n/exit\n");
            string id = Json.AsString(runner.LastSession()["id"]);

            string output = runner.Run("/open " + id + "\n/exit\n");
            Assert.Contains("Opened session: " + id, output);
        }

        [Fact]
        public void OpenRejectsOutOfRangeNumber()
        {
            runner.Run("会話\n/exit\n");
            string output = runner.Run("/open 99\n/exit\n");
            Assert.Contains("No session with that number", output);
        }

        [Fact]
        public void OpenWithoutArgumentShowsUsage()
        {
            runner.Run("会話\n/exit\n");
            string output = runner.Run("/open\n/exit\n");
            Assert.Contains("Specify the session to open", output);
        }

        [Fact]
        public void HelpShowsNumberOrIdForOpenAndDelete()
        {
            string output = runner.Run("/help\n/exit\n");
            Assert.Contains("/o[pen] <n|id>", output);
            Assert.Contains("/d[elete] <n|id>", output);
        }

        [Fact]
        public void DeleteCancelsOnNo()
        {
            runner.Run("会話\n/exit\n");
            int before = runner.SessionFiles().Length;

            string output = runner.Run("/delete 1\nn\n/exit\n");
            Assert.Contains("Cancelled", output);
            Assert.Equal(before, runner.SessionFiles().Length);
        }

        // /list の表示から (番号, ID) を取り出す。
        static List<(int num, string id)> ParseSessions(string output)
        {
            List<(int, string)> list = new();
            foreach (string line in output.Split('\n'))
            {
                string t = line.Trim().TrimStart('*').Trim();
                int dot = t.IndexOf('.');
                if (dot <= 0 || dot > 3) continue;
                string rest = t.Substring(dot + 1).Trim();
                int sp = rest.IndexOf(' ');
                string id = sp > 0 ? rest.Substring(0, sp) : rest;
                if (id.Length == 0) continue;
                if (int.TryParse(t.Substring(0, dot), out int n)) list.Add((n, id));
            }
            return list;
        }

        [Fact]
        public void DeleteRemovesSession()
        {
            runner.Run("会話1\n/exit\n");
            runner.Run("/new\n会話2\n/exit\n");
            int before = runner.SessionFiles().Length;
            Assert.True(before >= 2);

            // 表示中のセッションを消すと代替が作られて件数が変わらないため、
            // 表示中ではないセッションを消す。
            // ファイルの並びは ID(時刻 + GUID)依存で不安定なので、
            // /list の表示から ID を取る。
            string listing = runner.Run("/list\n/exit\n");
            List<(int num, string id)> sessions = ParseSessions(listing);
            Assert.True(sessions.Count >= 2, "need at least 2 sessions:\n" + listing);

            // 表示中(現在のセッション)を確実に避けるため、
            // 明示的に 2 番目のセッションを開いてから 1 番目を消す。
            string openId = sessions[1].id;
            runner.Run("/open " + openId + "\n/exit\n");

            string target = sessions[0].id;
            Assert.NotEqual(openId, target);

            string output = runner.Run("/delete " + target + "\ny\n/exit\n");
            Assert.Contains("Deleted", output);
            Assert.Equal(before - 1, runner.SessionFiles().Length);
        }

        [Fact]
        public void DeleteCurrentSessionSwitchesToAnother()
        {
            // 表示中のセッションを消した場合は、代替のセッションへ切り替わる。
            // そのため件数は変わらない(減って見えない)のが仕様。
            runner.Run("会話1\n/exit\n");
            int before = runner.SessionFiles().Length;

            string output = runner.Run("/delete 1\ny\n/exit\n");
            Assert.Contains("Deleted", output);
            Assert.Contains("Switched session", output);
            Assert.Equal(before, runner.SessionFiles().Length);
        }

        [Fact]
        public void DeleteWithoutArgumentShowsUsage()
        {
            runner.Run("会話\n/exit\n");
            string output = runner.Run("/delete\n/exit\n");
            Assert.Contains("Specify the session to delete", output);
        }

        [Fact]
        public void DeleteRejectsOutOfRange()
        {
            runner.Run("会話\n/exit\n");
            string output = runner.Run("/delete 99\n/exit\n");
            Assert.Contains("No session with that number", output);
        }

        [Fact]
        public void ClearRemovesHistoryButKeepsSession()
        {
            runner.Run("残すべきでない会話\n/exit\n");
            Dictionary<string, object> before = runner.LastSession();
            string id = Json.AsString(before["id"]);

            runner.Run("/clear\ny\n/exit\n");

            Dictionary<string, object> after = runner.LastSession();
            Assert.Equal(id, Json.AsString(after["id"]));
            var msgs = after["messages"] as List<object>;
            Assert.NotNull(msgs);
            Assert.Empty(msgs);
        }

        [Fact]
        public void ClearCancelsOnNo()
        {
            runner.Run("消さない会話\n/exit\n");
            int before = ((List<object>)runner.LastSession()["messages"]).Count;

            runner.Run("/clear\nn\n/exit\n");

            int after = ((List<object>)runner.LastSession()["messages"]).Count;
            Assert.Equal(before, after);
        }
    }
}
