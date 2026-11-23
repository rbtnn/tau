using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // コマンドラインオプション(--config show/set/edit, --new, --open,
    // --list, --test)と、対話コマンドの省略形。
    [Collection("env")]
    public class OptionTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public OptionTests()
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
            return Json.Parse(File.ReadAllText(runner.ConfigPath, Encoding.UTF8))
                as Dictionary<string, object>;
        }

        // ---------------- --config show ----------------

        [Fact]
        public void ConfigShowListsSettingsAndPath()
        {
            runner.WriteConfig(llm.Url, model: "shown-model");
            string output = runner.Run("", new[] { "--config", "show" });

            Assert.Contains("Config file:", output);
            Assert.Contains("tau.json", output);
            Assert.Contains("model", output);
            Assert.Contains("shown-model", output);
        }

        [Fact]
        public void ConfigShowDoesNotStartChat()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--config", "show" });
            // 対話ループに入っていないこと
            Assert.DoesNotContain("/help for commands", output);
        }

        // ---------------- --config set ----------------

        [Fact]
        public void ConfigSetChangesValue()
        {
            runner.WriteConfig(llm.Url, model: "old");
            runner.Run("", new[] { "--config", "set", "model", "gpt-4o" });

            Assert.Equal("gpt-4o", Json.AsString(ReadConfig()["model"]));
        }

        [Fact]
        public void ConfigSetBoolValue()
        {
            runner.WriteConfig(llm.Url, allowTools: false);
            runner.Run("", new[] { "--config", "set", "allowTools", "true" });
            Assert.Equal("true", Json.AsString(ReadConfig()["allowTools"]));
        }

        [Fact]
        public void ConfigSetIntValue()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("", new[] { "--config", "set", "contextWindow", "128000" });
            Assert.Equal(128000, Json.AsInt(ReadConfig()["contextWindow"]));
        }

        [Fact]
        public void ConfigSetRejectsUnknownKey()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--config", "set", "noSuchKey", "x" });
            Assert.Contains("Unknown setting", output);
        }

        [Fact]
        public void ConfigSetRejectsInvalidValue()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--config", "set", "inputEncoding", "bogus" });
            Assert.Contains("Invalid value", output);
        }

        [Fact]
        public void ConfigSetWithoutValueShowsUsage()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--config", "set", "model" });
            Assert.Contains("Usage:", output);
        }

        // ---------------- 後方互換: --config <path> ----------------

        [Fact]
        public void ConfigStillAcceptsAPath()
        {
            // 従来の --config <path> も動くこと
            runner.WriteConfig(llm.Url);
            string output = runner.Run("テスト\n/exit\n",
                new[] { "--config", runner.ConfigPath });
            Assert.Contains("tau.json", output);
        }

        // ---------------- --new ----------------

        [Fact]
        public void NewFlagStartsAFreshSession()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("最初の会話\n/exit\n");
            int before = runner.SessionFiles().Length;

            string output = runner.Run("/exit\n", new[] { "--new" });
            Assert.Contains("Session:", output);
            Assert.Equal(before + 1, runner.SessionFiles().Length);
        }

        [Fact]
        public void NewFlagDoesNotShowPreviousHistory()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("前の会話\n/exit\n");

            string output = runner.Run("/exit\n", new[] { "--new" });
            Assert.DoesNotContain("前の会話", output);
        }

        // ---------------- --open ----------------

        [Fact]
        public void OpenFlagContinuesExistingSession()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("開くべき会話\n/exit\n");
            string id = Json.AsString(runner.LastSession()["id"]);

            string output = runner.Run("/exit\n", new[] { "--open", id });
            Assert.Contains("開くべき会話", output);
        }

        [Fact]
        public void OpenFlagDoesNotCallLlm()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("会話\n/exit\n");
            string id = Json.AsString(runner.LastSession()["id"]);

            llm.ClearRequests();
            runner.Run("", new[] { "--open", id });
            Assert.Empty(llm.Requests);
        }

        [Fact]
        public void OpenFlagAcceptsNumber()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("会話\n/exit\n");

            string listing = runner.Run("/list\n/exit\n");
            // 表示から ID を取り出す
            string firstId = "";
            foreach (string line in listing.Split('\n'))
            {
                string t = line.Trim().TrimStart('*').Trim();
                int dot = t.IndexOf('.');
                if (dot > 0 && dot < 4)
                {
                    string rest = t.Substring(dot + 1).Trim();
                    int sp = rest.IndexOf(' ');
                    firstId = sp > 0 ? rest.Substring(0, sp) : rest;
                    break;
                }
            }
            Assert.False(string.IsNullOrWhiteSpace(firstId));

            string output = runner.Run("", new[] { "--open", "1" });
            Assert.Contains(firstId, output);
        }

        // ---------------- --list ----------------

        [Fact]
        public void ListFlagShowsSessionsAndExits()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("一覧に出る会話\n/exit\n");

            string output = runner.Run("", new[] { "--list" });
            Assert.Contains("一覧に出る会話", output);
            Assert.DoesNotContain("/help for commands", output);
        }

        // ---------------- --test ----------------

        [Fact]
        public void TestOfflineRunsEncodingAndTlsChecks()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--test=offline" }, timeoutSec: 120);

            Assert.Contains("self test", output);
            Assert.Contains("input encoding", output);
            Assert.Contains("TLS", output);
            Assert.Contains("Result:", output);
            Assert.DoesNotContain("FAILED", output);
        }

        [Fact]
        public void TestOfflineSkipsNetworkChecks()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--test=offline" }, timeoutSec: 120);
            Assert.Contains("network checks skipped", output);
        }

        [Fact]
        public void TestDoesNotIncludeMovedSections()
        {
            // Markdown・JSON・セッションなどは dotnet test に移したので
            // --test には出ない
            runner.WriteConfig(llm.Url);
            string output = runner.Run("", new[] { "--test=offline" }, timeoutSec: 120);

            Assert.DoesNotContain("Markdown renderer", output);
            Assert.DoesNotContain("session store", output);
            Assert.DoesNotContain("config file", output);
            Assert.DoesNotContain("JSON parser", output);
        }

        // ---------------- コマンドの省略形 ----------------

        [Theory]
        [InlineData("/n")]
        [InlineData("/ne")]
        [InlineData("/new")]
        [InlineData("/l")]
        [InlineData("/li")]
        [InlineData("/list")]
        [InlineData("/t")]
        [InlineData("/to")]
        [InlineData("/tools")]
        [InlineData("/h")]
        [InlineData("/he")]
        [InlineData("/help")]
        [InlineData("/e")]
        [InlineData("/ex")]
        [InlineData("/exit")]
        [InlineData("/q")]
        [InlineData("/qu")]
        [InlineData("/quit")]
        [InlineData("/c")]
        [InlineData("/cl")]
        [InlineData("/cle")]
        [InlineData("/clear")]
        public void AbbreviationsAreAccepted(string cmd)
        {
            runner.WriteConfig(llm.Url);
            // /open と /delete は引数が要るので、ここでは引数なしで呼ぶ
            string output = runner.Run(cmd + "\n/exit\n");
            Assert.DoesNotContain("Unknown command", output);
        }

        [Fact]
        public void RemovedChatCommandsAreRejected()
        {
            // /model /set /config は廃止し、--config に一本化した
            runner.WriteConfig(llm.Url);
            foreach (string cmd in new[] { "/model", "/m", "/set", "/s", "/config", "/co" })
            {
                string output = runner.Run(cmd + "\n/exit\n");
                Assert.Contains("Unknown command: " + cmd, output);
            }
        }

        [Fact]
        public void UnknownCommandIsRejected()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("/zzz\n/exit\n");
            Assert.Contains("Unknown command: /zzz", output);
        }

        [Fact]
        public void CResolvesToClear()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("消す会話\n/exit\n");

            string output = runner.Run("/c\ny\n/exit\n");
            Assert.Contains("Cleared the session history", output);
        }

        // ---------------- ヘルプの表記 ----------------

        [Fact]
        public void HelpUsesVimStyleAbbreviations()
        {
            string output = runner.Run("", new[] { "--help" });

            Assert.Contains("/n[ew]", output);
            Assert.Contains("/l[ist]", output);
            Assert.Contains("/o[pen]", output);
            Assert.Contains("/d[elete]", output);
            Assert.Contains("/c[lear]", output);
            Assert.Contains("/t[ools]", output);
            Assert.Contains("/e[xit]", output);
            Assert.Contains("/q[uit]", output);
        }

        [Fact]
        public void HelpDocumentsConfigSubcommands()
        {
            string output = runner.Run("", new[] { "--help" });
            Assert.Contains("--config show", output);
            Assert.Contains("--config set", output);
            Assert.Contains("--config edit", output);
        }
    }
}
