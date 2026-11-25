// ---------------------------------------------------------------------------
//  DocumentText - PDF / Word / Excel からテキストを取り出す
//
//  以前は pdf2text.exe / word2text.exe / excel2text.exe を子プロセスで
//  起動していた。4 つの exe がそれぞれ .NET ランタイムを抱えると
//  合計 150MB を超えるため、tau.exe から直接呼ぶ形にした。
//
//  スタンドアロンの exe も同じ処理を使う(ソースは共通)。
// ---------------------------------------------------------------------------
using System;

namespace Tau
{
    static class DocumentText
    {
        // kind は "pdf" / "word" / "excel"。
        public static DocumentJson.Result Read(string kind, string path, string sheet, int maxRows)
        {
            switch (kind)
            {
                case "pdf": return ReadPdf(path);
                case "word": return ReadWord(path);
                case "excel": return ReadExcel(path, sheet, maxRows);
                default:
                    return new DocumentJson.Result { Error = "unsupported file type: " + kind };
            }
        }

        static DocumentJson.Result ReadPdf(string path)
        {
            DocumentJson.Result r = new();

            string text = Pdf2Text.PdfReader.ReadText(path, out string error);
            r.Pages = Pdf2Text.PdfReader.CountPages(path);

            if (text != null)
            {
                r.Text = text;
                return r;
            }

            r.Error = error ?? "could not read the PDF";
            r.Describe = Pdf2Text.PdfReader.Describe(path);
            return r;
        }

        static DocumentJson.Result ReadWord(string path)
        {
            DocumentJson.Result r = new();

            string text = Word2Text.WordReader.ReadText(path, out string error);
            if (text != null)
            {
                r.Text = text;
                return r;
            }

            r.Error = error ?? "could not read the Word document";
            r.Describe = Word2Text.WordReader.Describe(path);
            return r;
        }

        static DocumentJson.Result ReadExcel(string path, string sheet, int maxRows)
        {
            DocumentJson.Result r = new();

            string text = Excel2Text.ExcelReader.ReadText(path, sheet, maxRows, out string error);
            if (text != null)
            {
                r.Text = text;
                return r;
            }

            r.Error = error ?? "could not read the Excel workbook";
            r.Describe = Excel2Text.ExcelReader.Describe(path);
            return r;
        }
    }
}
