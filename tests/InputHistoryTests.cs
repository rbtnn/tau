using System;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 入力履歴(Ctrl-P / Ctrl-N で呼び出す)。
    [Collection("env")]
    public class InputHistoryTests : IDisposable
    {
        public InputHistoryTests()
        {
            InputHistory.Clear();
        }

        public void Dispose()
        {
            InputHistory.Clear();
        }

        [Fact]
        public void PreviousReturnsTheLastLine()
        {
            InputHistory.Add("one");
            InputHistory.Add("two");

            Assert.Equal("two", InputHistory.Previous(""));
            Assert.Equal("one", InputHistory.Previous("two"));
        }

        [Fact]
        public void PreviousStopsAtTheOldest()
        {
            InputHistory.Add("one");

            Assert.Equal("one", InputHistory.Previous(""));
            Assert.Null(InputHistory.Previous("one"));
        }

        [Fact]
        public void PreviousWithNoHistoryReturnsNull()
        {
            Assert.Null(InputHistory.Previous(""));
        }

        [Fact]
        public void NextWalksBackTowardsTheDraft()
        {
            InputHistory.Add("one");
            InputHistory.Add("two");

            // 2 回さかのぼる
            Assert.Equal("two", InputHistory.Previous("draft"));
            Assert.Equal("one", InputHistory.Previous("two"));

            // 1 回戻る
            Assert.Equal("two", InputHistory.Next("one"));

            // さらに戻ると、たどる前の行に戻る
            Assert.Equal("draft", InputHistory.Next("two"));
            Assert.Equal(-1, InputHistory.Position);
        }

        [Fact]
        public void NextWithNoHistoryReturnsNull()
        {
            Assert.Null(InputHistory.Next(""));
        }

        [Fact]
        public void DuplicateLinesAreNotAdded()
        {
            InputHistory.Add("same");
            InputHistory.Add("same");
            InputHistory.Add("same");

            Assert.Equal(1, InputHistory.Count);
        }

        [Fact]
        public void EmptyLinesAreNotAdded()
        {
            InputHistory.Add("");
            InputHistory.Add(null);

            Assert.Equal(0, InputHistory.Count);
        }

        [Fact]
        public void SameLineIsAddedAgainAfterAnotherLine()
        {
            // 連続でなければ、同じ行でも履歴に残す。
            InputHistory.Add("a");
            InputHistory.Add("b");
            InputHistory.Add("a");

            Assert.Equal(3, InputHistory.Count);
        }

        [Fact]
        public void AddResetsThePosition()
        {
            InputHistory.Add("one");
            InputHistory.Previous("");
            Assert.Equal(0, InputHistory.Position);

            InputHistory.Add("two");
            Assert.Equal(-1, InputHistory.Position);
        }

        [Fact]
        public void ResetStopsWalkingTheHistory()
        {
            InputHistory.Add("one");
            InputHistory.Previous("");
            InputHistory.Reset();

            Assert.Equal(-1, InputHistory.Position);

            // 次は最新から始まる
            Assert.Equal("one", InputHistory.Previous(""));
        }

        [Fact]
        public void OldEntriesAreDroppedBeyondTheLimit()
        {
            for (int i = 0; i < 250; i++) InputHistory.Add("line " + i);

            Assert.Equal(200, InputHistory.Count);

            // 古いものは消え、新しいものが残る
            Assert.Equal("line 249", InputHistory.Previous(""));
        }

        [Fact]
        public void ClearEmptiesTheHistory()
        {
            InputHistory.Add("one");
            InputHistory.Clear();

            Assert.Equal(0, InputHistory.Count);
            Assert.Null(InputHistory.Previous(""));
        }
    }
}
