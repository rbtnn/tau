// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  agentweb (Web UI 版) とは独立した実装です。
//  コードは agentweb を参考にしていますが、共通化はしていません。
//  HTTP サーバ / Web UI を持たず、標準入出力だけで対話します。
//
//  ビルド: dotnet publish (tau-cli.csproj)
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tau
{
    // =======================================================================
    //  JSON の読み書き (.NET 標準の System.Text.Json を使う)
    //
    //  内部表現は従来どおり Dictionary<string, object> / List<object>。
    //  パースは JsonDocument に任せ、そこから上の 2 型へ詰め替える。
    //  書き出しは Utf8JsonWriter に任せる。
    //  呼び出し側 (100 箇所以上) は従来の API のまま使える。
    // =======================================================================
    static class Json
    {
        // 日本語をそのまま書き出す。既定のエンコーダは "あ" を \u3042 に
        // エスケープしてしまい、ファイルが読めなくなる。
        // 出力先は LLM へのリクエストとローカルの設定/セッションだけなので、
        // HTML への埋め込みを心配する必要はない。
        static readonly JsonWriterOptions WriterOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        };

        // コメント・末尾カンマは既定でも拒否されるが、方針を明示する。
        // 単一引用符も JsonDocument が拒否する。最大深度は 256 に拡張する。
        static readonly JsonDocumentOptions StrictOptions = new()
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 256,
        };

        // ---------------- パース ----------------

        /// <summary>JSON 文字列を Dictionary / List / プリミティブに変換する。</summary>
        /// <remarks>壊れた入力では JsonException を投げる。</remarks>
        public static object Parse(string s)
        {
            if (s == null) throw new JsonException("JSON input is null");

            // JsonDocument は BOM 付きの入力をそのままでは読めない。
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);

            // 空文字列は「JSON ではない」。JsonDocument も例外にするが、
            // 何が悪いのか分かるメッセージを付けておく。
            if (s.Trim().Length == 0)
                throw new JsonException("unexpected end of JSON");

            using JsonDocument doc = JsonDocument.Parse(s, StrictOptions);
            return FromElement(doc.RootElement);
        }

        /// <summary>JsonElement を Dictionary / List / プリミティブに詰め替える。</summary>
        static object FromElement(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        Dictionary<string, object> d = new();
                        foreach (JsonProperty p in e.EnumerateObject())
                            d[p.Name] = FromElement(p.Value);
                        return d;
                    }
                case JsonValueKind.Array:
                    {
                        List<object> a = new();
                        foreach (JsonElement item in e.EnumerateArray())
                            a.Add(FromElement(item));
                        return a;
                    }
                case JsonValueKind.String:
                    return e.GetString();
                case JsonValueKind.Number:
                    {
                        // 整数は int -> long の順で試し、収まらなければ double。
                        // AsInt が int/long/double のどれでも動くので問題ない。
                        if (e.TryGetInt32(out int i)) return i;
                        if (e.TryGetInt64(out long l)) return l;
                        return e.GetDouble();
                    }
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    // Null / Undefined
                    return null;
            }
        }

        // ---------------- シリアライズ ----------------

        /// <summary>Dictionary / List / プリミティブを JSON 文字列にする。</summary>
        public static string ToString(object o)
        {
            using MemoryStream ms = new();
            using (Utf8JsonWriter w = new(ms, WriterOptions))
                Write(w, o);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static void Write(Utf8JsonWriter w, object o)
        {
            if (o == null)
            {
                w.WriteNullValue();
                return;
            }

            switch (o)
            {
                case string s:
                    w.WriteStringValue(s);
                    return;
                case bool b:
                    w.WriteBooleanValue(b);
                    return;
                case int i:
                    w.WriteNumberValue(i);
                    return;
                case long l:
                    w.WriteNumberValue(l);
                    return;
                case double d:
                    WriteDouble(w, d);
                    return;
                case float f:
                    WriteDouble(w, f);
                    return;
                case decimal m:
                    w.WriteNumberValue(m);
                    return;
                case Dictionary<string, object> dict:
                    w.WriteStartObject();
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        w.WritePropertyName(kv.Key);
                        Write(w, kv.Value);
                    }
                    w.WriteEndObject();
                    return;
                case List<object> list:
                    w.WriteStartArray();
                    foreach (object item in list)
                        Write(w, item);
                    w.WriteEndArray();
                    return;
                default:
                    // それ以外は文字列として扱う (従来と同じ挙動)。
                    w.WriteStringValue(o.ToString());
                    return;
            }
        }

        static void WriteDouble(Utf8JsonWriter w, double d)
        {
            // NaN / Infinity は JSON の数値として表現できない。
            // Utf8JsonWriter は例外を投げるので、null として書き出して
            // アプリ全体を落とさないようにする。
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                w.WriteNullValue();
                return;
            }
            w.WriteNumberValue(d);
        }

        // ---------------- 取り出し ----------------

        public static string AsString(object o)
        {
            if (o == null) return "";
            if (o is string s) return s;
            if (o is bool b) return b ? "true" : "false";
            if (o is double d)
                return d.ToString("R", CultureInfo.InvariantCulture);
            return o.ToString();
        }

        public static int AsInt(object o)
        {
            if (o == null) return 0;
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is double d) return (int)d;
            if (o is bool b) return b ? 1 : 0;

            string t = AsString(o).Trim();
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            // "1.9" のような小数表記も受け付ける (従来は 0 になっていた)。
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                return (int)dv;
            return 0;
        }
    }
}
