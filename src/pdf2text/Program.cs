// ---------------------------------------------------------------------------
//  pdf2text - PDF からテキストを取り出す (tau の補助ツール)
//
//  使い方:
//    pdf2text <file.pdf>            テキストを標準出力へ
//    pdf2text --json <file.pdf>     JSON で結果を返す(tau が使う)
//    pdf2text --describe <file.pdf> 構造だけを調べる(原因の切り分け)
//
//  終了コード:
//    0  成功
//    1  読めなかった(--json では error を含む JSON を返す)
//    2  使い方の誤り
// ---------------------------------------------------------------------------
using System;
using System.Text;

namespace Pdf2Text
{
    static class Program
    {
        static int Main(string[] args)
        {
            // 出力は UTF-8 に固定する(tau が UTF-8 で読む)
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            bool json = false;
            bool describe = false;
            string path = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--json") json = true;
                else if (a == "--describe") describe = true;
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("usage: pdf2text [--json|--describe] <file.pdf>");
                    return 0;
                }
                else if (path == null) path = a;
                else
                {
                    Console.Error.WriteLine("too many arguments: " + a);
                    return 2;
                }
            }

            if (path == null)
            {
                Console.Error.WriteLine("usage: pdf2text [--json|--describe] <file.pdf>");
                return 2;
            }

            if (describe)
            {
                string info = PdfReader.Describe(path);
                Console.WriteLine(info);
                return 0;
            }

            string text = PdfReader.ReadText(path, out string error);

            if (json)
            {
                // tau が読む JSON。キーは手書きで十分(数が少ない)。
                StringBuilder sb = new();
                sb.Append("{\"ok\":").Append(text != null ? "true" : "false");
                sb.Append(",\"pages\":").Append(PdfReader.CountPages(path));

                if (text != null)
                {
                    sb.Append(",\"length\":").Append(text.Length);
                    sb.Append(",\"text\":").Append(JsonString(text));
                }
                else
                {
                    sb.Append(",\"error\":").Append(JsonString(error ?? "could not read the PDF"));
                    sb.Append(",\"describe\":").Append(JsonString(PdfReader.Describe(path)));
                }
                sb.Append('}');

                Console.Out.Write(sb.ToString());
                Console.Out.Write('\n');
                return text != null ? 0 : 1;
            }

            if (text == null)
            {
                Console.Error.WriteLine(error ?? "could not read the PDF");
                return 1;
            }

            Console.Out.Write(text);
            Console.Out.Write('\n');
            return 0;
        }

        // JSON の文字列リテラル
        static string JsonString(string s)
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
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
