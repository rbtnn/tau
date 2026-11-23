using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 複数行の入力（パイプ・貼り付け）。
    // ソースコードを貼り付けたときに 1 行ずつ送られないことを確認する。
    [Collection("env")]
    public class MultiLineInputTests : IDisposable
    {
        readonly TauRunner runner;
        readonly MockLlm llm;

        public MultiLineInputTests()
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

        // 保存された最後のユーザーメッセージ。
        static string LastUserMessage(Dictionary<string, object> session)
        {
            if (session == null || !session.ContainsKey("messages")) return null;
            var msgs = session["messages"] as List<object>;
            if (msgs == null) return null;
            string found = null;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m != null && Json.AsString(m["role"]) == "user")
                    found = Json.AsString(m["content"]);
            }
            return found;
        }

        static int CountUserMessages(Dictionary<string, object> session)
        {
            if (session == null || !session.ContainsKey("messages")) return 0;
            var msgs = session["messages"] as List<object>;
            if (msgs == null) return 0;
            int n = 0;
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m != null && Json.AsString(m["role"]) == "user") n++;
            }
            return n;
        }

        // ---------------- パイプ入力 ----------------

        [Fact]
        public void PipedLinesBecomeOneMessage()
        {
            runner.WriteConfig(llm.Url);

            string stdin = "def f(x):\n    return x * 2\n\nprint(f(3))\n/exit\n";
            runner.Run(stdin);

            Dictionary<string, object> s = runner.LastSession();
            Assert.Equal(1, CountUserMessages(s));

            string msg = LastUserMessage(s);
            Assert.Equal("def f(x):\n    return x * 2\n\nprint(f(3))", msg);
        }

        [Fact]
        public void PipedBlankLinesArePreserved()
        {
            // Python のコードは空行を多用する。途中で切れないこと。
            runner.WriteConfig(llm.Url);

            string stdin = "class A:\n    pass\n\n\nclass B:\n    pass\n/exit\n";
            runner.Run(stdin);

            string msg = LastUserMessage(runner.LastSession());
            Assert.Contains("class A:", msg);
            Assert.Contains("class B:", msg);
            Assert.Contains("\n\n\n", msg);
        }

        [Fact]
        public void PipedSourceCodeKeepsIndentation()
        {
            runner.WriteConfig(llm.Url);

            string stdin = "if (x > 0) {\n    Console.WriteLine(x);\n}\n/exit\n";
            runner.Run(stdin);

            string msg = LastUserMessage(runner.LastSession());
            Assert.Equal("if (x > 0) {\n    Console.WriteLine(x);\n}", msg);
        }

        [Fact]
        public void CommandSplitsPipedMessages()
        {
            runner.WriteConfig(llm.Url);

            string stdin = "line1\nline2\n/list\nline3\nline4\n/exit\n";
            runner.Run(stdin);

            // 2 つのメッセージに分かれる
            Dictionary<string, object> s = runner.LastSession();
            Assert.Equal(2, CountUserMessages(s));

            var msgs = s["messages"] as List<object>;
            List<string> users = new();
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user")
                    users.Add(Json.AsString(m["content"]));
            }
            Assert.Equal("line1\nline2", users[0]);
            Assert.Equal("line3\nline4", users[1]);
        }

        [Fact]
        public void PipedInputIsNotAnnounced()
        {
            // 一括で読まなくなったので行数の通知は出さない。
            // (先読みすると確認プロンプトが答えられなくなるため)
            runner.WriteConfig(llm.Url);
            string output = runner.Run("line1\nline2\n/exit\n");
            Assert.DoesNotContain("reading", output);
        }

        [Fact]
        public void PipedSingleLineStillWorks()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("ひとこと\n/exit\n");
            Assert.Equal("ひとこと", LastUserMessage(runner.LastSession()));
        }

        [Fact]
        public void PipedCommandOnlyWorks()
        {
            runner.WriteConfig(llm.Url);
            string output = runner.Run("/list\n/exit\n");
            Assert.Contains("Saved sessions", output);
        }

        [Fact]
        public void PipedJapaneseMultiLineIsKept()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("1行目の日本語\n2行目の日本語\n3行目の日本語\n/exit\n");

            string msg = LastUserMessage(runner.LastSession());
            Assert.Equal("1行目の日本語\n2行目の日本語\n3行目の日本語", msg);
        }

        [Fact]
        public void PipedMessageIsSentToLlmAsOneMessage()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("first\nsecond\n/exit\n");

            Assert.NotEmpty(llm.Requests);
            Dictionary<string, object> req = llm.Requests[llm.Requests.Count - 1];
            var msgs = req["messages"] as List<object>;
            string lastUser = "";
            foreach (object o in msgs)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (Json.AsString(m["role"]) == "user") lastUser = Json.AsString(m["content"]);
            }
            Assert.Equal("first\nsecond", lastUser);
        }

        [Fact]
        public void PipedNulCharactersAreStripped()
        {
            runner.WriteConfig(llm.Url);
            runner.Run("あ\0い\nう\0え\n/exit\n");

            string msg = LastUserMessage(runner.LastSession());
            Assert.NotNull(msg);
            foreach (char c in msg) Assert.NotEqual('\0', c);
        }

        // ---------------- 大きなコード ----------------

        [Fact]
        public void LargePipedCodeIsKeptIntact()
        {
            runner.WriteConfig(llm.Url);

            StringBuilder sb = new();
            sb.Append("public class Sample {\n");
            for (int i = 0; i < 40; i++)
                sb.Append("    public int Field" + i + " { get; set; }\n");
            sb.Append("}\n");
            string code = sb.ToString();

            runner.Run(code + "/exit\n");

            string msg = LastUserMessage(runner.LastSession());
            Assert.Equal(code.TrimEnd('\n'), msg);
        }
    }
}
