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
using Tau;

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

            DocumentJson.Result r = new();
            r.Text = ExcelReader.ReadText(path, sheet, maxRows, out string error);
            if (r.Text == null)
            {
                r.Error = error ?? "could not read the Excel workbook";
                r.Describe = ExcelReader.Describe(path);
            }

            if (json)
            {
                Console.Out.Write(DocumentJson.ToJson("excel", r, maxRows));
                Console.Out.Write('\n');
                return r.Text != null ? 0 : 1;
            }

            if (r.Text == null)
            {
                Console.Error.WriteLine(r.Error);
                return 1;
            }

            Console.Out.Write(r.Text);
            Console.Out.Write('\n');
            return 0;
        }
    }
}
