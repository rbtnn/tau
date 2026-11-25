using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // 実際のツールが書き出す PDF のうち、単純な構成ではないもの。
    //
    //  - オブジェクトストリーム(/Type /ObjStm) + xref ストリーム(/Type /XRef)
    //  - FlateDecode 以外のフィルタ(ASCII85, ASCIIHex, RunLength)
    //  - CID フォント(Identity-H) と ToUnicode
    //  - 暗号化
    [Collection("env")]
    public class PdfStructureTests : IDisposable
    {
        readonly string dir;

        public PdfStructureTests()
        {
            dir = Path.Combine(Path.GetTempPath(),
                "tau-pdfstruct-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        static byte[] Enc(string s) { return Encoding.Latin1.GetBytes(s); }

        static string Read(string path, out string error)
        {
            return PdfReader.ReadText(path, out error);
        }

        static string Zlib(byte[] raw)
        {
            using MemoryStream ms = new();
            using (ZLibStream z = new(ms, CompressionLevel.Optimal, true))
                z.Write(raw, 0, raw.Length);
            return Encoding.Latin1.GetString(ms.ToArray());
        }

        // 通常のオブジェクトを並べた PDF を書く。
        // objs[i] は「i+1 番」のオブジェクトの本体(辞書)。
        string WriteSimplePdf(List<byte[]> bodies, int rootNum, int size,
                              string extraTrailer = "")
        {
            MemoryStream ms = new();
            byte[] header = Enc("%PDF-1.4\n");
            ms.Write(header, 0, header.Length);

            List<int> offsets = new();
            for (int i = 0; i < bodies.Count; i++)
            {
                offsets.Add((int)ms.Length);
                byte[] o = Enc((i + 1) + " 0 obj\n");
                ms.Write(o, 0, o.Length);
                ms.Write(bodies[i], 0, bodies[i].Length);
                byte[] end = Enc("\nendobj\n");
                ms.Write(end, 0, end.Length);
            }

            int xref = (int)ms.Length;
            byte[] x = Enc("xref\n0 " + (bodies.Count + 1) + "\n0000000000 65535 f \n");
            ms.Write(x, 0, x.Length);
            foreach (int off in offsets)
            {
                byte[] line = Enc(off.ToString("D10") + " 00000 n \n");
                ms.Write(line, 0, line.Length);
            }

            string trailer = "trailer\n<< /Size " + (bodies.Count + 1)
                + " /Root " + rootNum + " 0 R " + extraTrailer + " >>\nstartxref\n"
                + xref + "\n%%EOF\n";
            byte[] t = Enc(trailer);
            ms.Write(t, 0, t.Length);

            string path = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        // ---------------- オブジェクトストリーム + xref ストリーム ----------------

        [Fact]
        public void ObjectStreamAndXrefStreamAreRead()
        {
            // PDF 1.5 以降の標準形。Word などが書き出す。
            List<byte[]> objs = new();

            // 1: Catalog, 2: Pages, 3: Page, 5: Font を ObjStm に入れる
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td (Hello from ObjStm) Tj ET");
            string contentZ = Zlib(content);

            List<KeyValuePair<int, string>> inner = new()
            {
                new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                       + "/Resources << /Font << /F1 5 0 R >> >> >>"),
                new(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            };

            StringBuilder header = new();
            StringBuilder body = new();
            foreach (KeyValuePair<int, string> kv in inner)
            {
                header.Append(kv.Key).Append(' ').Append(body.Length).Append(' ');
                body.Append(kv.Value).Append(' ');
            }
            string objStmZ = Zlib(Enc(header.ToString() + body.ToString()));

            MemoryStream ms = new();
            ms.Write(Enc("%PDF-1.5\n"), 0, Enc("%PDF-1.5\n").Length);

            int off4 = (int)ms.Length;
            string o4 = "4 0 obj\n<< /Length " + contentZ.Length
                + " /Filter /FlateDecode >>\nstream\n" + contentZ + "\nendstream\nendobj\n";
            ms.Write(Enc(o4), 0, Enc(o4).Length);

            int off6 = (int)ms.Length;
            string o6 = "6 0 obj\n<< /Type /ObjStm /N " + inner.Count
                + " /First " + header.Length + " /Length " + objStmZ.Length
                + " /Filter /FlateDecode >>\nstream\n" + objStmZ + "\nendstream\nendobj\n";
            ms.Write(Enc(o6), 0, Enc(o6).Length);

            // xref ストリーム(7)。エントリは W [1 4 2]
            int off7 = (int)ms.Length;
            byte[] entries = XrefEntries(off4, off6, off7, inner.Count);
            string xrefZ = Zlib(entries);
            string o7 = "7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Root 1 0 R /Length "
                + xrefZ.Length + " /Filter /FlateDecode >>\nstream\n" + xrefZ
                + "\nendstream\nendobj\n";
            ms.Write(Enc(o7), 0, Enc(o7).Length);

            string tail = "startxref\n" + off7 + "\n%%EOF\n";
            ms.Write(Enc(tail), 0, Enc(tail).Length);

            string path = Path.Combine(dir, "objstm.pdf");
            File.WriteAllBytes(path, ms.ToArray());

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Equal("Hello from ObjStm", text);
            Assert.Equal(1, PdfReader.CountPages(path));
        }

        // xref ストリームのエントリを作る。
        // 0=free, 1/2/3/5=ObjStm(6) 内, 4/6/7=通常オブジェクト
        static byte[] XrefEntries(int off4, int off6, int off7, int innerCount)
        {
            List<byte> b = new();
            // 0
            b.Add(0); AddU32(b, 0); AddU16(b, 65535);
            for (int i = 0; i < innerCount; i++)
            {
                b.Add(2); AddU32(b, 6); AddU16(b, i);
            }
            b.Add(1); AddU32(b, off4); AddU16(b, 0);
            b.Add(1); AddU32(b, off6); AddU16(b, 0);
            b.Add(1); AddU32(b, off7); AddU16(b, 0);
            return b.ToArray();
        }

        static void AddU32(List<byte> b, int v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16));
            b.Add((byte)(v >> 8)); b.Add((byte)v);
        }

        static void AddU16(List<byte> b, int v)
        {
            b.Add((byte)(v >> 8)); b.Add((byte)v);
        }

        // ---------------- フィルタ ----------------

        [Fact]
        public void Ascii85FilterIsDecoded()
        {
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td (ASCII85 text) Tj ET");
            string a85 = Ascii85Encode(content);

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                Enc("<< /Length " + a85.Length + " /Filter /ASCII85Decode >>\nstream\n"
                    + a85 + "\nendstream"),
            }, 1, 5);

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Equal("ASCII85 text", text);
        }

        [Fact]
        public void ChainedFiltersAreAppliedInOrder()
        {
            // ASCII85 → FlateDecode の順に適用される
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td (chained filters) Tj ET");
            string flate = Zlib(content);
            string a85 = Ascii85Encode(Enc(flate));

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                Enc("<< /Length " + a85.Length
                    + " /Filter [/ASCII85Decode /FlateDecode] >>\nstream\n" + a85 + "\nendstream"),
            }, 1, 5);

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Equal("chained filters", text);
        }

        [Fact]
        public void AsciiHexFilterIsDecoded()
        {
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td (hex text) Tj ET");
            StringBuilder hex = new();
            foreach (byte b in content) hex.Append(b.ToString("X2"));

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                Enc("<< /Length " + (hex.Length + 1) + " /Filter /ASCIIHexDecode >>\nstream\n"
                    + hex + ">\nendstream"),
            }, 1, 5);

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Equal("hex text", text);
        }

        static string Ascii85Encode(byte[] data)
        {
            StringBuilder sb = new();
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(4, data.Length - i);
                uint tuple = 0;
                for (int k = 0; k < 4; k++)
                    tuple = (tuple << 8) | (uint)(k < n ? data[i + k] : 0);

                if (n == 4 && tuple == 0) { sb.Append('z'); i += 4; continue; }

                char[] enc = new char[5];
                for (int k = 4; k >= 0; k--)
                {
                    enc[k] = (char)('!' + (tuple % 85));
                    tuple /= 85;
                }
                // 端数は必要な分だけ書く
                sb.Append(enc, 0, n + 1);
                i += n;
            }
            return sb.ToString();
        }

        // ---------------- CID フォント + ToUnicode ----------------

        [Fact]
        public void CidFontWithToUnicodeIsDecoded()
        {
            string cmap =
                "/CIDInit /ProcSet findresource begin\n"
                + "12 dict begin\nbegincmap\n"
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
                + "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n"
                + "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
                + "3 beginbfchar\n"
                + "<0001> <65E5>\n"     // 日
                + "<0002> <672C>\n"     // 本
                + "<0003> <8A9E>\n"     // 語
                + "endbfchar\n"
                + "1 beginbfrange\n<0010> <0013> <0041>\nendbfrange\n"   // A..D
                + "endcmap\n"
                + "CMapName currentdict /CMap defineresource pop\nend\nend\n";

            string cmapZ = Zlib(Enc(cmap));
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td <000100020003> Tj "
                + "0 -18 Td <0010001100120013> Tj ET");
            string contentZ = Zlib(content);

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                    + "/Resources << /Font << /F1 5 0 R >> >> >>"),
                Enc("<< /Length " + contentZ.Length + " /Filter /FlateDecode >>\nstream\n"
                    + contentZ + "\nendstream"),
                Enc("<< /Type /Font /Subtype /Type0 /BaseFont /MSGothic "
                    + "/Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>"),
                Enc("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MSGothic "
                    + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> >>"),
                Enc("<< /Length " + cmapZ.Length + " /Filter /FlateDecode >>\nstream\n"
                    + cmapZ + "\nendstream"),
            }, 1, 8);

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Contains("日本語", text);
            Assert.Contains("ABCD", text);
        }

        [Fact]
        public void CidFontWithoutToUnicodeExplainsTheProblem()
        {
            // ToUnicode が無いので、グリフ番号しか取れない
            byte[] content = Enc("BT /F1 12 Tf 72 720 Td <000100020003> Tj ET");
            string contentZ = Zlib(content);

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                    + "/Resources << /Font << /F1 5 0 R >> >> >>"),
                Enc("<< /Length " + contentZ.Length + " /Filter /FlateDecode >>\nstream\n"
                    + contentZ + "\nendstream"),
                Enc("<< /Type /Font /Subtype /Type0 /BaseFont /MSGothic "
                    + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>"),
                Enc("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MSGothic >>"),
            }, 1, 7);

            string text = Read(path, out string error);
            Assert.Null(text);
            Assert.Contains("ToUnicode", error);
        }

        // ---------------- 暗号化 ----------------

        [Fact]
        public void EncryptedPdfIsReportedAsEncrypted()
        {
            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                Enc("<< /Length 10 >>\nstream\nencrypted!\nendstream"),
                Enc("<< /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >>"),
            }, 1, 6, "/Encrypt 5 0 R /ID [<00><00>] ");

            string text = Read(path, out string error);
            Assert.Null(text);
            Assert.Contains("encrypted", error);
            Assert.Contains("password", error);
        }

        // ---------------- 複数ページ ----------------

        [Fact]
        public void ResourcesInheritedFromThePagesNodeAreUsed()
        {
            // フォントの定義を親(Pages)に置き、ページから参照する形
            string cmap =
                "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
                + "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
                + "1 beginbfchar\n<0001> <8A9E>\nendbfchar\nendcmap\nend\nend\n";
            string cmapZ = Zlib(Enc(cmap));

            byte[] content = Enc("BT /F1 12 Tf 72 720 Td <0001> Tj ET");
            string contentZ = Zlib(content);

            string path = WriteSimplePdf(new List<byte[]>
            {
                Enc("<< /Type /Catalog /Pages 2 0 R >>"),
                // 親に Resources を置く
                Enc("<< /Type /Pages /Kids [3 0 R] /Count 1 "
                    + "/Resources << /Font << /F1 5 0 R >> >> >>"),
                // ページには Resources を書かない
                Enc("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                Enc("<< /Length " + contentZ.Length + " /Filter /FlateDecode >>\nstream\n"
                    + contentZ + "\nendstream"),
                Enc("<< /Type /Font /Subtype /Type0 /BaseFont /MSGothic "
                    + "/Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>"),
                Enc("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MSGothic >>"),
                Enc("<< /Length " + cmapZ.Length + " /Filter /FlateDecode >>\nstream\n"
                    + cmapZ + "\nendstream"),
            }, 1, 8);

            string text = Read(path, out string error);
            Assert.Null(error);
            Assert.Equal("語", text);
        }
    }
}
