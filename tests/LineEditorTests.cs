using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // コンソールが UTF-8(chcp 65001)のときだけ、自前の行編集を使う。
    //
    // Console.ReadKey() は Console.InputEncoding でバイトを文字にする。
    // コンソールの実際のコードページ(GetConsoleCP)と食い違うと、
    // 日本語が壊れる(「あ」→ U+7E3A)。UTF-8 なら両者が一致する。
    [Collection("env")]
    public class LineEditorTests : IDisposable
    {
        readonly string dir;
        readonly Dictionary<string, string> savedEnv = new();

        public LineEditorTests()
        {
            dir = Path.Combine(Path.GetTempPath(),
                "tau-lineeditor-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);

            foreach (string key in new[] { "TAU_INPUT_ENCODING", "TAU_DATA_DIR" })
            {
                savedEnv[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string> kv in savedEnv)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { Directory.Delete(dir, true); } catch { }
        }

        Config Cfg()
        {
            return new Config(Path.Combine(dir, "tau.json"));
        }

        [Fact]
        public void ConfigureNeverThrowsInTests()
        {
            // テストでは入力がリダイレクトされているので、行編集は使わない。
            // 例外を投げないことだけ確認する。
            LineEditor.Configure(Cfg());
            Assert.False(LineEditor.Available);
        }

        [Fact]
        public void IsUtf8ConsoleDoesNotThrow()
        {
            // Windows 以外は常に真、Windows では実際のコードページを見る。
            bool utf8 = LineEditor.IsUtf8Console();
            Assert.True(utf8 || !utf8);   // 例外を投げないこと
        }

        [Fact]
        public void ExplicitEncodingDisablesLineEditing()
        {
            // 利用者が shift-jis を指定したら、自動判定より優先して
            // 行編集は使わない(Console.ReadKey で日本語が壊れるため)。
            Config c = Cfg();
            c.InputEncoding = "shift-jis";
            LineEditor.Configure(c);

            Assert.False(LineEditor.Available);
        }

        [Fact]
        public void AutoEncodingIsTheDefault()
        {
            // 既定はコンソールに合わせる。
            Assert.Equal("auto", Cfg().InputEncoding);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("default", true)]
        [InlineData("auto", true)]
        [InlineData("utf-8", false)]
        [InlineData("shift-jis", false)]
        [InlineData("932", false)]
        public void AutoEncodingDetection(string name, bool expected)
        {
            Assert.Equal(expected, Program.IsAutoEncodingPublic(name));
        }

        [Fact]
        public void Utf8AndShiftJisResolveToTheirCodePages()
        {
            Encoding utf8 = Program.ResolveEncoding("utf-8");
            Assert.Equal(65001, utf8.CodePage);

            Encoding sjis = Program.ResolveEncoding("shift-jis");
            if (sjis != null) Assert.Equal(932, sjis.CodePage);
        }
    }
}
