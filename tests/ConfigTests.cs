using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 設定ファイルの読み書きと、未知キーの保持・移行を検証する。
    // 環境変数やデータディレクトリを汚さないよう、各テストで専用の場所を使う。
    [Collection("env")]
    public class ConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly Dictionary<string, string> _savedEnv = new();
        private static readonly string[] EnvKeys =
        {
            "LLM_API_URL", "LLM_API_KEY", "LLM_MODEL", "ALLOW_TOOLS",
            "LLM_EMIT_REASONING", "MAX_TOOLS_PER_TURN", "TOOL_TIMEOUT_SEC",
            "LLM_CONTEXT_WINDOW", "LLM_RESERVE_TOKENS", "LLM_KEEP_RECENT_TOKENS",
            "LLM_PROXY_URL", "LLM_PROXY_USER", "LLM_PROXY_PASS",
            "LLM_USE_SYSTEM_PROXY", "AI_SYSTEM_PROMPT", "TAU_CONFIG",
            "TAU_INPUT_ENCODING", "XDG_CONFIG_HOME", "HOME", "APPDATA",
        };

        public ConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "tau-cfg-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);

            foreach (string k in EnvKeys)
            {
                _savedEnv[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, null);
            }
            Environment.SetEnvironmentVariable("HOME", _dir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME",
                Path.Combine(_dir, ".config"));
        }

        public void Dispose()
        {
            foreach (var kv in _savedEnv)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string CfgPath(string name = "tau.json")
        {
            return Path.Combine(_dir, name);
        }

        // ---------------- 既定値 ----------------

        [Fact]
        public void HasSensibleDefaults()
        {
            var c = new Config(CfgPath());
            Assert.False(string.IsNullOrEmpty(c.ApiBaseUrl));
            Assert.False(string.IsNullOrEmpty(c.Model));
            Assert.True(c.ContextWindow > 0);
            Assert.True(c.ReserveTokens > 0);
            Assert.True(c.MaxToolsPerTurn > 0);
            Assert.True(c.ToolTimeoutSec > 0);
        }

        // ---------------- 保存と読み込み ----------------

        [Fact]
        public void SavesAndLoadsModel()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.Model = "test-model";
            c.Save();

            Assert.True(File.Exists(path));

            var c2 = new Config(path);
            Assert.Equal("test-model", c2.Model);
        }

        [Fact]
        public void SavesAndLoadsInputEncoding()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.InputEncoding = "utf-8";
            c.Save();

            var c2 = new Config(path);
            Assert.Equal("utf-8", c2.InputEncoding);
        }

        [Fact]
        public void SavesAndLoadsJapaneseSystemPrompt()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.SystemPrompt = "常に日本語で応答してください。";
            c.Save();

            var c2 = new Config(path);
            Assert.Equal("常に日本語で応答してください。", c2.SystemPrompt);
        }

        [Fact]
        public void SavesAndLoadsAllScalarFields()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.ApiBaseUrl = "https://example.com/v1/chat/completions";
            c.SecretKey = "secret-key";
            c.Model = "m";
            c.AllowTools = true;
            c.EmitReasoning = false;
            c.ContextWindow = 12345;
            c.ReserveTokens = 678;
            c.KeepRecentTokens = 9012;
            c.MaxToolsPerTurn = 3;
            c.ToolTimeoutSec = 7;
            c.ProxyUrl = "http://proxy:8080";
            c.ProxyUser = "u";
            c.ProxyPass = "p";
            c.UseSystemProxy = true;
            c.Save();

            var c2 = new Config(path);
            Assert.Equal("https://example.com/v1/chat/completions", c2.ApiBaseUrl);
            Assert.Equal("secret-key", c2.SecretKey);
            Assert.Equal("m", c2.Model);
            Assert.True(c2.AllowTools);
            Assert.False(c2.EmitReasoning);
            Assert.Equal(12345, c2.ContextWindow);
            Assert.Equal(678, c2.ReserveTokens);
            Assert.Equal(9012, c2.KeepRecentTokens);
            Assert.Equal(3, c2.MaxToolsPerTurn);
            Assert.Equal(7, c2.ToolTimeoutSec);
            Assert.Equal("http://proxy:8080", c2.ProxyUrl);
            Assert.Equal("u", c2.ProxyUser);
            Assert.Equal("p", c2.ProxyPass);
            Assert.True(c2.UseSystemProxy);
        }

        [Fact]
        public void MissingFileFallsBackToDefaults()
        {
            var c = new Config(CfgPath("does-not-exist.json"));
            Assert.False(string.IsNullOrEmpty(c.ApiBaseUrl));
        }

        [Fact]
        public void MalformedFileDoesNotCrash()
        {
            string path = CfgPath("broken.json");
            File.WriteAllText(path, "{ this is not json ", Encoding.UTF8);

            var c = new Config(path);
            // 既定値のまま動くこと
            Assert.False(string.IsNullOrEmpty(c.ApiBaseUrl));
        }

        // ---------------- 未知キーの保持 ----------------

        [Fact]
        public void PreservesUnknownKeysOnSave()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.Model = "first";
            c.Save();

            // 手で未知キーを追記する
            string raw = File.ReadAllText(path, Encoding.UTF8);
            int close = raw.LastIndexOf('}');
            raw = raw.Substring(0, close) + ",\"someCustomKey\":\"keep-me\"}";
            File.WriteAllText(path, raw, Encoding.UTF8);

            var c2 = new Config(path);
            Assert.Equal("first", c2.Model);
            c2.Model = "second";
            c2.Save();

            string after = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("keep-me", after);
        }

        [Fact]
        public void DoesNotWriteObsoleteModelsKey()
        {
            string path = CfgPath();
            var c = new Config(path);
            c.Model = "x";
            c.Save();

            string raw = File.ReadAllText(path, Encoding.UTF8);
            Assert.DoesNotContain("\"models\"", raw);
        }

        [Fact]
        public void RemovesModelsKeyFromAnExistingFile()
        {
            string path = CfgPath();
            File.WriteAllText(path,
                "{\"llmApiUrl\":\"http://x/v1\",\"model\":\"m\"," +
                "\"models\":[\"a\",\"b\"]}", Encoding.UTF8);

            var c = new Config(path);
            Assert.Equal("m", c.Model);
            c.Save();

            string raw = File.ReadAllText(path, Encoding.UTF8);
            Assert.DoesNotContain("\"models\"", raw);
        }

        // ---------------- 環境変数 ----------------

        [Fact]
        public void EnvironmentVariableOverridesDefaults()
        {
            Environment.SetEnvironmentVariable("LLM_API_URL", "http://env/v1/chat/completions");
            Environment.SetEnvironmentVariable("LLM_MODEL", "env-model");

            var c = new Config(CfgPath());
            Assert.Equal("http://env/v1/chat/completions", c.ApiBaseUrl);
            Assert.Equal("env-model", c.Model);
        }

        [Fact]
        public void ConfigFileOverridesEnvironment()
        {
            Environment.SetEnvironmentVariable("LLM_MODEL", "env-model");

            string path = CfgPath();
            var c = new Config(path);
            c.Model = "file-model";
            c.Save();

            var c2 = new Config(path);
            Assert.Equal("file-model", c2.Model);
        }

        // ---------------- データディレクトリ ----------------

        [Fact]
        public void DataDirIsTauCli()
        {
            string d = Config.TauDataDir();
            Assert.EndsWith("tau-cli", d);
        }

        [Fact]
        public void LegacyDataDirIsTau()
        {
            string d = Config.LegacyDataDir();
            Assert.EndsWith("tau", d);
            Assert.NotEqual(Config.TauDataDir(), d);
        }

        [Fact]
        public void DefaultConfigPathIsUnderDataDir()
        {
            string p = Config.DefaultConfigPath();
            Assert.StartsWith(Config.TauDataDir(), p);
            Assert.EndsWith("tau.json", p);
        }

        // ---------------- 移行 ----------------

        [Fact]
        public void MigratesLegacyDataIntoNewDirectory()
        {
            string oldDir = Config.LegacyDataDir();
            string newDir = Config.TauDataDir();

            // 旧い場所にデータを置く
            Directory.CreateDirectory(Path.Combine(oldDir, "sessions"));
            File.WriteAllText(Path.Combine(oldDir, "tau.json"),
                "{\"model\":\"legacy-model\"}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(oldDir, "sessions", "s_old.json"),
                "{\"id\":\"s_old\"}", Encoding.UTF8);

            // 新しい場所は空にしておく
            try { Directory.Delete(newDir, true); } catch { }

            Assert.True(Config.MigrateLegacyData());

            Assert.True(File.Exists(Path.Combine(newDir, "tau.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "sessions", "s_old.json")));

            // 旧い場所は残す
            Assert.True(File.Exists(Path.Combine(oldDir, "tau.json")));

            // 移行した設定が読み込める
            var c = new Config(Path.Combine(newDir, "tau.json"));
            Assert.Equal("legacy-model", c.Model);
        }

        [Fact]
        public void DoesNotMigrateWhenNewDirectoryHasData()
        {
            string oldDir = Config.LegacyDataDir();
            string newDir = Config.TauDataDir();

            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "tau.json"),
                "{\"model\":\"legacy-model\"}", Encoding.UTF8);

            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(newDir, "tau.json"),
                "{\"model\":\"keep-me\"}", Encoding.UTF8);

            Assert.False(Config.MigrateLegacyData());

            string after = File.ReadAllText(Path.Combine(newDir, "tau.json"), Encoding.UTF8);
            Assert.Contains("keep-me", after);
        }

        [Fact]
        public void DoesNotMigrateWhenLegacyDirectoryIsMissing()
        {
            try { Directory.Delete(Config.LegacyDataDir(), true); } catch { }
            Assert.False(Config.MigrateLegacyData());
        }
    }
}
