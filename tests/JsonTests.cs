using System;
using System.Collections.Generic;
using System.Text.Json;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // Json は .NET 標準の System.Text.Json を使うパーサ／シリアライザ。
    // 壊れると API 応答の解釈がすべて狂うため、ここで重点的に検証する。
    public class JsonTests
    {
        // ---------------- パース: 基本型 ----------------

        [Fact]
        public void ParsesInteger()
        {
            Assert.Equal(123, Json.AsInt(Json.Parse("123")));
        }

        [Fact]
        public void ParsesNegativeInteger()
        {
            Assert.Equal(-42, Json.AsInt(Json.Parse("-42")));
        }

        [Fact]
        public void ParsesZero()
        {
            Assert.Equal(0, Json.AsInt(Json.Parse("0")));
        }

        [Fact]
        public void ParsesDecimal()
        {
            Assert.Equal("3.5", Json.AsString(Json.Parse("3.5")));
        }

        [Fact]
        public void ParsesLargeInteger()
        {
            object o = Json.Parse("1234567890123");
            Assert.Equal(1234567890123L, Convert.ToInt64(o));
        }

        [Fact]
        public void ParsesTrue()
        {
            Assert.Equal("true", Json.AsString(Json.Parse("true")));
        }

        [Fact]
        public void ParsesFalse()
        {
            Assert.Equal("false", Json.AsString(Json.Parse("false")));
        }

        [Fact]
        public void ParsesNull()
        {
            Assert.Null(Json.Parse("null"));
        }

        [Fact]
        public void ParsesString()
        {
            Assert.Equal("hello", Json.AsString(Json.Parse("\"hello\"")));
        }

        // ---------------- パース: エスケープ ----------------

        [Fact]
        public void ParsesEscapedQuote()
        {
            Assert.Equal("a\"b", Json.AsString(Json.Parse("\"a\\\"b\"")));
        }

        [Fact]
        public void ParsesNewlineEscape()
        {
            Assert.Contains("\n", Json.AsString(Json.Parse("\"line1\\nline2\"")));
        }

        [Fact]
        public void ParsesTabEscape()
        {
            Assert.Contains("\t", Json.AsString(Json.Parse("\"tab\\there\"")));
        }

        [Fact]
        public void ParsesCarriageReturnEscape()
        {
            Assert.Contains("\r", Json.AsString(Json.Parse("\"a\\rb\"")));
        }

        [Fact]
        public void ParsesBackslashEscape()
        {
            Assert.Equal("back\\slash", Json.AsString(Json.Parse("\"back\\\\slash\"")));
        }

        [Fact]
        public void ParsesForwardSlashEscape()
        {
            Assert.Equal("a/b", Json.AsString(Json.Parse("\"a\\/b\"")));
        }

        [Fact]
        public void ParsesUnicodeEscape()
        {
            Assert.Equal("あ", Json.AsString(Json.Parse("\"\\u3042\"")));
        }

        [Fact]
        public void ParsesMultipleUnicodeEscapes()
        {
            Assert.Equal("あいう", Json.AsString(Json.Parse("\"\\u3042\\u3044\\u3046\"")));
        }

        [Fact]
        public void ParsesRawJapaneseText()
        {
            Assert.Equal("日本語のテスト", Json.AsString(Json.Parse("\"日本語のテスト\"")));
        }

        [Fact]
        public void ParsesMixedEscapes()
        {
            string s = Json.AsString(Json.Parse("\"a\\tb\\nc\\\\d\\\"e\""));
            Assert.Equal("a\tb\nc\\d\"e", s);
        }

        // ---------------- パース: 配列 ----------------

        [Fact]
        public void ParsesArray()
        {
            var arr = Json.Parse("[1,2,3]") as List<object>;
            Assert.NotNull(arr);
            Assert.Equal(3, arr.Count);
            Assert.Equal(2, Json.AsInt(arr[1]));
        }

        [Fact]
        public void ParsesEmptyArray()
        {
            var arr = Json.Parse("[]") as List<object>;
            Assert.NotNull(arr);
            Assert.Empty(arr);
        }

        [Fact]
        public void ParsesNestedArrays()
        {
            var arr = Json.Parse("[[1],[2,3]]") as List<object>;
            Assert.NotNull(arr);
            Assert.Equal(2, arr.Count);
            var inner = arr[1] as List<object>;
            Assert.NotNull(inner);
            Assert.Equal(2, inner.Count);
        }

        [Fact]
        public void ParsesArrayOfMixedTypes()
        {
            var arr = Json.Parse("[1,\"two\",true,null]") as List<object>;
            Assert.NotNull(arr);
            Assert.Equal(4, arr.Count);
            Assert.Null(arr[3]);
        }

        // ---------------- パース: オブジェクト ----------------

        [Fact]
        public void ParsesObject()
        {
            var d = Json.Parse("{\"a\":1,\"b\":\"x\"}") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Equal(2, d.Count);
            Assert.Equal(1, Json.AsInt(d["a"]));
            Assert.Equal("x", Json.AsString(d["b"]));
        }

        [Fact]
        public void ParsesEmptyObject()
        {
            var d = Json.Parse("{}") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Empty(d);
        }

        [Fact]
        public void ParsesDeeplyNestedStructure()
        {
            var root = Json.Parse("{\"a\":{\"b\":[1,{\"c\":2}]}}") as Dictionary<string, object>;
            Assert.NotNull(root);
            var a = root["a"] as Dictionary<string, object>;
            Assert.NotNull(a);
            var b = a["b"] as List<object>;
            Assert.NotNull(b);
            Assert.Equal(2, b.Count);
            var c = b[1] as Dictionary<string, object>;
            Assert.NotNull(c);
            Assert.Equal(2, Json.AsInt(c["c"]));
        }

        [Fact]
        public void IgnoresWhitespace()
        {
            var d = Json.Parse("  { \t\"a\" : 1 , \n \"b\" : [ 2 ] }  ") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Equal(2, d.Count);
        }

        [Fact]
        public void ParsesJapaneseKeys()
        {
            var d = Json.Parse("{\"名前\":\"太郎\"}") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Equal("太郎", Json.AsString(d["名前"]));
        }

        // ---------------- パース: 不正な入力 ----------------

        [Theory]
        [InlineData("{\"a\":}")]          // 値の欠落
        [InlineData("[1,")]               // 未終端の配列
        [InlineData("")]                  // 空入力
        [InlineData("{\"a\" 1}")]         // コロン欠落
        [InlineData("{")]                 // 未終端のオブジェクト
        [InlineData("[")]                 // 未終端の配列
        [InlineData("}")]                 // 閉じ括弧だけ
        [InlineData("\"unterminated")]    // 未終端の文字列
        [InlineData("{a:1}")]             // クォート無しのキー
        [InlineData("[1 2]")]             // カンマ欠落
        public void ThrowsOnMalformedInput(string json)
        {
            Assert.ThrowsAny<Exception>(() => Json.Parse(json));
        }

        [Fact]
        public void ThrowsOnTrailingGarbageThatIsNotParsed()
        {
            // 値の後に余分な文字があっても、先頭の値は読める
            object o = Json.Parse("1");
            Assert.Equal(1, Json.AsInt(o));
        }

        // ---------------- シリアライズ ----------------

        [Fact]
        public void SerialisesNull()
        {
            Assert.Equal("null", Json.ToString(null));
        }

        [Fact]
        public void SerialisesTrue()
        {
            Assert.Equal("true", Json.ToString(true));
        }

        [Fact]
        public void SerialisesFalse()
        {
            Assert.Equal("false", Json.ToString(false));
        }

        [Fact]
        public void SerialisesInteger()
        {
            Assert.Equal("42", Json.ToString(42));
        }

        [Fact]
        public void SerialisesString()
        {
            Assert.Equal("\"hi\"", Json.ToString("hi"));
        }

        [Fact]
        public void EscapesQuote()
        {
            Assert.Equal("\"a\\\"b\"", Json.ToString("a\"b"));
        }

        [Fact]
        public void EscapesNewline()
        {
            Assert.Contains("\\n", Json.ToString("line1\nline2"));
        }

        [Fact]
        public void EscapesTab()
        {
            Assert.Contains("\\t", Json.ToString("tab\there"));
        }

        [Fact]
        public void EscapesBackslash()
        {
            Assert.Contains("\\\\", Json.ToString("back\\slash"));
        }

        [Fact]
        public void EscapesCarriageReturn()
        {
            Assert.Contains("\\r", Json.ToString("a\rb"));
        }

        [Fact]
        public void KeepsJapaneseTextUnescaped()
        {
            Assert.Contains("日本語のテスト", Json.ToString("日本語のテスト"));
        }

        [Fact]
        public void SerialisesArray()
        {
            Assert.Equal("[1,2,3]", Json.ToString(new List<object> { 1, 2, 3 }));
        }

        [Fact]
        public void SerialisesEmptyArray()
        {
            Assert.Equal("[]", Json.ToString(new List<object>()));
        }

        [Fact]
        public void SerialisesObject()
        {
            var d = new Dictionary<string, object> { ["a"] = 1 };
            Assert.Equal("{\"a\":1}", Json.ToString(d));
        }

        [Fact]
        public void SerialisesEmptyObject()
        {
            Assert.Equal("{}", Json.ToString(new Dictionary<string, object>()));
        }

        // ---------------- 往復 ----------------

        [Theory]
        [InlineData("\"plain\"")]
        [InlineData("\"日本語\"")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"a\":1,\"b\":\"x\",\"c\":[true,null]}")]
        [InlineData("{\"nested\":{\"deep\":{\"value\":42}}}")]
        [InlineData("[]")]
        [InlineData("{}")]
        public void RoundTripIsStable(string json)
        {
            object first = Json.Parse(json);
            string text = Json.ToString(first);
            object second = Json.Parse(text);
            string text2 = Json.ToString(second);
            Assert.Equal(text, text2);
        }

        [Fact]
        public void RoundTripPreservesJapanese()
        {
            string original = "こんにちは、世界！";
            string json = Json.ToString(original);
            Assert.Equal(original, Json.AsString(Json.Parse(json)));
        }

        [Fact]
        public void RoundTripPreservesEscapes()
        {
            string original = "line1\nline2\ttab \"quoted\" back\\slash";
            string json = Json.ToString(original);
            Assert.Equal(original, Json.AsString(Json.Parse(json)));
        }

        // ---------------- AsString / AsInt ----------------

        [Fact]
        public void AsStringOnNullIsEmpty()
        {
            Assert.Equal("", Json.AsString(null));
        }

        [Fact]
        public void AsStringOnInt()
        {
            Assert.Equal("1", Json.AsString(1));
        }

        [Fact]
        public void AsStringOnBool()
        {
            Assert.Equal("true", Json.AsString(true));
            Assert.Equal("false", Json.AsString(false));
        }

        [Fact]
        public void AsStringOnString()
        {
            Assert.Equal("x", Json.AsString("x"));
        }

        [Fact]
        public void AsIntOnNullIsZero()
        {
            Assert.Equal(0, Json.AsInt(null));
        }

        [Fact]
        public void AsIntOnNumericString()
        {
            Assert.Equal(12, Json.AsInt("12"));
        }

        [Fact]
        public void AsIntOnNonNumericStringIsZero()
        {
            Assert.Equal(0, Json.AsInt("abc"));
        }

        [Fact]
        public void AsIntOnBool()
        {
            Assert.Equal(1, Json.AsInt(true));
            Assert.Equal(0, Json.AsInt(false));
        }

        [Fact]
        public void AsIntOnLong()
        {
            Assert.Equal(7, Json.AsInt(7L));
        }

        [Fact]
        public void AsIntOnDouble()
        {
            Assert.Equal(3, Json.AsInt(3.9));
        }

        // ---------------- System.Text.Json 固有の挙動 ----------------

        [Fact]
        public void BomIsAccepted()
        {
            // ファイルから読むと BOM が付くことがある。JsonDocument は
            // そのままでは読めないので Parse 側で取り除いている。
            var d = Json.Parse("\uFEFF{\"a\":1}") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Equal(1, Json.AsInt(d["a"]));
        }

        [Fact]
        public void WhitespaceOnlyIsRejected()
        {
            Assert.ThrowsAny<JsonException>(() => Json.Parse("   "));
            Assert.ThrowsAny<JsonException>(() => Json.Parse("\t\n "));
        }

        [Fact]
        public void NullInputIsRejected()
        {
            Assert.ThrowsAny<JsonException>(() => Json.Parse(null));
        }

        [Fact]
        public void TrailingContentIsRejected()
        {
            // 1 つの値の後に別の値が続くのは JSON として不正。
            Assert.ThrowsAny<JsonException>(() => Json.Parse("{\"a\":1}{\"b\":2}"));
            Assert.ThrowsAny<JsonException>(() => Json.Parse("1 2"));
        }

        [Fact]
        public void SingleQuotesAreRejected()
        {
            Assert.ThrowsAny<JsonException>(() => Json.Parse("{'a':1}"));
        }

        [Fact]
        public void TrailingCommaIsRejected()
        {
            Assert.ThrowsAny<JsonException>(() => Json.Parse("{\"a\":1,}"));
            Assert.ThrowsAny<JsonException>(() => Json.Parse("[1,]"));
        }

        [Fact]
        public void CommentsAreRejected()
        {
            Assert.ThrowsAny<JsonException>(() => Json.Parse("{\"a\":1 /* c */}"));
            Assert.ThrowsAny<JsonException>(() => Json.Parse("// c\n{\"a\":1}"));
        }

        [Fact]
        public void JapaneseIsNotEscapedOnWrite()
        {
            // 既定のエンコーダだと "あ" が \u3042 になる。
            // 設定ファイルやセッションが読めなくなるため、そのまま書く。
            string s = Json.ToString("あいう");
            Assert.Equal("\"あいう\"", s);
            Assert.DoesNotContain("\\u", s);
        }

        [Fact]
        public void ControlCharactersAreEscapedOnWrite()
        {
            string s = Json.ToString("a\u0001b");
            Assert.Contains("\\u0001", s, StringComparison.OrdinalIgnoreCase);
            // 往復しても元に戻る
            Assert.Equal("a\u0001b", Json.AsString(Json.Parse(s)));
        }

        [Fact]
        public void NanIsWrittenAsNull()
        {
            // JSON に NaN は無い。Utf8JsonWriter は例外を投げるので
            // null にしてアプリを落とさないようにしている。
            Assert.Equal("null", Json.ToString(double.NaN));
            Assert.Equal("null", Json.ToString(double.PositiveInfinity));
            Assert.Equal("null", Json.ToString(double.NegativeInfinity));
        }

        [Fact]
        public void NanInsideObjectDoesNotThrow()
        {
            var d = new Dictionary<string, object> { ["v"] = double.NaN };
            string s = Json.ToString(d);
            Assert.Equal("{\"v\":null}", s);
        }

        [Fact]
        public void DoubleUsesRoundTripFormat()
        {
            // 精度が落ちないこと
            Assert.Equal("0.1", Json.ToString(0.1));
            Assert.Equal("1.5", Json.ToString(1.5));
            Assert.Equal(0.1, Json.Parse(Json.ToString(0.1)));
        }

        [Fact]
        public void FloatIsWritten()
        {
            Assert.Equal("1.5", Json.ToString(1.5f));
        }

        [Fact]
        public void DecimalIsWritten()
        {
            Assert.Equal("1.5", Json.ToString(1.5m));
        }

        [Fact]
        public void AsIntAcceptsDecimalString()
        {
            // "1.9" は従来 0 になっていた。int に切り捨てて返す。
            Assert.Equal(1, Json.AsInt("1.9"));
            Assert.Equal(0, Json.AsInt("abc"));
        }

        [Fact]
        public void AsStringFormatsDoubleInvariantly()
        {
            // カルチャで小数点が "," にならないこと
            Assert.Equal("1.5", Json.AsString(1.5));
        }

        [Fact]
        public void LargeIntegersKeepPrecision()
        {
            var d = Json.Parse("{\"big\":123456789012345}") as Dictionary<string, object>;
            Assert.IsType<long>(d["big"]);
            Assert.Equal(123456789012345L, (long)d["big"]);
        }

        [Fact]
        public void IntStaysInt()
        {
            var d = Json.Parse("{\"n\":42}") as Dictionary<string, object>;
            Assert.IsType<int>(d["n"]);
        }

        [Fact]
        public void ExponentBecomesDouble()
        {
            var d = Json.Parse("{\"e\":1e10}") as Dictionary<string, object>;
            Assert.Equal(1e10, (double)d["e"]);
        }

        [Fact]
        public void UnicodeEscapeDecodesToChar()
        {
            Assert.Equal("あ", Json.AsString(Json.Parse("\"\\u3042\"")));
        }

        [Fact]
        public void SurrogatePairDecodes()
        {
            // 絵文字 (U+1F600) はサロゲートペア
            string s = Json.AsString(Json.Parse("\"\\uD83D\\uDE00\""));
            Assert.Equal("\U0001F600", s);
        }

        [Fact]
        public void DeeplyNestedRoundTrip()
        {
            string json = "{\"a\":{\"b\":{\"c\":{\"d\":[1,[2,[3]]]}}}}";
            object v = Json.Parse(json);
            Assert.Equal(json, Json.ToString(v));
        }

        [Fact]
        public void EmptyKeyIsPreserved()
        {
            var d = Json.Parse("{\"\":1}") as Dictionary<string, object>;
            Assert.NotNull(d);
            Assert.Equal(1, Json.AsInt(d[""]));
        }

        [Fact]
        public void DuplicateKeysLastWins()
        {
            // Dictionary への代入なので後勝ち。挙動を固定しておく。
            var d = Json.Parse("{\"a\":1,\"a\":2}") as Dictionary<string, object>;
            Assert.Equal(2, Json.AsInt(d["a"]));
        }

        // ---------------- 実データ: OpenAI 形式 ----------------

        [Fact]
        public void ParsesOpenAiChatCompletion()
        {
            string sample =
                "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\"," +
                "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
                "\"content\":\"こんにちは\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5," +
                "\"total_tokens\":15}}";

            var root = Json.Parse(sample) as Dictionary<string, object>;
            Assert.NotNull(root);

            var choices = root["choices"] as List<object>;
            Assert.NotNull(choices);
            Assert.NotEmpty(choices);

            var first = choices[0] as Dictionary<string, object>;
            var msg = first["message"] as Dictionary<string, object>;
            Assert.Equal("こんにちは", Json.AsString(msg["content"]));

            var usage = root["usage"] as Dictionary<string, object>;
            Assert.Equal(15, Json.AsInt(usage["total_tokens"]));
        }

        [Fact]
        public void ParsesOpenAiError()
        {
            string sample =
                "{\"error\":{\"message\":\"Incorrect API key\",\"type\":\"invalid_request_error\"}}";
            var root = Json.Parse(sample) as Dictionary<string, object>;
            Assert.NotNull(root);
            Assert.True(root.ContainsKey("error"));
            var err = root["error"] as Dictionary<string, object>;
            Assert.Equal("Incorrect API key", Json.AsString(err["message"]));
        }

        [Fact]
        public void ParsesToolCallWithNestedArgumentsJson()
        {
            string sample =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\"," +
                "\"tool_calls\":[{\"id\":\"call_0\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"read_file\"," +
                "\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]}}]}";

            var root = Json.Parse(sample) as Dictionary<string, object>;
            var choices = root["choices"] as List<object>;
            var first = choices[0] as Dictionary<string, object>;
            var message = first["message"] as Dictionary<string, object>;
            var calls = message["tool_calls"] as List<object>;
            var call = calls[0] as Dictionary<string, object>;
            var fn = call["function"] as Dictionary<string, object>;

            Assert.Equal("read_file", Json.AsString(fn["name"]));

            // arguments は JSON 文字列なので、さらにパースする
            string argsJson = Json.AsString(fn["arguments"]);
            var args = Json.Parse(argsJson) as Dictionary<string, object>;
            Assert.Equal("a.txt", Json.AsString(args["path"]));
        }

        [Fact]
        public void ParsesStreamingDelta()
        {
            string sample =
                "{\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1," +
                "\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}," +
                "\"finish_reason\":null}]}";

            var root = Json.Parse(sample) as Dictionary<string, object>;
            var choices = root["choices"] as List<object>;
            var ch = choices[0] as Dictionary<string, object>;
            var delta = ch["delta"] as Dictionary<string, object>;
            Assert.Equal("OK", Json.AsString(delta["content"]));
        }

        [Fact]
        public void ParsesReasoningContent()
        {
            string sample =
                "{\"choices\":[{\"message\":{\"content\":\"answer\"," +
                "\"reasoning_content\":\"thinking...\"}}]}";

            var root = Json.Parse(sample) as Dictionary<string, object>;
            var choices = root["choices"] as List<object>;
            var first = choices[0] as Dictionary<string, object>;
            var msg = first["message"] as Dictionary<string, object>;

            Assert.Equal("answer", Json.AsString(msg["content"]));
            Assert.Equal("thinking...", Json.AsString(msg["reasoning_content"]));
        }

        // ---------------- 大きめの入力 ----------------

        [Fact]
        public void ParsesLongString()
        {
            string longText = new string('あ', 5000);
            string json = Json.ToString(longText);
            Assert.Equal(longText, Json.AsString(Json.Parse(json)));
        }

        [Fact]
        public void ParsesLargeArray()
        {
            var list = new List<object>();
            for (int i = 0; i < 1000; i++) list.Add(i);
            string json = Json.ToString(list);

            var back = Json.Parse(json) as List<object>;
            Assert.NotNull(back);
            Assert.Equal(1000, back.Count);
            Assert.Equal(999, Json.AsInt(back[999]));
        }
    }
}
