using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // セッションの保存・読み込み・削除・一覧を検証する。
    // 実際の %APPDATA% を汚さないよう、環境変数で専用の場所に向ける。
    [Collection("env")]
    public class SessionStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly Dictionary<string, string> _savedEnv = new();
        private static readonly string[] EnvKeys =
            { "HOME", "XDG_CONFIG_HOME", "APPDATA", "TAU_DATA_DIR" };

        public SessionStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "tau-sess-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);

            foreach (string k in EnvKeys)
            {
                _savedEnv[k] = Environment.GetEnvironmentVariable(k);
                Environment.SetEnvironmentVariable(k, null);
            }
            Environment.SetEnvironmentVariable("HOME", _dir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME",
                Path.Combine(_dir, ".config"));
            // Windows では %APPDATA% が差し替わらないので、保存先を明示する
            Environment.SetEnvironmentVariable("TAU_DATA_DIR",
                Path.Combine(_dir, ".config", "tau-cli"));
        }

        public void Dispose()
        {
            foreach (var kv in _savedEnv)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static Dictionary<string, object> Msg(string role, string content)
        {
            return new Dictionary<string, object>
            {
                ["role"] = role,
                ["content"] = content,
            };
        }

        // ---------------- ID ----------------

        [Fact]
        public void NewIdIsNonEmptyAndUnique()
        {
            var store = new SessionStore();
            string a = store.NewId();
            string b = store.NewId();

            Assert.False(string.IsNullOrWhiteSpace(a));
            Assert.NotEqual(a, b);
            Assert.StartsWith("s", a);
        }

        // ---------------- 保存と読み込み ----------------

        [Fact]
        public void SaveAndLoadMessages()
        {
            var store = new SessionStore();
            string id = store.NewId();

            var msgs = new List<object>
            {
                Msg("user", "こんにちは"),
                Msg("assistant", "やあ"),
            };
            store.Save(id, msgs, "テスト会話");

            var loaded = store.Load(id);
            Assert.NotNull(loaded);
            Assert.True(loaded.ContainsKey("messages"));

            var back = loaded["messages"] as List<object>;
            Assert.NotNull(back);
            Assert.Equal(2, back.Count);
        }

        [Fact]
        public void JapaneseContentSurvivesRoundTrip()
        {
            var store = new SessionStore();
            string id = store.NewId();

            var msgs = new List<object> { Msg("user", "日本語のテストです。①②③") };
            store.Save(id, msgs, "日本語タイトル");

            var loaded = store.Load(id);
            var back = loaded["messages"] as List<object>;
            var first = back[0] as Dictionary<string, object>;

            Assert.Equal("日本語のテストです。①②③", Json.AsString(first["content"]));
            Assert.Equal("日本語タイトル", Json.AsString(loaded["title"]));
        }

        [Fact]
        public void MessagesOfReturnsSavedMessages()
        {
            var store = new SessionStore();
            string id = store.NewId();

            store.Save(id, new List<object> { Msg("user", "x") }, "t");

            var msgs = store.MessagesOf(id);
            Assert.Single(msgs);
        }

        [Fact]
        public void MessagesOfMissingSessionIsEmpty()
        {
            var store = new SessionStore();
            Assert.Empty(store.MessagesOf("no-such-session"));
        }

        [Fact]
        public void LoadMissingSessionIsNull()
        {
            var store = new SessionStore();
            Assert.Null(store.Load("no-such-session"));
        }

        [Fact]
        public void SaveKeepsCreatedAtAndUpdatesUpdatedAt()
        {
            var store = new SessionStore();
            string id = store.NewId();

            store.Save(id, new List<object> { Msg("user", "a") }, "t");
            var first = store.Load(id);
            string created = Json.AsString(first["createdAt"]);
            Assert.False(string.IsNullOrEmpty(created));

            store.Save(id, new List<object> { Msg("user", "b") }, "t");
            var second = store.Load(id);
            Assert.Equal(created, Json.AsString(second["createdAt"]));
        }

        [Fact]
        public void SaveStoresSummaryAndContextStart()
        {
            var store = new SessionStore();
            string id = store.NewId();

            store.Save(id, new List<object> { Msg("user", "a") }, "t", "要約です", 3);

            var loaded = store.Load(id);
            Assert.Equal("要約です", Json.AsString(loaded["summary"]));
            Assert.Equal(3, Json.AsInt(loaded["contextStart"]));
        }

        [Fact]
        public void EmptyTitleBecomesNewConversation()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object>(), "");

            var loaded = store.Load(id);
            Assert.Equal("New conversation", Json.AsString(loaded["title"]));
        }

        // ---------------- 一覧 ----------------

        [Fact]
        public void ListIncludesSavedSessions()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object> { Msg("user", "x") }, "listed");

            var list = store.List();
            Assert.NotEmpty(list);

            bool found = false;
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d != null && Json.AsString(d["id"]) == id) found = true;
            }
            Assert.True(found);
        }

        [Fact]
        public void ListExcludesUnderscoreStateFile()
        {
            var store = new SessionStore();

            // _state.json は一覧に出ない
            string statePath = Path.Combine(store.DataDir, "_state.json");
            File.WriteAllText(statePath, "{\"lastSession\":\"x\"}", Encoding.UTF8);

            var list = store.List();
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                Assert.NotEqual("_state", Json.AsString(d["id"]));
            }
        }

        [Fact]
        public void ListCountsMessages()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object> { Msg("user", "a"), Msg("assistant", "b") }, "t");

            var list = store.List();
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (Json.AsString(d["id"]) == id)
                {
                    Assert.Equal(2, Json.AsInt(d["count"]));
                    return;
                }
            }
            Assert.Fail("session not found in list");
        }

        // ---------------- 削除 ----------------

        [Fact]
        public void DeleteRemovesSession()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object> { Msg("user", "x") }, "t");

            Assert.True(store.Delete(id));
            Assert.Null(store.Load(id));
        }

        [Fact]
        public void DeleteMissingSessionIsFalse()
        {
            var store = new SessionStore();
            Assert.False(store.Delete("no-such-session"));
        }

        [Fact]
        public void DeleteEmptyIdIsFalse()
        {
            var store = new SessionStore();
            Assert.False(store.Delete(""));
            Assert.False(store.Delete(null));
        }

        // ---------------- lastSession ----------------

        [Fact]
        public void LastSessionIdRoundTrips()
        {
            var store = new SessionStore();
            string id = store.NewId();

            // LastSessionId は保存済みファイルがある場合のみ返す仕様
            store.Save(id, new List<object> { Msg("user", "x") }, "t");
            store.SetLastSessionId(id);
            Assert.Equal(id, store.LastSessionId());
        }

        [Fact]
        public void LastSessionIdIgnoresDeletedSession()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object> { Msg("user", "x") }, "t");
            store.SetLastSessionId(id);

            // ファイルだけ消す(状態は残る)
            store.Delete(id);

            Assert.Equal("", store.LastSessionId());
        }

        [Fact]
        public void DeletingLastSessionClearsIt()
        {
            var store = new SessionStore();
            string id = store.NewId();
            store.Save(id, new List<object>(), "t");
            store.SetLastSessionId(id);

            store.Delete(id);

            string last = store.LastSessionId();
            Assert.NotEqual(id, last);
        }

        // ---------------- 壊れたファイル ----------------

        [Fact]
        public void BrokenJsonFileIsIgnoredOnList()
        {
            var store = new SessionStore();
            string path = Path.Combine(store.DataDir, "broken.json");
            File.WriteAllText(path, "{ not json", Encoding.UTF8);

            // 例外を投げずに一覧を返すこと
            var list = store.List();
            Assert.NotNull(list);
        }

        [Fact]
        public void BrokenJsonFileReturnsNullOnLoad()
        {
            var store = new SessionStore();
            string path = Path.Combine(store.DataDir, "broken.json");
            File.WriteAllText(path, "{ not json", Encoding.UTF8);

            Assert.Null(store.Load("broken"));
        }
    }
}
