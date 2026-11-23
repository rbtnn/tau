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
            StringWriter sw = new StringWriter();
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
        public void CodeFenceIsRenderedAsABox()
        {
            string output = Render("```python\nprint(1)\n```\n");
            Assert.Contains("python", output);
            Assert.Contains("print(1)", output);
            Assert.Contains("┌", output);
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
