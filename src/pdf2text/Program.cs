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
//
//  tau.exe はこの exe を起動せず、同じパーサを直接呼ぶ(ランタイムの
//  二重持ちを避けるため)。この exe は単体で使いたい人向け。
// ---------------------------------------------------------------------------
using System;
using System.Text;
using Tau;

namespace Pdf2Text
{
    static class Program
    {
        static int Main(string[] args)
        {
            // 出力は UTF-8 に固定する
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
                Console.WriteLine(PdfReader.Describe(path));
                return 0;
            }

            DocumentJson.Result r = new();
            r.Text = PdfReader.ReadText(path, out string error);
            r.Pages = PdfReader.CountPages(path);
            if (r.Text == null)
            {
                r.Error = error ?? "could not read the PDF";
                r.Describe = PdfReader.Describe(path);
            }

            if (json)
            {
                Console.Out.Write(DocumentJson.ToJson("pdf", r, 0));
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
