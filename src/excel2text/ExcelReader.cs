// ---------------------------------------------------------------------------
//  excel2text - Excel (.xlsx) からテキストを取り出す (tau の補助ツール)
//
//  COM は使わず、Office 文書の実体である ZIP + XML を直接読む。
//  (Excel がインストールされていなくても動く)
//
//  tau の read_document は、この exe を子プロセスとして呼び出す。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Excel2Text
{
    static class ExcelReader
    {
        // ZIP 内のエントリを読む。無ければ null。
        static byte[] Entry(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using Stream s = e.Open();
            using MemoryStream ms = new();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        static string TextOf(byte[] bytes)
        {
            if (bytes == null) return null;
            using MemoryStream ms = new(bytes);
            using StreamReader r = new(ms, Encoding.UTF8, true);
            return r.ReadToEnd();
        }

        static XmlReaderSettings Settings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
            };
        }

        /// <summary>
        /// xlsx から各シートのセルを取り出す。
        /// sheet を指定するとそのシートだけ、null なら全シート。
        /// </summary>
        public static string ReadText(string path, string sheet, int maxRows, out string error)
        {
            error = null;
            try
            {
                using FileStream fs = File.OpenRead(path);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);

                // 共有文字列 (xl/sharedStrings.xml)。セルの t="s" はこの添字。
                List<string> shared = ReadSharedStrings(zip);

                // シート名 → XML のパス
                List<KeyValuePair<string, string>> sheets = ReadSheetList(zip);
                if (sheets.Count == 0)
                {
                    error = "not an Excel workbook (no worksheets found)";
                    return null;
                }

                if (maxRows <= 0) maxRows = 200;

                StringBuilder sb = new();
                bool any = false;

                foreach (KeyValuePair<string, string> kv in sheets)
                {
                    if (sheet != null && sheet.Trim().Length > 0
                        && !kv.Key.Equals(sheet.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    string xml = TextOf(Entry(zip, kv.Value));
                    if (xml == null) continue;

                    any = true;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("## ").Append(kv.Key).Append('\n');

                    int rows = AppendSheetRows(sb, xml, shared, maxRows);
                    if (rows == 0) sb.Append("(empty)\n");
                }

                if (!any)
                {
                    error = "no such sheet: " + sheet;
                    return null;
                }

                return Normalize(sb.ToString());
            }
            catch (Exception ex)
            {
                error = "could not read the Excel workbook: " + ex.Message;
                return null;
            }
        }

        /// <summary>シート名の一覧(診断・エラー表示用)。</summary>
        public static List<string> SheetNames(string path)
        {
            List<string> names = new();
            try
            {
                using FileStream fs = File.OpenRead(path);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                foreach (KeyValuePair<string, string> kv in ReadSheetList(zip))
                    names.Add(kv.Key);
            }
            catch { }
            return names;
        }

        public static string Describe(string path)
        {
            try
            {
                using FileStream fs = File.OpenRead(path);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                List<string> names = new();
                foreach (KeyValuePair<string, string> kv in ReadSheetList(zip)) names.Add(kv.Key);

                StringBuilder sb = new();
                sb.Append("size=").Append(fs.Length);
                sb.Append("  sheets=").Append(names.Count);
                sb.Append(" [").Append(string.Join(",", names)).Append(']');
                sb.Append("  sharedStrings=").Append(zip.GetEntry("xl/sharedStrings.xml") != null ? "yes" : "no");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "describe failed: " + ex.Message;
            }
        }

        static List<string> ReadSharedStrings(ZipArchive zip)
        {
            List<string> list = new();
            string xml = TextOf(Entry(zip, "xl/sharedStrings.xml"));
            if (xml == null) return list;

            using StringReader sr = new(xml);
            using XmlReader xr = XmlReader.Create(sr, Settings());
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "si") continue;

                // <si><t>text</t></si> または <si><r><t>..</t></r>..</si>
                StringBuilder sb = new();
                if (!xr.IsEmptyElement)
                {
                    int depth = xr.Depth;
                    while (xr.Read())
                    {
                        if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth) break;
                        if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                            sb.Append(ReadText(xr));
                    }
                }
                list.Add(sb.ToString());
            }
            return list;
        }

        static List<KeyValuePair<string, string>> ReadSheetList(ZipArchive zip)
        {
            List<KeyValuePair<string, string>> list = new();

            // シート名は xl/workbook.xml、実体のパスは rels で対応付ける。
            Dictionary<string, string> rels = new();
            string relXml = TextOf(Entry(zip, "xl/_rels/workbook.xml.rels"));
            if (relXml != null)
            {
                using StringReader sr = new(relXml);
                using XmlReader xr = XmlReader.Create(sr, Settings());
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "Relationship") continue;
                    string id = xr.GetAttribute("Id");
                    string target = xr.GetAttribute("Target");
                    if (id != null && target != null)
                    {
                        if (target.StartsWith("/")) target = target.TrimStart('/');
                        else if (!target.StartsWith("xl/")) target = "xl/" + target;
                        rels[id] = target;
                    }
                }
            }

            string wbXml = TextOf(Entry(zip, "xl/workbook.xml"));
            if (wbXml != null)
            {
                using StringReader sr = new(wbXml);
                using XmlReader xr = XmlReader.Create(sr, Settings());
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "sheet") continue;
                    string name = xr.GetAttribute("name");
                    string rid = null;
                    for (int i = 0; i < xr.AttributeCount; i++)
                    {
                        xr.MoveToAttribute(i);
                        if (xr.LocalName == "id") rid = xr.Value;
                    }
                    xr.MoveToElement();
                    if (name == null) continue;
                    string target = (rid != null && rels.ContainsKey(rid)) ? rels[rid] : null;
                    if (target != null) list.Add(new(name, target));
                }
            }

            // workbook.xml が読めなかった場合は、決まった名前を順に試す
            if (list.Count == 0)
            {
                for (int i = 1; i <= 64; i++)
                {
                    string p = "xl/worksheets/sheet" + i + ".xml";
                    if (zip.GetEntry(p) != null) list.Add(new("Sheet" + i, p));
                }
            }

            return list;
        }

        static int AppendSheetRows(StringBuilder sb, string xml, List<string> shared, int maxRows)
        {
            int count = 0;
            int lastRow = 0;

            using StringReader sr = new(xml);
            using XmlReader xr = XmlReader.Create(sr, Settings());
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "row") continue;

                int r = 1;
                string rAttr = xr.GetAttribute("r");
                if (rAttr != null && int.TryParse(rAttr, out int parsed)) r = parsed;

                // 空行を飛ばして行番号を保つ
                while (lastRow + 1 < r)
                {
                    lastRow++;
                    sb.Append("(empty row)\n");
                    count++;
                    if (count >= maxRows) return count;
                }
                lastRow = r;

                string line = RowText(xr, shared);
                sb.Append(line).Append('\n');
                count++;
                if (count >= maxRows)
                {
                    sb.Append("...[more rows omitted]\n");
                    return count;
                }
            }
            return count;
        }

        // <row> の内側を読み、列位置を保ったままタブ区切りの行を作る。
        static string RowText(XmlReader xr, List<string> shared)
        {
            SortedDictionary<int, string> cells = new();
            int maxCol = 0;

            if (xr.IsEmptyElement) return "";

            int rowDepth = xr.Depth;
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == rowDepth) break;
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "c") continue;

                string cellRef = xr.GetAttribute("r");
                string type = xr.GetAttribute("t");
                int col = ColumnIndex(cellRef);

                string value = CellValue(xr, type, shared);
                if (value.Length > 0)
                {
                    cells[col] = value;
                    if (col > maxCol) maxCol = col;
                }
            }

            StringBuilder sb = new();
            for (int i = 0; i <= maxCol; i++)
            {
                if (i > 0) sb.Append('\t');
                if (cells.TryGetValue(i, out string v)) sb.Append(v);
            }
            return sb.ToString();
        }

        // <c> の内側から値を取り出す。呼び出し後は </c> の次に進んでいる。
        static string CellValue(XmlReader xr, string type, List<string> shared)
        {
            if (xr.IsEmptyElement) return "";

            string v = null;
            string inlineText = null;

            int depth = xr.Depth;
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth) break;
                if (xr.NodeType != XmlNodeType.Element) continue;

                if (xr.LocalName == "v") v = ReadText(xr);
                else if (xr.LocalName == "is")
                {
                    StringBuilder sb = new();
                    int isDepth = xr.Depth;
                    if (!xr.IsEmptyElement)
                    {
                        while (xr.Read())
                        {
                            if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == isDepth) break;
                            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                                sb.Append(ReadText(xr));
                        }
                    }
                    inlineText = sb.ToString();
                }
            }

            if (type == "s")
            {
                if (v != null && int.TryParse(v, out int idx)
                    && idx >= 0 && idx < shared.Count) return shared[idx];
                return "";
            }
            if (type == "inlineStr") return inlineText ?? "";
            if (type == "b") return (v == "1") ? "TRUE" : "FALSE";
            return v ?? "";
        }

        // 要素の中の文字列だけを読む。
        static string ReadText(XmlReader xr)
        {
            if (xr.IsEmptyElement) return "";
            StringBuilder sb = new();
            int depth = xr.Depth;
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth) break;
                if (xr.NodeType == XmlNodeType.Text
                    || xr.NodeType == XmlNodeType.CDATA
                    || xr.NodeType == XmlNodeType.SignificantWhitespace)
                    sb.Append(xr.Value);
            }
            return sb.ToString();
        }

        // "B12" → 1 (0 始まりの列番号)
        static int ColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 0;
            int col = 0;
            for (int i = 0; i < cellRef.Length; i++)
            {
                char c = cellRef[i];
                if (c >= 'A' && c <= 'Z') col = col * 26 + (c - 'A' + 1);
                else if (c >= 'a' && c <= 'z') col = col * 26 + (c - 'a' + 1);
                else break;
            }
            return col > 0 ? col - 1 : 0;
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            StringBuilder sb = new();
            int blank = 0;
            foreach (string line in s.Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    blank++;
                    if (blank > 2) continue;
                }
                else blank = 0;
                sb.Append(line).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
