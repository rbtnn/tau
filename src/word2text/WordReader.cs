// ---------------------------------------------------------------------------
//  word2text - Word (.docx) からテキストを取り出す (tau の補助ツール)
//
//  COM は使わず、Office 文書の実体である ZIP + XML を直接読む。
//  (Word がインストールされていなくても動く)
//
//  tau の read_document は、この exe を子プロセスとして呼び出す。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Word2Text
{
    static class WordReader
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
            // BOM を見て UTF-8 で読む(Office の XML は通常 UTF-8)
            using MemoryStream ms = new(bytes);
            using StreamReader r = new(ms, Encoding.UTF8, true);
            return r.ReadToEnd();
        }

        static XmlReaderSettings Settings()
        {
            // 外部参照は解決しない(XXE 対策)。DTD も使わない。
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
            };
        }

        /// <summary>docx から本文テキストを取り出す。</summary>
        public static string ReadText(string path, out string error)
        {
            error = null;
            try
            {
                using FileStream fs = File.OpenRead(path);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);

                string xml = TextOf(Entry(zip, "word/document.xml"));
                if (xml == null)
                {
                    error = "not a Word document (word/document.xml is missing)";
                    return null;
                }

                StringBuilder sb = new();
                using StringReader sr = new(xml);
                using XmlReader xr = XmlReader.Create(sr, Settings());

                // w:p が段落、w:t が文字、w:tab がタブ、w:br が改行。
                // w:tbl の中は 1 行 1 段落として出し、セルはタブ区切りにする。
                int tableDepth = 0;

                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        switch (xr.LocalName)
                        {
                            case "tbl":
                                tableDepth++;
                                break;
                            case "tr":
                                NewLine(sb);
                                break;
                            case "tc":
                                if (sb.Length > 0 && sb[sb.Length - 1] != '\t'
                                    && sb[sb.Length - 1] != '\n')
                                    sb.Append('\t');
                                break;
                            case "t":
                                sb.Append(ReadText(xr));
                                break;
                            case "tab":
                                sb.Append('\t');
                                break;
                            case "br":
                            case "cr":
                                sb.Append('\n');
                                break;
                            case "p":
                                if (tableDepth == 0) NewLine(sb);
                                break;
                        }
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        switch (xr.LocalName)
                        {
                            case "tbl":
                                if (tableDepth > 0) tableDepth--;
                                NewLine(sb);
                                break;
                        }
                    }
                }

                return Normalize(sb.ToString());
            }
            catch (Exception ex)
            {
                error = "could not read the Word document: " + ex.Message;
                return null;
            }
        }

        // 要素の中の文字列だけを読む(子要素は読み飛ばす)。
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

        static void NewLine(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            // CRLF を LF に、連続する空行を 2 行までに抑える
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

        /// <summary>docx から取り出せる文字数を概算する(診断用)。</summary>
        public static string Describe(string path)
        {
            try
            {
                using FileStream fs = File.OpenRead(path);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                StringBuilder sb = new();
                sb.Append("size=").Append(fs.Length);
                sb.Append("  document.xml=").Append(zip.GetEntry("word/document.xml") != null ? "yes" : "no");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "describe failed: " + ex.Message;
            }
        }
    }
}
