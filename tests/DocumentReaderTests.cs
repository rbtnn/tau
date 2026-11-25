using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // PDF / Word / Excel を、COM を使わずに読むツールの検証。
    [Collection("env")]
    public class DocumentReaderTests : IDisposable
    {
        readonly string dir;
        readonly TauRunner runner;

        public DocumentReaderTests()
        {
            // read_document は pdf2text などを別プロセスで呼ぶ。
            // TauRunner が TAU_HELPERS_DIR を設定する。
            runner = new TauRunner();

            dir = Path.Combine(Path.GetTempPath(),
                "tau-doc-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            runner.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }

        string Run(string path, string extra = "")
        {
            Dictionary<string, object> args = new() { ["path"] = path };
            if (extra.Length > 0)
            {
                Dictionary<string, object> more =
                    Json.Parse(extra) as Dictionary<string, object>;
                foreach (KeyValuePair<string, object> kv in more) args[kv.Key] = kv.Value;
            }
            return ToolExecutor.Execute("read_document", Json.ToString(args), new Config(""));
        }

        static Dictionary<string, object> Result(string json)
        {
            return Json.Parse(json) as Dictionary<string, object>;
        }

        // ---------------- 補助ツール無しで動く ----------------

        [Fact]
        public void ReadDocumentWorksWithoutHelperExecutables()
        {
            // tau は PDF / Word / Excel のパーサを直接呼ぶので、
            // pdf2text.exe などが無くても読める。
            string path = MakePdf("no helpers needed");

            string saved = Environment.GetEnvironmentVariable("TAU_HELPERS_DIR");
            try
            {
                Environment.SetEnvironmentVariable("TAU_HELPERS_DIR",
                    Path.Combine(dir, "does-not-exist"));

                Dictionary<string, object> d =
                    Result(ToolExecutor.Execute("read_document",
                        Json.ToString(new Dictionary<string, object> { ["path"] = path }),
                        new Config("")));

                Assert.True(d.ContainsKey("content"), Json.ToString(d));
                Assert.Contains("no helpers needed", Json.AsString(d["content"]));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TAU_HELPERS_DIR", saved);
            }
        }

        [Fact]
        public void ReadDocumentReadsWordAndExcelInProcess()
        {
            // Word / Excel も同じ経路で読む。
            Dictionary<string, object> w = Result(ToolExecutor.Execute("read_document",
                Json.ToString(new Dictionary<string, object> { ["path"] = MakeDocx() }),
                new Config("")));
            Assert.Contains("テスト", Json.AsString(w["content"]));

            Dictionary<string, object> x = Result(ToolExecutor.Execute("read_document",
                Json.ToString(new Dictionary<string, object> { ["path"] = MakeXlsx() }),
                new Config("")));
            Assert.Contains("りんご", Json.AsString(x["content"]));
        }

        // ---------------- サンプルの生成 ----------------

        // 最小構成の PDF を作る。テキストは WinAnsi の ASCII と UTF-16BE の両方。
        string MakePdf(string text)
        {
            List<byte[]> objs = new();

            objs.Add(Enc("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
            objs.Add(Enc("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));
            objs.Add(Enc("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n"));

            byte[] stream = Enc("BT /F1 12 Tf 72 720 Td (" + text + ") Tj ET");
            byte[] head = Enc("4 0 obj\n<< /Length " + stream.Length + " >>\nstream\n");
            byte[] tail = Enc("\nendstream\nendobj\n");
            byte[] content = new byte[head.Length + stream.Length + tail.Length];
            Buffer.BlockCopy(head, 0, content, 0, head.Length);
            Buffer.BlockCopy(stream, 0, content, head.Length, stream.Length);
            Buffer.BlockCopy(tail, 0, content, head.Length + stream.Length, tail.Length);
            objs.Add(content);

            objs.Add(Enc("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));

            MemoryStream ms = new();
            Write(ms, "%PDF-1.4\n");
            List<int> offsets = new();
            foreach (byte[] o in objs)
            {
                offsets.Add((int)ms.Length);
                ms.Write(o, 0, o.Length);
            }
            int xref = (int)ms.Length;
            Write(ms, "xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
            foreach (int off in offsets)
                Write(ms, off.ToString("D10") + " 00000 n \n");
            Write(ms, "trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\n"
                + "startxref\n" + xref + "\n%%EOF\n");

            string path = Path.Combine(dir, "sample.pdf");
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        static byte[] Enc(string s) { return Encoding.Latin1.GetBytes(s); }
        static void Write(Stream s, string s2) { byte[] b = Enc(s2); s.Write(b, 0, b.Length); }

        string MakeDocx()
        {
            string path = Path.Combine(dir, "sample.docx");
            using FileStream fs = new(path, FileMode.Create);
            using ZipArchive zip = new(fs, ZipArchiveMode.Create);

            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
            Add(zip, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + "<w:body>"
                + "<w:p><w:r><w:t>見出し1</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>これは</w:t></w:r><w:r><w:t>テスト</w:t></w:r>"
                + "<w:r><w:tab/><w:t>です。</w:t></w:r></w:p>"
                + "<w:tbl>"
                + "<w:tr><w:tc><w:p><w:r><w:t>セルA1</w:t></w:r></w:p></w:tc>"
                + "<w:tc><w:p><w:r><w:t>セルB1</w:t></w:r></w:p></w:tc></w:tr>"
                + "<w:tr><w:tc><w:p><w:r><w:t>セルA2</w:t></w:r></w:p></w:tc>"
                + "<w:tc><w:p><w:r><w:t>セルB2</w:t></w:r></w:p></w:tc></w:tr>"
                + "</w:tbl>"
                + "</w:body></w:document>");
            return path;
        }

        string MakeXlsx()
        {
            string path = Path.Combine(dir, "sample.xlsx");
            using FileStream fs = new(path, FileMode.Create);
            using ZipArchive zip = new(fs, ZipArchiveMode.Create);

            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
            Add(zip, "xl/sharedStrings.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"4\" uniqueCount=\"4\">"
                + "<si><t>名前</t></si><si><t>数量</t></si><si><t>りんご</t></si><si><t>備考</t></si>"
                + "</sst>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"売上\" sheetId=\"1\" r:id=\"rId1\"/>"
                + "<sheet name=\"在庫\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>"
                + "</Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
                + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>3</v></c>"
                + "<c r=\"C2\" t=\"s\"><v>3</v></c></row>"
                + "</sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet2.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>在庫あり</t></is></c></row>"
                + "</sheetData></worksheet>");
            return path;
        }

        static void Add(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name);
            using Stream s = e.Open();
            byte[] b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }

        // ---------------- PDF ----------------

        [Fact]
        public void PdfTextIsExtracted()
        {
            string path = MakePdf("Hello PDF");
            Dictionary<string, object> d = Result(Run(path));

            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            Assert.Equal("pdf", Json.AsString(d["type"]));
            Assert.Equal(1, Json.AsInt(d["pages"]));
            Assert.Contains("Hello PDF", Json.AsString(d["content"]));
        }

        [Fact]
        public void PdfWithoutTextExplainsOcrIsNeeded()
        {
            // テキスト演算子を持たない内容ストリーム
            List<byte[]> objs = new();
            objs.Add(Enc("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
            objs.Add(Enc("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));
            objs.Add(Enc("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n"));
            byte[] stream = Enc("0 0 100 100 re f");
            objs.Add(Enc("4 0 obj\n<< /Length " + stream.Length + " >>\nstream\n")
                .Concat(stream).Concat(Enc("\nendstream\nendobj\n")).ToArray());

            MemoryStream ms = new();
            Write(ms, "%PDF-1.4\n");
            List<int> offsets = new();
            foreach (byte[] o in objs) { offsets.Add((int)ms.Length); ms.Write(o, 0, o.Length); }
            int xref = (int)ms.Length;
            Write(ms, "xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
            foreach (int off in offsets) Write(ms, off.ToString("D10") + " 00000 n \n");
            Write(ms, "trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\nstartxref\n"
                + xref + "\n%%EOF\n");

            string path = Path.Combine(dir, "scan.pdf");
            File.WriteAllBytes(path, ms.ToArray());

            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("OCR", Json.AsString(d["error"]));
        }

        [Fact]
        public void CompressedPdfWithUtf16TextIsExtracted()
        {
            // FlateDecode 圧縮 + UTF-16BE の文字列 + 2 ページ
            byte[] c1 = Zlib(Enc("BT /F1 12 Tf 72 720 Td <FEFF3053304430463057> Tj ET"));
            byte[] c2 = Zlib(Enc("BT /F1 12 Tf 72 720 Td (Second page) Tj ET"));

            List<byte[]> objs = new();
            objs.Add(Enc("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
            objs.Add(Enc("2 0 obj\n<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>\nendobj\n"));
            objs.Add(Enc("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n"));
            objs.Add(Stream(4, c1, "/Filter /FlateDecode"));
            objs.Add(Enc("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));
            objs.Add(Enc("6 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>\nendobj\n"));
            objs.Add(Stream(7, c2, "/Filter /FlateDecode"));

            string path = WritePdf(objs, "flate.pdf");
            Dictionary<string, object> d = Result(Run(path));

            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            Assert.Equal(2, Json.AsInt(d["pages"]));
            string text = Json.AsString(d["content"]);
            Assert.Contains("こいうし", text);
            Assert.Contains("Second page", text);
        }

        static byte[] Zlib(byte[] raw)
        {
            using MemoryStream ms = new();
            using (ZLibStream z = new(ms, CompressionLevel.Optimal, true))
                z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        static byte[] Stream(int num, byte[] body, string extraDict)
        {
            byte[] head = Enc(num + " 0 obj\n<< /Length " + body.Length + " " + extraDict + " >>\nstream\n");
            byte[] tail = Enc("\nendstream\nendobj\n");
            byte[] r = new byte[head.Length + body.Length + tail.Length];
            Buffer.BlockCopy(head, 0, r, 0, head.Length);
            Buffer.BlockCopy(body, 0, r, head.Length, body.Length);
            Buffer.BlockCopy(tail, 0, r, head.Length + body.Length, tail.Length);
            return r;
        }

        string WritePdf(List<byte[]> objs, string name)
        {
            MemoryStream ms = new();
            Write(ms, "%PDF-1.4\n");
            List<int> offsets = new();
            foreach (byte[] o in objs) { offsets.Add((int)ms.Length); ms.Write(o, 0, o.Length); }
            int xref = (int)ms.Length;
            Write(ms, "xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
            foreach (int off in offsets) Write(ms, off.ToString("D10") + " 00000 n \n");
            Write(ms, "trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\nstartxref\n"
                + xref + "\n%%EOF\n");
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        [Fact]
        public void NonPdfFileIsRejected()
        {
            string path = Path.Combine(dir, "fake.pdf");
            File.WriteAllText(path, "this is not a PDF");
            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("error"));
        }

        // ---------------- Word ----------------

        [Fact]
        public void WordTextAndTableAreExtracted()
        {
            string path = MakeDocx();
            Dictionary<string, object> d = Result(Run(path));

            Assert.Equal("true", Json.AsString(d["ok"]));
            Assert.Equal("word", Json.AsString(d["type"]));

            string text = Json.AsString(d["content"]);
            Assert.Contains("見出し1", text);
            // 同じ段落の連続した w:r は連結される
            Assert.Contains("これはテスト", text);
            Assert.Contains("です。", text);
            // 表はタブ区切りで 1 行ずつ
            Assert.Contains("セルA1\tセルB1", text);
            Assert.Contains("セルA2\tセルB2", text);
        }

        [Fact]
        public void WordWithoutDocumentXmlIsRejected()
        {
            string path = Path.Combine(dir, "broken.docx");
            using (FileStream fs = new(path, FileMode.Create))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
            }
            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("Word", Json.AsString(d["error"]));
        }

        // ---------------- Excel ----------------

        [Fact]
        public void ExcelSheetsAndCellsAreExtracted()
        {
            string path = MakeXlsx();
            Dictionary<string, object> d = Result(Run(path));

            Assert.Equal("true", Json.AsString(d["ok"]));
            Assert.Equal("excel", Json.AsString(d["type"]));

            string text = Json.AsString(d["content"]);
            Assert.Contains("## 売上", text);
            Assert.Contains("## 在庫", text);
            Assert.Contains("名前\t数量", text);
            Assert.Contains("りんご\t3\t備考", text);
            Assert.Contains("在庫あり", text);
        }

        [Fact]
        public void ExcelSheetCanBeSelected()
        {
            string path = MakeXlsx();
            Dictionary<string, object> d = Result(Run(path, "{\"sheet\":\"在庫\"}"));

            string text = Json.AsString(d["content"]);
            Assert.Contains("## 在庫", text);
            Assert.DoesNotContain("## 売上", text);
        }

        [Fact]
        public void UnknownSheetIsReported()
        {
            string path = MakeXlsx();
            Dictionary<string, object> d = Result(Run(path, "{\"sheet\":\"無い\"}"));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("無い", Json.AsString(d["error"]));
        }

        [Fact]
        public void ExcelMaxRowsStopsReading()
        {
            string path = Path.Combine(dir, "many.xlsx");
            using (FileStream fs = new(path, FileMode.Create))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                Add(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + "<sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

                StringBuilder rows = new();
                for (int i = 1; i <= 30; i++)
                    rows.Append("<row r=\"" + i + "\"><c r=\"A" + i + "\"><v>" + i + "</v></c></row>");

                Add(zip, "xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<sheetData>" + rows + "</sheetData></worksheet>");
            }

            Dictionary<string, object> d = Result(Run(path, "{\"maxRows\":5}"));
            string text = Json.AsString(d["content"]);
            Assert.Contains("more rows omitted", text);
            Assert.Contains("5", text);
            Assert.DoesNotContain("\n30\n", text);
        }

        // ---------------- 日本語と半角英数字の混在 ----------------

        [Fact]
        public void PdfKeepsMixedJapaneseAndAscii()
        {
            // WinAnsi の ASCII と UTF-16BE の日本語が同じ行にある
            string path = WritePdf(new List<byte[]>
            {
                Enc("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"),
                Enc("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"),
                Enc("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n"),
                Stream(4, Enc("BT /F1 12 Tf 72 720 Td (ABC123 ) Tj <FEFF65E5672C8A9E> Tj ( xyz789) Tj ET"),
                    ""),
                Enc("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"),
            }, "mixed.pdf");

            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            string text = Json.AsString(d["content"]);

            Assert.Contains("ABC123", text);
            Assert.Contains("日本語", text);
            Assert.Contains("xyz789", text);
            // 順序が保たれる
            Assert.True(text.IndexOf("ABC123") < text.IndexOf("日本語"),
                "the ASCII part must come first: " + text);
            Assert.True(text.IndexOf("日本語") < text.IndexOf("xyz789"),
                "the trailing ASCII part must come last: " + text);
        }

        [Fact]
        public void WordKeepsMixedJapaneseAndAscii()
        {
            string path = Path.Combine(dir, "mixed.docx");
            using (FileStream fs = new(path, FileMode.Create))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
                Add(zip, "word/document.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                    + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                    + "<w:body>"
                    + "<w:p><w:r><w:t>商品ID: A-1024 の在庫は 3 個です。</w:t></w:r></w:p>"
                    + "<w:p><w:r><w:t>価格は 1,980 JPY</w:t></w:r><w:r><w:t>（税込）</w:t></w:r></w:p>"
                    + "<w:tbl>"
                    + "<w:tr><w:tc><w:p><w:r><w:t>SKU</w:t></w:r></w:p></w:tc>"
                    + "<w:tc><w:p><w:r><w:t>商品名</w:t></w:r></w:p></w:tc></w:tr>"
                    + "<w:tr><w:tc><w:p><w:r><w:t>X-9</w:t></w:r></w:p></w:tc>"
                    + "<w:tc><w:p><w:r><w:t>りんご L サイズ</w:t></w:r></w:p></w:tc></w:tr>"
                    + "</w:tbl>"
                    + "</w:body></w:document>");
            }

            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            string text = Json.AsString(d["content"]);

            Assert.Contains("商品ID: A-1024 の在庫は 3 個です。", text);
            Assert.Contains("価格は 1,980 JPY（税込）", text);
            Assert.Contains("SKU\t商品名", text);
            Assert.Contains("X-9\tりんご L サイズ", text);
        }

        [Fact]
        public void ExcelKeepsMixedJapaneseAndAscii()
        {
            string path = Path.Combine(dir, "mixed.xlsx");
            using (FileStream fs = new(path, FileMode.Create))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                Add(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + "<sheets><sheet name=\"2026年度 売上\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                Add(zip, "xl/sharedStrings.xml",
                    "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<si><t>商品コード</t></si><si><t>商品名</t></si>"
                    + "<si><t>A-1024</t></si><si><t>りんご L サイズ</t></si>"
                    + "<si><t>税込 1,980 円</t></si></sst>");
                Add(zip, "xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + "<sheetData>"
                    + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
                    + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\" t=\"s\"><v>3</v></c>"
                    + "<c r=\"C2\" t=\"s\"><v>4</v></c></row>"
                    + "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>B-2048</t></is></c>"
                    + "<c r=\"B3\" t=\"inlineStr\"><is><t>みかん S サイズ</t></is></c></row>"
                    + "</sheetData></worksheet>");
            }

            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            string text = Json.AsString(d["content"]);

            // シート名にも日本語と半角数字が混在する
            Assert.Contains("## 2026年度 売上", text);
            Assert.Contains("商品コード\t商品名", text);
            Assert.Contains("A-1024\tりんご L サイズ\t税込 1,980 円", text);
            Assert.Contains("B-2048\tみかん S サイズ", text);
        }

        // ---------------- 形式の判定 ----------------

        [Fact]
        public void LegacyBinaryFormatsAreRejectedWithAdvice()
        {
            string path = Path.Combine(dir, "old.doc");
            File.WriteAllBytes(path, new byte[] { 0xD0, 0xCF, 0x11, 0xE0 });
            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("not supported", Json.AsString(d["error"]));
        }

        [Fact]
        public void UnsupportedExtensionIsRejected()
        {
            string path = Path.Combine(dir, "note.txt");
            File.WriteAllText(path, "plain");
            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("unsupported", Json.AsString(d["error"]));
        }

        [Fact]
        public void MissingFileIsReported()
        {
            Dictionary<string, object> d = Result(Run(Path.Combine(dir, "nope.pdf")));
            Assert.True(d.ContainsKey("error"));
            Assert.Contains("does not exist", Json.AsString(d["error"]));
        }

        [Fact]
        public void ReadDocumentIsRegisteredAsATool()
        {
            List<object> tools = ToolExecutor.Definitions();
            bool found = false;
            foreach (object o in tools)
            {
                Dictionary<string, object> t = o as Dictionary<string, object>;
                Dictionary<string, object> f = t["function"] as Dictionary<string, object>;
                if (Json.AsString(f["name"]) == "read_document") found = true;
            }
            Assert.True(found, "read_document must be offered to the LLM");
        }

        // ---------------- 実ファイルに近い構造 ----------------

        [Fact]
        public void RealisticDocxIsRead()
        {
            // Word が実際に書き出す構造に近いもの（styles, sectPr, 表）
            string path = Path.Combine(dir, "report.docx");
            using (FileStream fs = new(path, FileMode.Create))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
                Add(zip, "word/styles.xml", "<?xml version=\"1.0\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>");
                Add(zip, "word/document.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                    + "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>報告書</w:t></w:r></w:p>"
                    + "<w:p><w:r><w:t xml:space=\"preserve\">商品ID: A-1024 の在庫は 3 個です。</w:t></w:r></w:p>"
                    + "<w:p><w:r><w:t>価格は 1,980 JPY</w:t></w:r><w:r><w:t>（税込）</w:t></w:r></w:p>"
                    + "<w:tbl><w:tblPr/><w:tblGrid/>"
                    + "<w:tr><w:tc><w:p><w:r><w:t>SKU</w:t></w:r></w:p></w:tc>"
                    + "<w:tc><w:p><w:r><w:t>商品名</w:t></w:r></w:p></w:tc></w:tr>"
                    + "<w:tr><w:tc><w:p><w:r><w:t>X-9</w:t></w:r></w:p></w:tc>"
                    + "<w:tc><w:p><w:r><w:t>りんご L サイズ</w:t></w:r></w:p></w:tc></w:tr>"
                    + "</w:tbl><w:sectPr/></w:body></w:document>");
            }

            Dictionary<string, object> d = Result(Run(path));
            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            string text = Json.AsString(d["content"]);

            Assert.Contains("報告書", text);
            Assert.Contains("商品ID: A-1024 の在庫は 3 個です。", text);
            Assert.Contains("価格は 1,980 JPY（税込）", text);
            Assert.Contains("SKU\t商品名", text);
            Assert.Contains("X-9\tりんご L サイズ", text);
            // 表の後ろに余計な空行が残らない
            Assert.DoesNotContain("\n\n\n", text);
        }

        [Theory]
        [InlineData("read_pdf")]
        [InlineData("read_docx")]
        [InlineData("read_xlsx")]
        [InlineData("read_document")]
        public void AliasesMapToReadDocument(string name)
        {
            Assert.Equal("read_document", ToolExecutor.NormalizeNamePublic(name));
        }
    }
}
