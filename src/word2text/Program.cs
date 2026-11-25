// ---------------------------------------------------------------------------
//  word2text - Word (.docx) からテキストを取り出す (tau の補助ツール)
//
//  使い方:
//    word2text <file.docx>            テキストを標準出力へ
//    word2text --json <file.docx>     JSON で結果を返す(tau が使う)
//    word2text --describe <file.docx> 構造だけを調べる
//
//  終了コード:
//    0  成功
//    1  読めなかった(--json では error を含む JSON を返す)
//    2  使い方の誤り
// ---------------------------------------------------------------------------
using System;
using System.Text;
using Tau;

namespace Word2Text
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
                    Console.WriteLine("usage: word2text [--json|--describe] <file.docx>");
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
                Console.Error.WriteLine("usage: word2text [--json|--describe] <file.docx>");
                return 2;
            }

            if (describe)
            {
                Console.WriteLine(WordReader.Describe(path));
                return 0;
            }

            DocumentJson.Result r = new();
            r.Text = WordReader.ReadText(path, out string error);
            if (r.Text == null)
            {
                r.Error = error ?? "could not read the Word document";
                r.Describe = WordReader.Describe(path);
            }

            if (json)
            {
                Console.Out.Write(DocumentJson.ToJson("word", r, 0));
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
