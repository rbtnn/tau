// ---------------------------------------------------------------------------
//  DocumentJson - 補助ツールが返す JSON の組み立て
//
//  tau の read_document と、スタンドアロンの pdf2text / word2text /
//  excel2text が同じ形の JSON を返すようにするための共通処理。
//  ここだけを各 exe が共有する(Reader 本体は tau だけが持つ)。
// ---------------------------------------------------------------------------
using System;
using System.Text;

namespace Tau
{
    static class DocumentJson
    {
        // 結果の入れ物。
        public class Result
        {
            public string Text;      // 取り出した本文(失敗時は null)
            public string Error;     // 失敗した理由
            public string Describe;  // 構造の説明(切り分け用)
            public int Pages;        // PDF のページ数(分からなければ 0)
        }

        // tau の補助ツールが返していた JSON と同じ形にする。
        // スタンドアロンの exe と tau の両方がこれを使う。
        public static string ToJson(string kind, Result r, int maxRows)
        {
            StringBuilder sb = new();
            sb.Append("{\"ok\":").Append(r.Text != null ? "true" : "false");

            if (kind == "pdf") sb.Append(",\"pages\":").Append(r.Pages);

            if (r.Text != null)
            {
                sb.Append(",\"length\":").Append(r.Text.Length);
                if (kind == "excel" && maxRows > 0) sb.Append(",\"maxRows\":").Append(maxRows);
                sb.Append(",\"text\":").Append(JsonString(r.Text));
            }
            else
            {
                sb.Append(",\"error\":").Append(JsonString(r.Error));
                sb.Append(",\"describe\":").Append(JsonString(r.Describe));
            }
            sb.Append('}');
            return sb.ToString();
        }

        // JSON の文字列リテラル
        public static string JsonString(string s)
        {
            if (s == null) return "null";

            StringBuilder sb = new();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
