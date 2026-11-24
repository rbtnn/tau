using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 大きな文書を読んだときの、コンテキストサイズと切り詰めの検証。
    [Collection("env")]
    public class DocumentSizeTests : IDisposable
    {
        readonly string dir;

        public DocumentSizeTests()
        {
            dir = Path.Combine(Path.GetTempPath(),
                "tau-docsize-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        static Dictionary<string, object> Run(string path, string extra = "")
        {
            Dictionary<string, object> args = new() { ["path"] = path };
            if (extra.Length > 0)
            {
                Dictionary<string, object> more = Json.Parse(extra) as Dictionary<string, object>;
                foreach (KeyValuePair<string, object> kv in more) args[kv.Key] = kv.Value;
            }
            string json = ToolExecutor.Execute("read_document", Json.ToString(args), new Config(""));
            return Json.Parse(json) as Dictionary<string, object>;
        }

        static void Add(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name);
            using Stream s = e.Open();
            byte[] b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }

        // 大きな Excel を作る。1 セルに長い日本語を入れる。
        string MakeLargeExcel(int rows, int charsPerCell)
        {
            string path = Path.Combine(dir, "large.xlsx");
            using FileStream fs = new(path, FileMode.Create);
            using ZipArchive zip = new(fs, ZipArchiveMode.Create);

            string cell = new('あ', charsPerCell);
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"データ\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

            StringBuilder rowsXml = new();
            for (int i = 1; i <= rows; i++)
            {
                rowsXml.Append("<row r=\"" + i + "\">");
                rowsXml.Append("<c r=\"A" + i + "\" t=\"inlineStr\"><is><t>" + cell + "</t></is></c>");
                rowsXml.Append("<c r=\"B" + i + "\" t=\"inlineStr\"><is><t>"
                    + "A-" + i.ToString("D5") + "</t></is></c>");
                rowsXml.Append("</row>");
            }

            Add(zip, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<sheetData>" + rowsXml + "</sheetData></worksheet>");
            return path;
        }

        // 大きな PDF を作る。1 ページに長い日本語を入れる。
        string MakeLargePdf(int pages, int charsPerPage)
        {
            List<byte[]> objs = new();
            objs.Add(Enc("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));

            // ページツリー
            StringBuilder kids = new();
            for (int i = 0; i < pages; i++)
            {
                int pageNum = 4 + i * 2;
                kids.Append(pageNum).Append(" 0 R ");
            }
            objs.Add(Enc("2 0 obj\n<< /Type /Pages /Kids [" + kids + "] /Count "
                + pages + " >>\nendobj\n"));
            objs.Add(Enc("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));

            for (int i = 0; i < pages; i++)
            {
                int pageNum = 4 + i * 2;
                int contentNum = pageNum + 1;
                objs.Add(Enc(pageNum + " 0 obj\n<< /Type /Page /Parent 2 0 R "
                    + "/Contents " + contentNum + " 0 R >>\nendobj\n"));

                // UTF-16BE の文字列を作る
                StringBuilder hex = new();
                hex.Append("FEFF");
                for (int k = 0; k < charsPerPage; k++)
                    hex.Append("3042");   // あ

                byte[] stream = Enc("BT /F1 9 Tf 20 750 Td <" + hex + "> Tj ET");
                byte[] head = Enc(contentNum + " 0 obj\n<< /Length " + stream.Length
                    + " >>\nstream\n");
                byte[] tail = Enc("\nendstream\nendobj\n");
                byte[] o = new byte[head.Length + stream.Length + tail.Length];
                Buffer.BlockCopy(head, 0, o, 0, head.Length);
                Buffer.BlockCopy(stream, 0, o, head.Length, stream.Length);
                Buffer.BlockCopy(tail, 0, o, head.Length + stream.Length, tail.Length);
                objs.Add(o);
            }

            MemoryStream ms = new();
            Write(ms, "%PDF-1.4\n");
            List<int> offsets = new();
            foreach (byte[] o in objs) { offsets.Add((int)ms.Length); ms.Write(o, 0, o.Length); }
            int xref = (int)ms.Length;
            Write(ms, "xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
            foreach (int off in offsets) Write(ms, off.ToString("D10") + " 00000 n \n");
            Write(ms, "trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\nstartxref\n"
                + xref + "\n%%EOF\n");

            string path = Path.Combine(dir, "large.pdf");
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        static byte[] Enc(string s) { return Encoding.Latin1.GetBytes(s); }
        static void Write(Stream s, string s2) { byte[] b = Enc(s2); s.Write(b, 0, b.Length); }

        // ---------------- 分割して返す ----------------

        [Fact]
        public void LargeExcelIsReturnedInChunks()
        {
            string path = MakeLargeExcel(50, 2000);   // 約 100,000 文字
            Dictionary<string, object> d = Run(path);

            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            string content = Json.AsString(d["content"]);

            // 1 回では全部返さない
            Assert.True(content.Length <= ToolExecutor.ChunkSizeForTests,
                "content should be a chunk, got " + content.Length);
            Assert.Equal("true", Json.AsString(d["truncated"]));

            // 元の長さと、続きの位置が分かる
            int total = Json.AsInt(d["length"]);
            Assert.True(total > 100000, "the original length should be reported");
            int next = Json.AsInt(d["nextOffset"]);
            Assert.Equal(content.Length, next);
            Assert.Contains("offset=", Json.AsString(d["hint"]));
        }

        [Fact]
        public void ChunksCoverTheWholeDocumentWithoutLoss()
        {
            string path = MakeLargeExcel(20, 500);   // 約 10,000 文字
            string whole = Json.AsString(Run(path, "{\"limit\":100000}")["content"]);

            StringBuilder joined = new();
            int offset = 0;
            int guard = 0;
            while (true)
            {
                Dictionary<string, object> d = Run(path,
                    "{\"offset\":" + offset + ",\"limit\":3000}");
                joined.Append(Json.AsString(d["content"]));
                if (Json.AsString(d["truncated"]) != "true") break;
                offset = Json.AsInt(d["nextOffset"]);
                Assert.True(++guard < 100, "chunk loop did not finish");
            }

            Assert.Equal(whole, joined.ToString());
        }

        [Fact]
        public void OffsetBeyondTheEndReturnsNothing()
        {
            string path = MakeLargeExcel(2, 10);
            Dictionary<string, object> d = Run(path, "{\"offset\":999999}");

            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            Assert.Equal("", Json.AsString(d["content"]));
            Assert.Equal("false", Json.AsString(d["truncated"]));
        }

        [Fact]
        public void LargePdfIsReturnedInChunks()
        {
            string path = MakeLargePdf(20, 1000);   // 約 20,000 文字
            Dictionary<string, object> d = Run(path);

            Assert.True(d.ContainsKey("ok"), Json.ToString(d));
            Assert.Equal(20, Json.AsInt(d["pages"]));

            string content = Json.AsString(d["content"]);
            Assert.True(content.Length <= ToolExecutor.ChunkSizeForTests,
                "content should be a chunk, got " + content.Length);
        }

        [Fact]
        public void OneChunkFitsTheDefaultContext()
        {
            // 既定の contextWindow=64000, reserveTokens=12000 → 閾値 52000。
            // Agent の TokensOf は JSON の文字数の半分で概算する。
            string path = MakeLargeExcel(50, 2000);
            Dictionary<string, object> d = Run(path);

            string json = Json.ToString(d);
            int tokens = json.Length / 2 + 1;

            Config cfg = new("");
            long threshold = cfg.ContextWindow - cfg.ReserveTokens;
            Assert.True(tokens < threshold,
                "a single chunk must fit the context threshold: "
                + tokens + " tokens >= " + threshold);
        }

        [Fact]
        public void LargeExcelCanBeReadInChunksByRows()
        {
            // maxRows を小さくすれば、行単位で読める。
            string path = MakeLargeExcel(50, 2000);

            Dictionary<string, object> first = Run(path, "{\"maxRows\":2}");
            Assert.Contains("more rows omitted", Json.AsString(first["content"]));

            Dictionary<string, object> small = Run(path, "{\"maxRows\":5}");
            Assert.Contains("A-00001", Json.AsString(small["content"]));
            Assert.Contains("A-00005", Json.AsString(small["content"]));
            Assert.DoesNotContain("A-00006", Json.AsString(small["content"]));
        }

        // 文字数からトークン数を概算する(日本語 1 文字 ≒ 1〜2、英数 4 文字 ≒ 1)
        static int EstimateTokens(string s)
        {
            int tokens = 0;
            foreach (char c in s)
            {
                if (c < 0x80) tokens += 1;
                else tokens += 2;
            }
            return tokens / 2;
        }
    }
}
