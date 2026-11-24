using System;
using System.Collections.Generic;
using System.IO;
using Tau;
using Xunit;

namespace Tau.Tests
{
    [Collection("env")]
    public class MarkdownTests
    {
        // ---------------- 表示幅 ----------------

        [Fact]
        public void FullWidthCharCountsAsTwo()
        {
            Assert.Equal(2, MarkdownRenderer.VisibleLength("あ"));
        }

        [Fact]
        public void HalfWidthCharCountsAsOne()
        {
            Assert.Equal(1, MarkdownRenderer.VisibleLength("A"));
        }

        [Fact]
        public void MixedWidthAddsUp()
        {
            Assert.Equal(3, MarkdownRenderer.VisibleLength("あA"));
        }

        [Fact]
        public void HalfWidthKatakanaCountsAsOne()
        {
            Assert.Equal(1, MarkdownRenderer.VisibleLength("ｱ"));
        }

        [Fact]
        public void AsciiPunctuationCountsAsOne()
        {
            Assert.Equal(1, MarkdownRenderer.VisibleLength("-"));
            Assert.Equal(1, MarkdownRenderer.VisibleLength("."));
        }

        [Fact]
        public void KanjiCountsAsTwo()
        {
            Assert.Equal(2, MarkdownRenderer.VisibleLength("漢"));
        }

        [Fact]
        public void FullWidthLatinCountsAsTwo()
        {
            Assert.Equal(2, MarkdownRenderer.VisibleLength("Ａ"));
        }

        [Fact]
        public void EmptyStringIsZero()
        {
            Assert.Equal(0, MarkdownRenderer.VisibleLength(""));
        }

        [Fact]
        public void AnsiEscapesAreNotCounted()
        {
            string colored = MarkdownRenderer.Inline("**bold**");
            Assert.Equal(4, MarkdownRenderer.VisibleLength(colored));
        }

        [Fact]
        public void IsWideForCjk()
        {
            Assert.True(MarkdownRenderer.IsWide('あ'));
            Assert.True(MarkdownRenderer.IsWide('漢'));
            Assert.False(MarkdownRenderer.IsWide('A'));
            Assert.False(MarkdownRenderer.IsWide('1'));
        }

        [Theory]
        // 日本語と半角英数字が混在したときの表示幅
        [InlineData("商品ID: A-1024", 14)]          // 4 全角(8) + 6 半角
        [InlineData("価格は 1,980 JPY", 16)]        // 6 全角(6) + 10 半角
        [InlineData("あAいBうC", 9)]                // 3 全角 + 3 半角
        [InlineData("ABC only", 8)]
        [InlineData("すべて全角", 10)]
        public void MixedWidthIsCountedCorrectly(string text, int expected)
        {
            Assert.Equal(expected, MarkdownRenderer.VisibleLength(text));
        }

        [Fact]
        public void MixedWidthTableAlignsColumns()
        {
            // 全角・半角が混在しても、表の区切りが同じ桁にそろうこと
            string md = "| 名前 | 数量 |\n|---|---|\n| りんご L | 3 |\n| A-1024 | 12 |\n";
            string output = Render(md);
            string[] lines = output.Split('\n');

            List<int> bars = new();
            foreach (string line in lines)
            {
                if (!line.Contains('│')) continue;
                int col = 0;
                foreach (char c in line)
                {
                    if (c == '│') bars.Add(col);
                    col += MarkdownRenderer.IsWide(c) ? 2 : 1;
                }
            }

            Assert.True(bars.Count > 0, "the table should be rendered: " + output);

            // 罫線行を除いた各行の区切り位置が、すべて同じであること
            List<List<int>> perRow = new();
            foreach (string line in lines)
            {
                if (!line.Contains('│')) continue;
                List<int> cols = new();
                int col = 0;
                foreach (char c in line)
                {
                    if (c == '│') cols.Add(col);
                    col += MarkdownRenderer.IsWide(c) ? 2 : 1;
                }
                perRow.Add(cols);
            }

            Assert.True(perRow.Count >= 3, "header and two rows should be rendered: " + output);
            for (int i = 1; i < perRow.Count; i++)
                Assert.Equal(perRow[0], perRow[i]);
        }

        // Ambiguous(A) の文字は端末次第だが、分類としては全角側に入る
        [Theory]
        [InlineData('①')]
        [InlineData('②')]
        [InlineData('←')]
        [InlineData('→')]
        [InlineData('■')]
        [InlineData('★')]
        [InlineData('☆')]
        [InlineData('※')]
        [InlineData('℡')]
        [InlineData('№')]
        [InlineData('Ⅰ')]
        public void AmbiguousCharsAreTreatedAsWide(char c)
        {
            Assert.True(MarkdownRenderer.IsWide(c));
        }

        [Fact]
        public void AmbiguousOverrideForcesNarrow()
        {
            Environment.SetEnvironmentVariable("TAU_AMBIGUOUS_WIDTH", "1");
            try
            {
                Assert.True(MarkdownRenderer.AmbiguousIsNarrow());
                Assert.Equal(1, MarkdownRenderer.MeasuredAmbiguousWidth());
            }
            finally
            {
                Environment.SetEnvironmentVariable("TAU_AMBIGUOUS_WIDTH", null);
            }
        }

        [Fact]
        public void AmbiguousOverrideForcesWide()
        {
            Environment.SetEnvironmentVariable("TAU_AMBIGUOUS_WIDTH", "2");
            try
            {
                Assert.False(MarkdownRenderer.AmbiguousIsNarrow());
                Assert.Equal(2, MarkdownRenderer.MeasuredAmbiguousWidth());
            }
            finally
            {
                Environment.SetEnvironmentVariable("TAU_AMBIGUOUS_WIDTH", null);
            }
        }

        // ---------------- 記号の除去 ----------------

        [Theory]
        [InlineData("**x**", "x")]
        [InlineData("*x*", "x")]
        [InlineData("`c`", "c")]
        [InlineData("[t](u)", "t")]
        [InlineData("~~s~~", "s")]
        [InlineData("plain", "plain")]
        public void StripMarkdownRemovesMarkers(string input, string expected)
        {
            Assert.Equal(expected, MarkdownRenderer.StripMarkdown(input));
        }

        [Fact]
        public void StripMarkdownOnNullIsEmpty()
        {
            Assert.Equal("", MarkdownRenderer.StripMarkdown(null));
        }

        [Fact]
        public void StripMarkdownKeepsJapanese()
        {
            Assert.Equal("日本語", MarkdownRenderer.StripMarkdown("**日本語**"));
        }

        // ---------------- インライン書式 ----------------

        [Fact]
        public void InlineOnNullIsEmpty()
        {
            Assert.Equal("", MarkdownRenderer.Inline(null));
        }

        [Fact]
        public void InlineRemovesBoldMarkers()
        {
            string s = MarkdownRenderer.Inline("**bold**");
            Assert.DoesNotContain("**", s);
            Assert.Contains("bold", s);
        }

        [Fact]
        public void InlineRemovesCodeMarkers()
        {
            string s = MarkdownRenderer.Inline("`code`");
            Assert.DoesNotContain("`", s);
            Assert.Contains("code", s);
        }

        [Fact]
        public void InlineTurnsLinkIntoTextAndUrl()
        {
            string s = MarkdownRenderer.Inline("[t](https://example.com)");
            Assert.DoesNotContain("[t](https://example.com)", s);
            Assert.Contains("t", s);
            Assert.Contains("https://example.com", s);
        }

        // ---------------- テーブル ----------------

        private static string Render(string md)
        {
            // 改行コードを LF に固定する。Windows の StringWriter は
            // 既定で CRLF を書くため、Split('\n') で比べると行末に
            // '\r' が残ってしまう。
            StringWriter sw = new StringWriter { NewLine = "\n" };
            TextWriter old = Console.Out;
            try
            {
                Console.SetOut(sw);
                MarkdownRenderer.Configure(false);   // 色なしで比較しやすくする
                MarkdownRenderer.Render(md);
            }
            finally
            {
                Console.SetOut(old);
            }
            return sw.ToString();
        }

        [Fact]
        public void TableIsDrawnWithBoxDrawingCharacters()
        {
            string output = Render("| a | b |\n|:--|--:|\n| 1 | 2 |\n");
            Assert.Contains("┌", output);
            Assert.Contains("│", output);
            Assert.Contains("└", output);
            Assert.Contains("┬", output);
        }

        [Fact]
        public void TableMarkupIsNotPrintedVerbatim()
        {
            string output = Render("| a | b |\n|:--|--:|\n| 1 | 2 |\n");
            Assert.DoesNotContain("|:--|--:|", output);
            Assert.DoesNotContain("| a | b |", output);
        }

        [Fact]
        public void TableCellsArePaddedToTheSameWidth()
        {
            string output = Render("| あ | A |\n|:--|:--|\n| い | B |\n");

            // 各行の │ の位置が揃っていること(罫線の幅が同じ)
            var widths = new List<int>();
            foreach (string line in output.Split('\n'))
            {
                string t = line.TrimEnd();
                if (t.Contains("│") || t.Contains("┌") || t.Contains("└"))
                    widths.Add(MarkdownRenderer.VisibleLength(t));
            }
            Assert.True(widths.Count > 0);
            foreach (int w in widths)
                Assert.Equal(widths[0], w);
        }

        // ---------------- インライン書式と "_" の扱い ----------------
        //
        // "_" は識別子の区切り (XXX_YYY_ZZZ) にもよく使われる。
        // 単語の内側の "_" を斜体とみなすと、識別子から "_" が消えてしまう。

        private static string InlineText(string s)
        {
            MarkdownRenderer.Configure(false);   // 色なしで比較しやすくする
            return MarkdownRenderer.Inline(s);
        }

        [Theory]
        [InlineData("XXX_YYY_ZZZ")]
        [InlineData("FOO_BAR")]
        [InlineData("snake_case_name")]
        [InlineData("a_b_c")]
        [InlineData("x_1_y_2_z")]
        [InlineData("A_B_C_D_E")]
        [InlineData("call_me_now()")]
        [InlineData("foo_bar baz_qux")]
        public void IntrawordUnderscoresAreKept(string text)
        {
            Assert.Equal(text, InlineText(text));
        }

        [Theory]
        [InlineData("_italic_", "italic")]
        [InlineData("__bold__", "bold")]
        [InlineData("prefix _italic_ suffix", "prefix italic suffix")]
        [InlineData("_日本語_", "日本語")]
        public void UnderscoreEmphasisStillWorks(string input, string expected)
        {
            Assert.Equal(expected, InlineText(input));
        }

        [Theory]
        [InlineData("*bold*", "bold")]
        [InlineData("**strong**", "strong")]
        [InlineData("a*b*c", "abc")]
        public void AsteriskEmphasisStillWorks(string input, string expected)
        {
            Assert.Equal(expected, InlineText(input));
        }

        [Fact]
        public void AsteriskEmphasisIgnoresIntrawordRule()
        {
            // "*" は識別子に使われないので、単語の内側でも装飾してよい
            Assert.Equal("abc", InlineText("a*b*c"));
        }

        [Fact]
        public void UnderscoreInsideBoldIsKept()
        {
            Assert.Equal("a_b_c", InlineText("**a_b_c**"));
        }

        [Fact]
        public void LoneUnderscoreIsKept()
        {
            Assert.Equal("_ leading space", InlineText("_ leading space"));
            Assert.Equal("trailing _ space", InlineText("trailing _ space"));
        }

        [Fact]
        public void JapaneseUnderscoreIsNotEmphasis()
        {
            // 日本語は単語の区切りにならないので、識別子として残す
            Assert.Equal("日本語_テスト_日本語", InlineText("日本語_テスト_日本語"));
        }

        [Fact]
        public void HeadingMarkersAreRemoved()
        {
            string output = Render("# Heading\n");
            Assert.DoesNotContain("# Heading", output);
            Assert.Contains("Heading", output);
        }

        [Fact]
        public void BulletBecomesDot()
        {
            string output = Render("- item\n");
            Assert.Contains("・item", output);
        }

        [Fact]
        public void CodeFenceShowsLanguageLabel()
        {
            string output = Render("```python\nprint(1)\n```\n");
            Assert.Contains("python", output);
            Assert.Contains("print(1)", output);
        }

        [Fact]
        public void CodeBlockHasNoBoxBorders()
        {
            // 罫線で囲むと、端末からコピーしたときに │ や余白まで
            // 貼り付いてしまう。コードだけをコピーできるようにする。
            string output = Render("```python\nprint(1)\n```\n");
            Assert.DoesNotContain("┌", output);
            Assert.DoesNotContain("└", output);
            Assert.DoesNotContain("│", output);
        }

        [Fact]
        public void CodeLinesStartAtColumnZero()
        {
            // 行頭に余白を置かない(コピーした文字列を汚さないため)
            string output = Render("```python\nprint(1)\n```\n");
            bool found = false;
            foreach (string line in output.Split('\n'))
            {
                if (line == "print(1)") found = true;
            }
            Assert.True(found, "code line must start at column 0, got:\n" + output);
        }

        [Fact]
        public void CodeBlockPreservesIndentation()
        {
            string output = Render("```python\nif x:\n    print(1)\n```\n");
            bool found = false;
            foreach (string line in output.Split('\n'))
            {
                if (line == "    print(1)") found = true;
            }
            Assert.True(found, "indentation must be preserved, got:\n" + output);
        }

        [Fact]
        public void CodeBlockKeepsBlankLinesInside()
        {
            string output = Render("```\na\n\nb\n```\n");
            var lines = new List<string>(output.Split('\n'));
            Assert.Contains("a", lines);
            Assert.Contains("", lines);
            Assert.Contains("b", lines);
        }

        [Fact]
        public void CodeBlockDoesNotPadWithTrailingSpaces()
        {
            // 右側を空白で埋めると、コピーしたときに末尾の空白が付く
            string output = Render("```\nx\nlonger line\n```\n");
            foreach (string line in output.Split('\n'))
            {
                if (line.StartsWith("x") || line.StartsWith("longer"))
                    Assert.Equal(line.TrimEnd(), line);
            }
        }

        [Fact]
        public void QuoteGetsAVerticalBar()
        {
            string output = Render("> quoted\n");
            Assert.Contains("│ quoted", output);
        }

        [Fact]
        public void HorizontalRuleBecomesALine()
        {
            string output = Render("---\n");
            Assert.Contains("─", output);
        }

        [Fact]
        public void EmptyInputProducesNoCrash()
        {
            Assert.NotNull(Render(""));
            Assert.NotNull(Render(null));
        }
    }
}
