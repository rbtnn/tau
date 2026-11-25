// ---------------------------------------------------------------------------
//  excel2text - Excel (.xlsx) からテキストを取り出す (tau の補助ツール)
//
//  使い方:
//    excel2text <file.xlsx>                         全シートを標準出力へ
//    excel2text --sheet <name> <file.xlsx>          指定シートだけ
//    excel2text --rows <n> <file.xlsx>              1 シートあたりの最大行
//    excel2text --json [--sheet <name>] <file.xlsx> JSON で結果を返す(tau が使う)
//    excel2text --describe <file.xlsx>              構造だけを調べる
//
//  終了コード:
//    0  成功
//    1  読めなかった(--json では error を含む JSON を返す)
//    2  使い方の誤り
// ---------------------------------------------------------------------------
using System;
using System.Text;

namespace Excel2Text
{
    static class Program
    {
        static int Main(string[] args)
        {
            // 出力は UTF-8 に固定する(tau が UTF-8 で読む)
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            bool json = false;
            bool describe = false;
            string sheet = null;
            int maxRows = 0;
            string path = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--json") json = true;
                else if (a == "--describe") describe = true;
                else if (a == "--sheet" && i + 1 < args.Length) sheet = args[++i];
                else if (a.StartsWith("--sheet=")) sheet = a.Substring("--sheet=".Length);
                else if (a == "--rows" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out maxRows);
                }
                else if (a.StartsWith("--rows="))
                {
                    int.TryParse(a.Substring("--rows=".Length), out maxRows);
                }
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("usage: excel2text [--json] [--sheet <name>] [--rows <n>] <file.xlsx>");
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
                Console.Error.WriteLine("usage: excel2text [--json] [--sheet <name>] [--rows <n>] <file.xlsx>");
                return 2;
            }

            if (describe)
            {
                Console.WriteLine(ExcelReader.Describe(path));
                return 0;
            }

            string text = ExcelReader.ReadText(path, sheet, maxRows, out string error);

            if (json)
            {
                StringBuilder sb = new();
                sb.Append("{\"ok\":").Append(text != null ? "true" : "false");

                if (text != null)
                {
                    sb.Append(",\"length\":").Append(text.Length);
                    if (maxRows > 0) sb.Append(",\"maxRows\":").Append(maxRows);
                    sb.Append(",\"text\":").Append(JsonString(text));
                }
                else
                {
                    sb.Append(",\"error\":").Append(JsonString(error ?? "could not read the Excel workbook"));
                    sb.Append(",\"describe\":").Append(JsonString(ExcelReader.Describe(path)));
                }
                sb.Append('}');

                Console.Out.Write(sb.ToString());
                Console.Out.Write('\n');
                return text != null ? 0 : 1;
            }

            if (text == null)
            {
                Console.Error.WriteLine(error ?? "could not read the Excel workbook");
                return 1;
            }

            Console.Out.Write(text);
            Console.Out.Write('\n');
            return 0;
        }

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
