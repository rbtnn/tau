// ---------------------------------------------------------------------------
//  pdf2text - PDF からテキストを取り出す (tau の補助ツール)
//
//  COM / 外部ライブラリは使わず、PDF の構文を直接読む。
//  対象は「テキストを持つ普通の PDF」。スキャン画像だけの PDF は
//  文字を取り出せないので、その旨を返す。
//
//  tau の read_document は、この exe を子プロセスとして呼び出す。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Pdf2Text
{
    static class PdfReader
    {
        const int MAX_BYTES = 200 * 1024 * 1024;

        // ---------------- 入口 ----------------

        public static string ReadText(string path, out string error)
        {
            error = null;
            try
            {
                FileInfo fi = new(path);
                if (!fi.Exists) { error = "file does not exist: " + path; return null; }
                if (fi.Length > MAX_BYTES)
                {
                    error = "the PDF is too large to read (" + fi.Length + " bytes)";
                    return null;
                }

                byte[] data = File.ReadAllBytes(path);
                if (!StartsWithPdfHeader(data))
                {
                    error = "not a PDF file";
                    return null;
                }

                PdfDocument doc = PdfDocument.Parse(data);
                if (doc == null)
                {
                    error = "the PDF could not be parsed (damaged or unsupported)";
                    return null;
                }

                if (doc.IsEncrypted)
                {
                    error = "this PDF is encrypted (password protected), so its text "
                        + "cannot be read. Remove the protection and save it again.";
                    return null;
                }

                StringBuilder sb = new();
                int pages = 0;

                bool undecodable = false;

                foreach (PdfDocument.PageContent page in doc.Pages())
                {
                    // 文字の対応表が無いフォントがあれば、復元できない。
                    foreach (KeyValuePair<string, FontMap> f in page.Fonts)
                        if (f.Value != null && f.Value.Undecodable) undecodable = true;

                    // 1 ページに複数の内容ストリームがある場合は連結して読む。
                    StringBuilder pageText = new();
                    foreach (PdfObject content in page.Contents)
                    {
                        string stream = doc.DecodeStream(content);
                        if (stream == null) continue;

                        if (stream.IndexOf("Tj", StringComparison.Ordinal) >= 0
                            || stream.IndexOf("TJ", StringComparison.Ordinal) >= 0)
                            doc.HadContent = true;

                        pageText.Append(TextExtractor.Run(stream, page.Fonts));
                        pageText.Append('\n');
                    }
                    pages++;

                    string text = pageText.ToString();
                    if (text.Trim().Length > 0)
                    {
                        if (sb.Length > 0) sb.Append("\n\n");
                        sb.Append(text);
                    }
                }

                string result = Normalize(sb.ToString());

                // 内容ストリームに文字が無い場合は、フォームの値を読む。
                // (入力欄だけの PDF)
                if (!HasReadableText(result) && !doc.HadContent)
                {
                    List<string> fields = doc.FormFieldTexts();
                    if (fields.Count > 0)
                    {
                        StringBuilder fb = new();
                        foreach (string v in fields) fb.Append(v).Append('\n');
                        result = Normalize(fb.ToString());
                    }
                }

                // 制御文字だけ(グリフ番号がそのまま出た場合)は意味のある
                // テキストではない。読み取れなかったものとして扱う。
                if (!HasReadableText(result) || undecodable)
                {
                    error = (doc.HadContent || undecodable)
                        ? "the PDF has text, but it could not be decoded "
                          + "(the font has no ToUnicode map and no embedded cmap). "
                          + "Try converting it to text with another tool, or save "
                          + "it as an image and use OCR."
                        : "no text could be extracted. This PDF may be a scan "
                          + "(image only) and needs OCR.";
                    return null;
                }

                // 何ページから取ったかを呼び出し側で添えられるよう、
                // ページ数は error ではなく戻り値の先頭に書かない。
                // (ページ数は ToolExecutor 側で数える)
                return result;
            }
            catch (Exception ex)
            {
                error = "could not read the PDF: " + ex.Message;
                return null;
            }
        }

        // 読めない PDF の構造を調べる。原因の切り分け用。
        public static string Describe(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                StringBuilder sb = new();
                sb.Append("size=").Append(data.Length);

                // バージョン
                string head = PdfUtil.Sub(Latin1(data), 0, Math.Min(64, data.Length));
                int v = head.IndexOf("%PDF-");
                if (v >= 0)
                {
                    int end = head.IndexOfAny(new[] { '\r', '\n' }, v);
                    if (end < 0) end = head.Length;
                    sb.Append("  version=").Append(PdfUtil.Sub(head, v + 5, end - v - 5));
                }

                PdfDocument doc = PdfDocument.Parse(data);
                if (doc == null)
                {
                    sb.Append("  parse=FAILED");
                    return sb.ToString();
                }

                sb.Append("  objects=").Append(doc.ObjectCount);
                sb.Append("  objstm=").Append(doc.CompressedCount);
                sb.Append("  encrypted=").Append(doc.IsEncrypted ? "yes" : "no");

                int pages = 0;
                int streams = 0;
                int filters = 0;
                int decoded = 0;
                List<string> filterNames = new();

                foreach (PdfDocument.PageContent page in doc.Pages())
                {
                    pages++;
                    foreach (PdfObject c in page.Contents)
                    {
                        streams++;
                        List<string> f = doc.FiltersOf(c);
                        foreach (string name in f)
                        {
                            filters++;
                            if (!filterNames.Contains(name)) filterNames.Add(name);
                        }
                        if (doc.DecodeStream(c) != null) decoded++;
                    }
                }

                sb.Append("  pages=").Append(pages);
                sb.Append("  contents=").Append(streams);
                sb.Append("  decoded=").Append(decoded);
                sb.Append("  filters=[").Append(string.Join(",", filterNames)).Append(']');

                // フォントの構成。復号できない原因を切り分けるために出す。
                // 同じフォントが何度も出るので、名前でまとめる。
                SortedDictionary<string, int> fonts = new(StringComparer.Ordinal);
                int undecodable = 0;

                foreach (PdfDocument.PageContent page in doc.Pages())
                {
                    foreach (KeyValuePair<string, FontMap> kv in page.Fonts)
                    {
                        FontMap fm = kv.Value;
                        if (fm == null) continue;

                        string line = fm.Describe();
                        fonts.TryGetValue(line, out int n);
                        fonts[line] = n + 1;
                        if (fm.Undecodable) undecodable++;
                    }
                }

                sb.Append("\n  fonts=").Append(fonts.Count);
                sb.Append("  undecodable=").Append(undecodable);

                int shown = 0;
                foreach (KeyValuePair<string, int> kv in fonts)
                {
                    if (shown++ >= 20) { sb.Append("\n  ..."); break; }
                    sb.Append("\n    x").Append(kv.Value).Append(' ').Append(kv.Key);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "describe failed: " + ex.Message;
            }
        }

        static bool Contains(byte[] data, string s)
        {
            return Encoding.Latin1.GetString(data).Contains(s);
        }

        static string Latin1(byte[] b)
        {
            return Encoding.Latin1.GetString(b);
        }

        /// <summary>ページ数を数える。</summary>
        public static int CountPages(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                PdfDocument doc = PdfDocument.Parse(data);
                if (doc == null) return 0;
                int n = 0;
                foreach (PdfDocument.PageContent page in doc.Pages())
                    if (page.Contents.Count > 0) n++;
                return n;
            }
            catch { return 0; }
        }

        // 読める文字が十分にあるか(制御文字・置換文字だけは不可)
        static bool HasReadableText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int good = 0;
            foreach (char c in s)
            {
                if (c == '\n' || c == '\t' || c == '\r') continue;
                if (char.IsControl(c)) continue;
                if (c == '\uFFFD' || c == '?') continue;
                good++;
            }
            return good > 0;
        }

        static bool StartsWithPdfHeader(byte[] data)
        {
            // 先頭 1KB 以内に %PDF- があれば PDF とみなす
            int limit = Math.Min(data.Length, 1024);
            for (int i = 0; i + 4 < limit; i++)
            {
                if (data[i] == '%' && data[i + 1] == 'P'
                    && data[i + 2] == 'D' && data[i + 3] == 'F')
                    return true;
            }
            return false;
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            StringBuilder sb = new();
            int blank = 0;
            foreach (string line in s.Split('\n'))
            {
                string t = line.TrimEnd();
                if (t.Length == 0)
                {
                    blank++;
                    if (blank > 2) continue;
                }
                else blank = 0;
                sb.Append(t).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }
    }

    // =======================================================================
    //  PDF の最小パーサ
    // =======================================================================
    sealed class PdfDocument
    {
        readonly byte[] data;

        // data を Latin-1 として見た文字列。GetObject が何度も呼ばれるので
        // 1 度だけ作る(35MB の PDF では、毎回作ると非常に遅い)。
        string textCache;

        // オブジェクト番号 → オブジェクトの開始位置
        readonly Dictionary<int, int> offsets = new();

        // トレーラの /Root
        int rootRef = -1;

        // オブジェクトストリーム(/Type /ObjStm)の中にあるオブジェクト。
        // 番号 → (ストリーム番号, ストリーム内の位置)
        readonly Dictionary<int, KeyValuePair<int, int>> compressed = new();

        // 展開済みのオブジェクトストリーム。番号 → 本文
        readonly Dictionary<int, string> objStmBodies = new();

        // xref ストリーム(/Type /XRef)の辞書(あれば)
        Dictionary<string, PdfObject> xrefDict;

        public int ObjectCount { get { return offsets.Count; } }
        public int CompressedCount { get { return compressed.Count; } }

        // /Encrypt が指定されているか(暗号化 PDF)
        public bool IsEncrypted { get; private set; }

        // 内容ストリームに文字を描く命令(Tj / TJ)があったか。
        // あったのに文字が取れない場合は、フォント側の問題。
        public bool HadContent { get; set; }


        PdfDocument(byte[] data)
        {
            this.data = data;
        }

        public static PdfDocument Parse(byte[] data)
        {
            PdfDocument doc = new(data);
            if (!doc.ScanObjects()) return null;
            doc.ReadXrefStream();
            doc.IndexObjectStreams();
            doc.ReadTrailer();
            return doc;
        }

        // ---------------- オブジェクトの索引 ----------------

        bool ScanObjects()
        {
            string text = Text;

            // "N G obj" を探す。単純な走査で十分(壊れた PDF では諦める)。
            int pos = 0;
            while (true)
            {
                int idx = PdfUtil.SafeIndexOf(text, "obj", pos);
                if (idx < 0) break;
                pos = idx + 3;

                // "obj" の直前が空白でなければ別物(例: /Obj)
                int p = idx - 1;
                if (p < 0 || !IsWhite(text[p])) continue;

                // 数字をさかのぼる: <num> <gen> obj
                // p は "obj" の前の空白。gen の終わりは p。
                int genEnd = p;
                int genStart = genEnd;
                while (genStart > 0 && char.IsDigit(text[genStart - 1])) genStart--;
                if (genStart == genEnd) continue;

                // gen の前の空白
                int sep = genStart - 1;
                if (sep < 0 || !IsWhite(text[sep])) continue;

                int numEnd = sep;
                int numStart = numEnd;
                while (numStart > 0 && char.IsDigit(text[numStart - 1])) numStart--;
                if (numStart == numEnd) continue;

                if (!int.TryParse(text.AsSpan(numStart, numEnd - numStart), out int num)) continue;

                offsets[num] = numStart;
            }
            return offsets.Count > 0;
        }

        // ---------------- xref ストリーム ----------------

        // PDF 1.5 以降は trailer の代わりに /Type /XRef のストリームを使う。
        // ここから /Root と、オブジェクトストリーム内のオブジェクトの
        // 位置(W[0]=2 のエントリ)を取り出す。
        void ReadXrefStream()
        {
            // ReadXrefEntries が offsets に足すので、キーをコピーしてから回す。
            foreach (int num in new List<int>(offsets.Keys))
            {
                PdfObject o = GetObject(num);
                if (o == null || o.Dict == null) continue;
                if (!o.Dict.ContainsKey("Type")) continue;
                if (o.Dict["Type"].NameValue != "XRef") continue;

                xrefDict = o.Dict;

                // /Root は直接入っていることが多い
                if (rootRef < 0 && o.Dict.ContainsKey("Root"))
                {
                    int r = RefOf(o.Dict["Root"]);
                    if (r >= 0) rootRef = r;
                }

                ReadXrefEntries(o);
            }
        }

        // xref ストリームの本体を読み、type=2 のエントリを索引に足す。
        void ReadXrefEntries(PdfObject o)
        {
            string body = DecodeStream(o);
            if (body == null) return;

            // /W [w0 w1 w2] のフィールド幅
            int[] w = new int[] { 1, 4, 2 };
            if (o.Dict.ContainsKey("W") && o.Dict["W"] != null && o.Dict["W"].IsArray)
                w = ParseIntArray(o.Dict["W"].ArrayBody);
            // 幅が 0 のフィールドは「既定値」を使う規格なので許容する。
            if (w.Length < 3) return;
            if (w[0] < 0 || w[1] < 0 || w[2] < 0) return;
            if (w[0] + w[1] + w[2] == 0) return;

            // /Index [start count ...]。無ければ [0 Size]
            int[] index = new int[] { 0, offsets.Count + 1 };
            if (o.Dict.ContainsKey("Index") && o.Dict["Index"] != null && o.Dict["Index"].IsArray)
                index = ParseIntArray(o.Dict["Index"].ArrayBody);

            byte[] bytes = Encoding.Latin1.GetBytes(body);
            int pos = 0;

            for (int i = 0; i + 1 < index.Length; i += 2)
            {
                int start = index[i];
                int count = index[i + 1];
                for (int k = 0; k < count; k++)
                {
                    if (pos + w[0] + w[1] + w[2] > bytes.Length) return;

                    // 幅 0 のフィールドは既定値(type=1, field3=0)
                    int type = (w[0] == 0) ? 1 : ReadBigEndian(bytes, pos, w[0]);
                    int field2 = (w[1] == 0) ? 0 : ReadBigEndian(bytes, pos + w[0], w[1]);
                    int field3 = (w[2] == 0) ? 0 : ReadBigEndian(bytes, pos + w[0] + w[1], w[2]);
                    pos += w[0] + w[1] + w[2];

                    int objNum = start + k;

                    if (type == 1)
                    {
                        // 通常のオブジェクト。ScanObjects で見つかっているはず。
                        if (!offsets.ContainsKey(objNum)) offsets[objNum] = field2;
                    }
                    else if (type == 2)
                    {
                        // オブジェクトストリーム内。field2=ストリーム番号, field3=位置
                        compressed[objNum] = new(field2, field3);
                    }
                }
            }
        }

        static int[] ParseIntArray(string body)
        {
            List<int> list = new();
            int i = 0;
            while (i < body.Length)
            {
                i = SkipWs(body, i);
                if (i >= body.Length) break;
                int start = i;
                if (body[i] == '-') i++;
                while (i < body.Length && char.IsDigit(body[i])) i++;
                if (i > start)
                {
                    if (int.TryParse(body.AsSpan(start, i - start), out int v)) list.Add(v);
                }
                else i++;
            }
            return list.ToArray();
        }

        static int ReadBigEndian(byte[] b, int pos, int width)
        {
            int v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | b[pos + i];
            return v;
        }

        // ---------------- オブジェクトストリーム ----------------

        // /Type /ObjStm の中には複数のオブジェクトがまとめて入っている。
        // 索引に無い番号は、ここから取り出す。
        void IndexObjectStreams()
        {
            // 走査中に offsets が増えることがあるので、キーをコピーする。
            List<int> all = new(offsets.Keys);
            List<int> streams = new();
            foreach (int num in all)
            {
                PdfObject o = GetObject(num);
                if (o == null || o.Dict == null) continue;
                if (!o.Dict.ContainsKey("Type")) continue;
                if (o.Dict["Type"].NameValue != "ObjStm") continue;
                streams.Add(num);
            }

            foreach (int num in streams)
            {
                PdfObject o = GetObject(num);
                if (o == null) continue;

                string body = DecodeStream(o);
                if (body == null) continue;
                objStmBodies[num] = body;

                int n = o.Dict.ContainsKey("N") ? AsInt(o.Dict["N"]) : 0;
                int first = o.Dict.ContainsKey("First") ? AsInt(o.Dict["First"]) : 0;
                if (n <= 0 || first < 0) continue;

                // 先頭に "<num> <offset> ..." が並ぶ
                string header = PdfUtil.Sub(body, 0, Math.Min(first, body.Length));
                int[] pairs = ParseIntArray(header);

                for (int i = 0; i + 1 < pairs.Length && i / 2 < n; i += 2)
                {
                    int objNum = pairs[i];
                    int off = pairs[i + 1];
                    compressed[objNum] = new(num, off);
                }
            }
        }

        static bool IsWhite(char c)
        {
            return c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == '\0';
        }

        static string Latin1(byte[] b)
        {
            return Encoding.Latin1.GetString(b);
        }

        // data を Latin-1 として見た文字列。GetObject が何度も呼ばれるので
        // 1 度だけ作る(35MB の PDF では、毎回作ると非常に遅い)。
        string Text
        {
            get
            {
                if (textCache == null) textCache = Encoding.Latin1.GetString(data);
                return textCache;
            }
        }

        void ReadTrailer()
        {
            string text = Latin1(data);
            int idx = text.LastIndexOf("trailer", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int brace = PdfUtil.SafeIndexOf(text, "<<", idx);
                if (brace >= 0)
                {
                    Dictionary<string, PdfObject> dict = ParseDictAt(text, brace);
                    if (dict != null && dict.ContainsKey("Root"))
                    {
                        int r = RefOf(dict["Root"]);
                        if (r >= 0) rootRef = r;
                    }
                    if (dict != null && dict.ContainsKey("Encrypt")) IsEncrypted = true;
                }
            }

            // xref ストリームに /Encrypt がある場合
            if (xrefDict != null && xrefDict.ContainsKey("Encrypt")) IsEncrypted = true;

            // 古い PDF は trailer が無いことがある。Catalog を直接探す。
            // オブジェクトストリームの中にある場合も探す。
            if (rootRef < 0)
            {
                List<int> all = new(offsets.Keys);
                foreach (int num in new List<int>(compressed.Keys))
                    if (!all.Contains(num)) all.Add(num);

                foreach (int num in all)
                {
                    PdfObject o = GetObject(num);
                    if (o == null || o.Dict == null) continue;
                    if (!o.Dict.ContainsKey("Pages")) continue;

                    // /Type /Catalog を優先する
                    if (o.Dict.ContainsKey("Type") && o.Dict["Type"].NameValue == "Catalog")
                    {
                        rootRef = num;
                        break;
                    }
                    if (rootRef < 0) rootRef = num;
                }
            }
        }

        // ---------------- オブジェクトの取り出し ----------------

        public PdfObject GetObject(int num)
        {
            if (offsets.TryGetValue(num, out int pos))
            {
                string text = Text;

                // "<num> <gen> obj" の後ろから辞書を読む
                int obj = PdfUtil.SafeIndexOf(text, "obj", pos);
                if (obj >= 0)
                {
                    int start = obj + 3;
                    while (start < text.Length && IsWhite(text[start])) start++;
                    PdfObject o = ParseValueAt(text, start);
                    if (o != null) return o;
                }
            }

            // オブジェクトストリームの中にある場合
            if (compressed.TryGetValue(num, out KeyValuePair<int, int> where))
            {
                return GetCompressedObject(num, where.Key, where.Value);
            }
            return null;
        }

        // オブジェクトストリームの中の 1 オブジェクトを読む。
        PdfObject GetCompressedObject(int num, int streamNum, int index)
        {
            if (!objStmBodies.TryGetValue(streamNum, out string body)) return null;
            if (!offsets.TryGetValue(streamNum, out int pos)) return null;

            PdfObject stm = GetObject(streamNum);
            if (stm == null || stm.Dict == null) return null;
            int first = stm.Dict.ContainsKey("First") ? AsInt(stm.Dict["First"]) : 0;

            int start = first + index;
            if (start < 0 || start >= body.Length) return null;

            // オブジェクトは空白で区切られている。次のオブジェクトの開始位置まで読む。
            int end = body.Length;
            int depth = 0;
            for (int i = start; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '<' && i + 1 < body.Length && body[i + 1] == '<') { depth++; i++; continue; }
                if (c == '>' && i + 1 < body.Length && body[i + 1] == '>')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                    {
                        // 辞書の終わり。直後が空白ならここまで。
                        end = i + 1;
                        break;
                    }
                    continue;
                }
            }

            string fragment = PdfUtil.Sub(body, start, Math.Max(0, end - start));
            return ParseFragment(fragment);
        }

        // 単体の値(辞書・配列など)を文字列から読む。
        static PdfObject ParseFragment(string fragment)
        {
            if (fragment == null) return null;

            int i = SkipWs(fragment, 0);
            if (i >= fragment.Length) return null;

            if (string.CompareOrdinal(fragment, i, "<<", 0, 2) == 0)
            {
                int end = SkipDict(fragment, i);
                Dictionary<string, PdfObject> dict = ParseDictBody(PdfUtil.Sub(fragment, i + 2, end - i - 4));
                return PdfObject.DictObj(dict);
            }
            return null;
        }

        PdfObject ParseValueAt(string text, int start)
        {
            if (start >= text.Length) return null;

            if (string.CompareOrdinal(text, start, "<<", 0, 2) == 0)
            {
                Dictionary<string, PdfObject> dict = ParseDictAt(text, start);
                if (dict == null) return null;

                // 辞書の直後に stream が続くか
                int after = SkipDict(text, start);
                int p = after;
                while (p < text.Length && IsWhite(text[p])) p++;
                PdfObject o = new() { Dict = dict, Start = start };
                if (string.CompareOrdinal(text, p, "stream", 0, 6) == 0)
                {
                    o.StreamStart = p + 6;
                    while (o.StreamStart < text.Length
                        && (text[o.StreamStart] == '\r' || text[o.StreamStart] == '\n'))
                        o.StreamStart++;
                    o.Length = dict.ContainsKey("Length")
                        ? LengthOf(dict["Length"]) : -1;
                }
                return o;
            }
            return null;
        }

        static int SkipDict(string text, int start)
        {
            int depth = 0;
            int i = start;
            while (i < text.Length - 1)
            {
                if (text[i] == '<' && text[i + 1] == '<') { depth++; i += 2; continue; }
                if (text[i] == '>' && text[i + 1] == '>')
                {
                    depth--;
                    i += 2;
                    if (depth == 0) return i;
                    continue;
                }
                i++;
            }
            return text.Length;
        }

        // text の start から始まる辞書を読む。
        //
        // 以前は Text を使っていたが、ParseValueAt から短い断片
        // (fragment など)の位置を渡すことがあり、別の文字列に対して
        // インデックスを使うため "Index was out of range" になっていた。
        Dictionary<string, PdfObject> ParseDictAt(string text, int start)
        {
            if (text == null) text = Text;
            if (start < 0 || start + 2 > text.Length) return new Dictionary<string, PdfObject>();

            int end = SkipDict(text, start);
            return ParseDictBody(PdfUtil.Sub(text, start + 2, end - start - 4));
        }

        // "<< ... >>" の中身を読む。
        // 値は 数値 / 参照 / 名前 / 文字列 / 配列 / 辞書 の最小対応。
        static Dictionary<string, PdfObject> ParseDictBody(string body)
        {
            Dictionary<string, PdfObject> d = new();
            int i = 0;
            while (i < body.Length)
            {
                i = SkipWs(body, i);
                if (i >= body.Length) break;

                if (body[i] != '/') { i++; continue; }

                int nameStart = i + 1;
                i++;
                while (i < body.Length && !IsDelimiterOrWs(body[i])) i++;
                string key = PdfUtil.Sub(body, nameStart, i - nameStart);

                i = SkipWs(body, i);
                if (i >= body.Length) break;

                int next;
                PdfObject value = ParseValue(body, i, out next);
                i = next;
                if (value != null) d[key] = value;
            }
            return d;
        }

        static int SkipWs(string s, int i)
        {
            while (i < s.Length && IsWhite(s[i])) i++;
            return i;
        }

        static bool IsDelimiterOrWs(char c)
        {
            return IsWhite(c) || c == '/' || c == '[' || c == ']'
                || c == '<' || c == '>' || c == '(' || c == ')';
        }

        static PdfObject ParseValue(string s, int i, out int next)
        {
            next = i;
            if (i >= s.Length) return null;

            // 参照: N G R
            if (char.IsDigit(s[i]))
            {
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                int num = ParseInt(s, start, i);

                int j = SkipWs(s, i);
                int genStart = j;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j > genStart)
                {
                    int k = SkipWs(s, j);
                    if (k < s.Length && s[k] == 'R')
                    {
                        next = k + 1;
                        return PdfObject.Ref(num);
                    }
                }

                next = i;
                return PdfObject.Number(num);
            }

            switch (s[i])
            {
                case '/':
                    {
                        int start = ++i;
                        while (i < s.Length && !IsDelimiterOrWs(s[i])) i++;
                        next = i;
                        return PdfObject.Name(PdfUtil.Sub(s, start, i - start));
                    }
                case '(':
                    {
                        int start = ++i;
                        int depth = 1;
                        while (i < s.Length)
                        {
                            if (s[i] == '\\') { i += 2; continue; }
                            if (s[i] == '(') depth++;
                            else if (s[i] == ')')
                            {
                                depth--;
                                if (depth == 0) break;
                            }
                            i++;
                        }
                        string raw = PdfUtil.Sub(s, start, Math.Max(0, i - start));
                        next = Math.Min(s.Length, i + 1);
                        return PdfObject.Str(Unescape(raw));
                    }
                case '[':
                    {
                        int start = ++i;
                        int depth = 1;
                        while (i < s.Length)
                        {
                            if (s[i] == '[') depth++;
                            else if (s[i] == ']')
                            {
                                depth--;
                                if (depth == 0) break;
                            }
                            i++;
                        }
                        next = Math.Min(s.Length, i + 1);
                        return PdfObject.Array(PdfUtil.Sub(s, start, Math.Max(0, i - start)));
                    }
                case '<':
                    {
                        if (i + 1 < s.Length && s[i + 1] == '<')
                        {
                            int end = SkipDict(s, i);
                            next = end;
                            return PdfObject.DictObj(ParseDictBody(
                                PdfUtil.Sub(s, i + 2, Math.Max(0, end - i - 4))));
                        }
                        int start = i + 1;
                        int e = s.IndexOf('>', start);
                        if (e < 0) e = s.Length;
                        next = Math.Min(s.Length, e + 1);
                        return PdfObject.Str(PdfUtil.Sub(s, start, e - start));
                    }
                default:
                    {
                        // true / false / null / 演算子などは読み飛ばす
                        int start = i;
                        while (i < s.Length && !IsDelimiterOrWs(s[i])) i++;
                        next = i;
                        return PdfObject.Name(PdfUtil.Sub(s, start, i - start));
                    }
            }
        }

        static int ParseInt(string s, int start, int end)
        {
            int v = 0;
            for (int i = start; i < end; i++) v = v * 10 + (s[i] - '0');
            return v;
        }

        static string Unescape(string raw)
        {
            StringBuilder sb = new();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= raw.Length) break;
                switch (raw[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '(': sb.Append('('); break;
                    case ')': sb.Append(')'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\n': break;
                    case '\r': if (i + 1 < raw.Length && raw[i + 1] == '\n') i++; break;
                    default:
                        if (raw[i] >= '0' && raw[i] <= '7')
                        {
                            int v = 0, n = 0;
                            while (n < 3 && i < raw.Length && raw[i] >= '0' && raw[i] <= '7')
                            {
                                v = v * 8 + (raw[i] - '0');
                                i++;
                                n++;
                            }
                            i--;
                            sb.Append((char)v);
                        }
                        else sb.Append(raw[i]);
                        break;
                }
            }
            return sb.ToString();
        }

        static int RefOf(PdfObject o)
        {
            if (o == null) return -1;
            return o.IsRef ? o.RefNum : -1;
        }

        static int AsInt(PdfObject o)
        {
            if (o == null) return -1;
            return o.IsNumber ? o.NumberValue : -1;
        }

        // /Length は数値か、数値を指す間接参照。
        // 参照のまま -1 を返すと、圧縮データの中の "endstream" を
        // 誤って探してしまうため、ここで解決する。
        int LengthOf(PdfObject o)
        {
            if (o == null) return -1;
            if (o.IsNumber) return o.NumberValue;
            if (!o.IsRef) return -1;

            PdfObject resolved = GetObject(o.RefNum);
            if (resolved == null) return -1;
            return resolved.IsNumber ? resolved.NumberValue : -1;
        }

        // ---------------- ページの内容ストリーム ----------------

        /// <summary>全ページの内容ストリーム(辞書付き)を順に返す。</summary>
        // 1 ページ分の内容ストリームと、そのページのフォント対応。
        public sealed class PageContent
        {
            // /Contents は 1 つのストリームか、配列(複数)になる。
            public List<PdfObject> Contents = new();
            public Dictionary<string, FontMap> Fonts = new();

            // 後方互換: 最初のストリーム
            public PdfObject Content
            {
                get { return Contents.Count > 0 ? Contents[0] : null; }
            }
        }

        public IEnumerable<PageContent> Pages()
        {
            List<PageContent> result = new();
            HashSet<int> seen = new();

            if (rootRef >= 0)
            {
                PdfObject catalog = GetObject(rootRef);
                if (catalog != null && catalog.Dict != null
                    && catalog.Dict.ContainsKey("Pages"))
                {
                    CollectPageNodes(RefOf(catalog.Dict["Pages"]), seen, result, null);
                }
            }

            // Catalog を辿れなかった場合は、/Type /Page を持つオブジェクトを拾う
            if (result.Count == 0)
            {
                foreach (int num in offsets.Keys)
                {
                    PdfObject o = GetObject(num);
                    if (o == null || o.Dict == null) continue;
                    if (!o.Dict.ContainsKey("Type")) continue;
                    if (o.Dict["Type"].NameValue != "Page") continue;
                    if (!o.Dict.ContainsKey("Contents")) continue;

                    PageContent pc = new();
                    pc.Contents = ContentsOf(o.Dict["Contents"]);
                    pc.Fonts = FontsOf(o.Dict, null);
                    if (pc.Contents.Count > 0) result.Add(pc);
                }
            }
            return result;
        }

        // ページの内容ストリームだけを返す(後方互換用)
        public IEnumerable<PdfObject> PageContents()
        {
            List<PdfObject> list = new();
            foreach (PageContent p in Pages())
                foreach (PdfObject c in p.Contents)
                    if (c != null) list.Add(c);
            return list;
        }

        // /Contents の値(単体または配列)を、ストリームの一覧にする。
        List<PdfObject> ContentsOf(PdfObject contents)
        {
            List<PdfObject> list = new();
            if (contents == null) return list;

            if (contents.IsArray && contents.ArrayBody != null)
            {
                string body = contents.ArrayBody;
                int i = 0;
                while (i < body.Length)
                {
                    i = SkipWs(body, i);
                    if (i >= body.Length) break;
                    if (!char.IsDigit(body[i])) { i++; continue; }

                    int start = i;
                    while (i < body.Length && char.IsDigit(body[i])) i++;
                    int num = ParseInt(body, start, i);
                    i = SkipWs(body, i);
                    while (i < body.Length && char.IsDigit(body[i])) i++;
                    i = SkipWs(body, i);
                    if (i < body.Length && body[i] == 'R') i++;

                    PdfObject o = GetObject(num);
                    if (o != null && o.StreamStart > 0) list.Add(o);
                }
                return list;
            }

            PdfObject single = Resolve(contents);
            if (single != null && single.StreamStart > 0) list.Add(single);
            return list;
        }

        // AcroForm のフィールド値を集める。
        // 内容ストリームに文字が無い PDF(フォームだけ)で使う。
        public List<string> FormFieldTexts()
        {
            List<string> list = new();
            if (rootRef < 0) return list;

            PdfObject catalog = GetObject(rootRef);
            if (catalog == null || catalog.Dict == null) return list;
            if (!catalog.Dict.ContainsKey("AcroForm")) return list;

            PdfObject form = Resolve(catalog.Dict["AcroForm"]);
            if (form == null || form.Dict == null) return list;
            if (!form.Dict.ContainsKey("Fields")) return list;

            PdfObject fields = Resolve(form.Dict["Fields"]);
            if (fields == null || !fields.IsArray || fields.ArrayBody == null) return list;

            // フィールドは入れ子になる。参照を順に辿る。
            List<int> refs = new();
            string body = fields.ArrayBody;
            int i = 0;
            while (i < body.Length)
            {
                i = SkipWs(body, i);
                if (i >= body.Length) break;
                if (!char.IsDigit(body[i])) { i++; continue; }
                int start = i;
                while (i < body.Length && char.IsDigit(body[i])) i++;
                refs.Add(ParseInt(body, start, i));
                i = SkipWs(body, i);
                while (i < body.Length && char.IsDigit(body[i])) i++;
                i = SkipWs(body, i);
                if (i < body.Length && body[i] == 'R') i++;
            }

            HashSet<int> seen = new();
            foreach (int num in refs) CollectFieldValues(num, list, seen);
            return list;
        }

        void CollectFieldValues(int num, List<string> list, HashSet<int> seen)
        {
            if (num < 0 || seen.Contains(num)) return;
            seen.Add(num);

            PdfObject f = GetObject(num);
            if (f == null || f.Dict == null) return;

            // /V が値。文字列なら文字、名前なら名前。
            if (f.Dict.ContainsKey("V"))
            {
                PdfObject v = f.Dict["V"];
                if (v != null && v.IsString && v.StringValue != null
                    && v.StringValue.Trim().Length > 0)
                    list.Add(v.StringValue);
                else if (v != null && v.IsName && v.NameValue != null)
                    list.Add(v.NameValue);
            }

            // 入れ子の /Kids
            if (f.Dict.ContainsKey("Kids"))
            {
                PdfObject kids = Resolve(f.Dict["Kids"]);
                if (kids != null && kids.IsArray && kids.ArrayBody != null)
                {
                    string body = kids.ArrayBody;
                    int i = 0;
                    while (i < body.Length)
                    {
                        i = SkipWs(body, i);
                        if (i >= body.Length) break;
                        if (!char.IsDigit(body[i])) { i++; continue; }
                        int start = i;
                        while (i < body.Length && char.IsDigit(body[i])) i++;
                        int child = ParseInt(body, start, i);
                        i = SkipWs(body, i);
                        while (i < body.Length && char.IsDigit(body[i])) i++;
                        i = SkipWs(body, i);
                        if (i < body.Length && body[i] == 'R') i++;
                        CollectFieldValues(child, list, seen);
                    }
                }
            }
        }

        // ページの /Resources からフォント対応を取り出す。
        // 親(Pages ノード)の Resources を継承する。
        Dictionary<string, FontMap> FontsOf(Dictionary<string, PdfObject> pageDict,
                                            Dictionary<string, PdfObject> inherited)
        {
            Dictionary<string, PdfObject> res = inherited;
            if (pageDict != null && pageDict.ContainsKey("Resources"))
            {
                PdfObject r = Resolve(pageDict["Resources"]);
                if (r != null && r.Dict != null) res = r.Dict;
            }

            Dictionary<string, FontMap> map = new();
            if (res == null || !res.ContainsKey("Font")) return map;

            PdfObject fonts = Resolve(res["Font"]);
            if (fonts == null || fonts.Dict == null) return map;

            foreach (KeyValuePair<string, PdfObject> kv in fonts.Dict)
            {
                PdfObject fontObj = Resolve(kv.Value);
                if (fontObj == null || fontObj.Dict == null) continue;
                map[kv.Key] = BuildFontMap(fontObj);
            }
            return map;
        }

        // フォントの /ToUnicode CMap を読む。無ければ null(その場合は
        // 従来どおり、文字列をそのまま解釈する)。
        FontMap BuildFontMap(PdfObject fontObj)
        {
            FontMap fm = new();

            // 診断用の情報を控えておく(--describe で表示する)
            if (fontObj.Dict.ContainsKey("BaseFont"))
                fm.BaseFont = fontObj.Dict["BaseFont"].NameValue ?? "";
            if (fontObj.Dict.ContainsKey("Subtype"))
                fm.Subtype = fontObj.Dict["Subtype"].NameValue ?? "";
            if (fontObj.Dict.ContainsKey("Encoding"))
            {
                PdfObject e = fontObj.Dict["Encoding"];
                if (e != null && e.IsName) fm.Encoding = e.NameValue ?? "";
                else if (e != null && e.Dict != null) fm.Encoding = "(dict)";
            }

            // 2 バイト文字か(Identity-H / Identity-V / Type0)
            if (fontObj.Dict.ContainsKey("Subtype")
                && fontObj.Dict["Subtype"].NameValue == "Type0")
                fm.TwoByte = true;
            if (fontObj.Dict.ContainsKey("Encoding"))
            {
                PdfObject enc = fontObj.Dict["Encoding"];
                if (enc != null && enc.IsName
                    && (enc.NameValue == "Identity-H" || enc.NameValue == "Identity-V"))
                    fm.TwoByte = true;
            }

            // /Encoding の /Differences でグリフ名が指定されている場合。
            // ToUnicode が無くても、グリフ名から文字が分かる。
            if (fontObj.Dict.ContainsKey("Encoding"))
            {
                PdfObject enc = Resolve(fontObj.Dict["Encoding"]);
                if (enc != null && enc.Dict != null && enc.Dict.ContainsKey("Differences"))
                {
                    PdfObject diffs = enc.Dict["Differences"];
                    if (diffs != null && diffs.IsArray && diffs.ArrayBody != null)
                    {
                        fm.HasDifferences = true;
                        ParseDifferences(diffs.ArrayBody, fm);
                    }
                }
            }

            if (fontObj.Dict.ContainsKey("ToUnicode"))
            {
                fm.HasToUnicode = true;
                PdfObject tu = Resolve(fontObj.Dict["ToUnicode"]);
                if (tu != null)
                {
                    string cmap = DecodeStream(tu);
                    if (cmap != null) ParseToUnicode(cmap, fm);
                }
            }

            // ToUnicode が無い場合、埋め込み TrueType (FontFile2) の cmap から
            // グリフ番号 → Unicode を逆引きする。これで Identity-H/V の
            // CID フォントでも文字が取れることがある。
            // CID フォントで ToUnicode が無い場合、Adobe-Japan1 の
            // 標準 CID 対応表から文字を復元する。
            if (!fm.HasMap && fm.TwoByte && fontObj.Dict.ContainsKey("DescendantFonts"))
            {
                PdfObject df = Resolve(fontObj.Dict["DescendantFonts"]);
                if (df != null && df.IsArray && df.ArrayBody != null)
                {
                    int num = FirstRefIn(df.ArrayBody);
                    PdfObject cidFont = num >= 0 ? GetObject(num) : null;
                    if (cidFont != null && cidFont.Dict != null)
                    {
                        fm.Ordering = OrderingOf(cidFont);
                        if (IsJapan1(cidFont)) fm.UseJapan1 = true;
                    }
                }
            }

            if (!fm.HasMap && fm.TwoByte && !fm.UseJapan1) fm.Undecodable = true;

            if (!fm.HasMap && fontObj.Dict.ContainsKey("DescendantFonts"))
            {
                PdfObject df = Resolve(fontObj.Dict["DescendantFonts"]);
                if (df != null && df.IsArray && df.ArrayBody != null)
                {
                    int num = FirstRefIn(df.ArrayBody);
                    PdfObject cidFont = num >= 0 ? GetObject(num) : null;
                    if (cidFont != null && cidFont.Dict != null)
                        ReadEmbeddedCmap(cidFont, fm);
                }
            }

            fm.MappedCount = fm.Map.Count;
            return fm;
        }

        // /CIDSystemInfo の Ordering を取り出す(診断用)。
        string OrderingOf(PdfObject cidFont)
        {
            if (!cidFont.Dict.ContainsKey("CIDSystemInfo")) return "";
            PdfObject info = Resolve(cidFont.Dict["CIDSystemInfo"]);
            if (info == null || info.Dict == null) return "";
            if (!info.Dict.ContainsKey("Ordering")) return "";

            PdfObject o = info.Dict["Ordering"];
            if (o == null) return "";
            return o.IsString ? o.StringValue : (o.NameValue ?? "");
        }

        // /CIDSystemInfo の Ordering が Japan1 か。
        bool IsJapan1(PdfObject cidFont)
        {
            if (!cidFont.Dict.ContainsKey("CIDSystemInfo")) return false;
            PdfObject info = Resolve(cidFont.Dict["CIDSystemInfo"]);
            if (info == null || info.Dict == null) return false;
            if (!info.Dict.ContainsKey("Ordering")) return false;

            PdfObject o = info.Dict["Ordering"];
            if (o == null) return false;
            string name = o.IsString ? o.StringValue : o.NameValue;
            return name != null && name.Contains("Japan1");
        }

        static int FirstRefIn(string body)
        {
            int i = SkipWs(body, 0);
            int start = i;
            while (i < body.Length && char.IsDigit(body[i])) i++;
            if (i == start) return -1;
            return ParseInt(body, start, i);
        }

        // 埋め込み TrueType の cmap テーブルを読み、
        // グリフ番号 → Unicode の対応を作る。
        void ReadEmbeddedCmap(PdfObject cidFont, FontMap fm)
        {
            if (!cidFont.Dict.ContainsKey("FontDescriptor")) return;
            PdfObject fd = Resolve(cidFont.Dict["FontDescriptor"]);
            if (fd == null || fd.Dict == null) return;

            fm.HasFontFile2 = fd.Dict.ContainsKey("FontFile2");
            fm.HasFontFile3 = fd.Dict.ContainsKey("FontFile3");

            if (!fm.HasFontFile2) return;

            PdfObject ff = Resolve(fd.Dict["FontFile2"]);
            if (ff == null) return;
            string raw = DecodeStream(ff);
            if (raw == null) return;

            byte[] ttf = Encoding.Latin1.GetBytes(raw);
            Dictionary<int, int> glyphToUnicode = TrueTypeCmap(ttf);
            if (glyphToUnicode == null || glyphToUnicode.Count == 0) return;

            foreach (KeyValuePair<int, int> kv in glyphToUnicode)
            {
                int cp = kv.Value;
                if (cp < 0) continue;
                if (cp >= 0xD800 && cp <= 0xDFFF) continue;
                fm.Map[kv.Key] = ((char)cp).ToString();
            }
            if (fm.HasMap) fm.Undecodable = false;
        }

        // TrueType の cmap を読み、グリフ番号 → Unicode を返す。
        // format 4 (BMP) と format 12 (UCS-4) に対応する。
        static Dictionary<int, int> TrueTypeCmap(byte[] ttf)
        {
            try
            {
                if (ttf.Length < 12) return null;
                int numTables = (ttf[4] << 8) | ttf[5];
                int cmapOffset = -1;

                for (int i = 0; i < numTables; i++)
                {
                    int rec = 12 + i * 16;
                    if (rec + 16 > ttf.Length) break;
                    uint tag = ((uint)ttf[rec] << 24) | ((uint)ttf[rec + 1] << 16)
                        | ((uint)ttf[rec + 2] << 8) | ttf[rec + 3];
                    // 'cmap'
                    if (tag == 0x636D6170)
                    {
                        cmapOffset = (ttf[rec + 8] << 24) | (ttf[rec + 9] << 16)
                            | (ttf[rec + 10] << 8) | ttf[rec + 11];
                        break;
                    }
                }
                if (cmapOffset < 0 || cmapOffset + 4 > ttf.Length) return null;

                int numSub = (ttf[cmapOffset + 2] << 8) | ttf[cmapOffset + 3];
                Dictionary<int, int> result = new();

                for (int i = 0; i < numSub; i++)
                {
                    int rec = cmapOffset + 4 + i * 8;
                    if (rec + 8 > ttf.Length) break;
                    int platform = (ttf[rec] << 8) | ttf[rec + 1];
                    int encoding = (ttf[rec + 2] << 8) | ttf[rec + 3];
                    int offset = (ttf[rec + 4] << 24) | (ttf[rec + 5] << 16)
                        | (ttf[rec + 6] << 8) | ttf[rec + 7];
                    int sub = cmapOffset + offset;
                    if (sub + 2 > ttf.Length) continue;

                    int format = (ttf[sub] << 8) | ttf[sub + 1];

                    // Unicode のサブテーブルだけを使う
                    if (!(platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10))))
                        continue;

                    if (format == 4) CmapFormat4(ttf, sub, result);
                    else if (format == 12) CmapFormat12(ttf, sub, result);
                }
                return result;
            }
            catch { return null; }
        }

        static void CmapFormat4(byte[] t, int sub, Dictionary<int, int> result)
        {
            int segX2 = U16(t, sub + 6);
            int segs = segX2 / 2;

            int endCodes = sub + 14;
            int startCodes = endCodes + segX2 + 2;      // reservedPad
            int idDelta = startCodes + segX2;
            int idRangeOffset = idDelta + segX2;

            for (int i = 0; i < segs; i++)
            {
                int end = U16(t, endCodes + i * 2);
                int start = U16(t, startCodes + i * 2);
                if (start > end) continue;
                if (start == 0xFFFF && end == 0xFFFF) continue;

                int delta = S16(t, idDelta + i * 2);
                int rangeOff = U16(t, idRangeOffset + i * 2);

                for (int c = start; c <= end && c - start < 65536; c++)
                {
                    int glyph;
                    if (rangeOff == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        int addr = idRangeOffset + i * 2 + rangeOff + (c - start) * 2;
                        if (addr + 2 > t.Length) continue;
                        glyph = U16(t, addr);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph == 0) continue;

                    // 逆引き。同じグリフに複数の文字が対応する場合は最初を優先。
                    if (!result.ContainsKey(glyph)) result[glyph] = c;
                }
            }
        }

        static void CmapFormat12(byte[] t, int sub, Dictionary<int, int> result)
        {
            uint groups = U32(t, sub + 12);
            if (groups > 100000) return;

            for (uint g = 0; g < groups; g++)
            {
                int rec = sub + 16 + (int)g * 12;
                if (rec + 12 > t.Length) break;
                uint startChar = U32(t, rec);
                uint endChar = U32(t, rec + 4);
                uint startGlyph = U32(t, rec + 8);

                for (uint c = startChar; c <= endChar && c - startChar < 65536; c++)
                {
                    int glyph = (int)(startGlyph + (c - startChar));
                    if (!result.ContainsKey(glyph))
                    {
                        if (c <= 0x10FFFF && !(c >= 0xD800 && c <= 0xDFFF))
                            result[glyph] = (int)c;
                    }
                }
            }
        }

        static int U16(byte[] t, int p)
        {
            if (p + 1 >= t.Length) return 0;
            return (t[p] << 8) | t[p + 1];
        }

        static int S16(byte[] t, int p)
        {
            if (p + 1 >= t.Length) return 0;
            return (short)((t[p] << 8) | t[p + 1]);
        }

        static uint U32(byte[] t, int p)
        {
            if (p + 3 >= t.Length) return 0;
            return ((uint)t[p] << 24) | ((uint)t[p + 1] << 16)
                | ((uint)t[p + 2] << 8) | t[p + 3];
        }

        // "/Differences [ 24 /breve /caron ... ]" を読む。
        // 数値が来たら、その番号から順にグリフ名を割り当てる。
        static void ParseDifferences(string body, FontMap fm)
        {
            int i = 0;
            int code = 0;
            while (i < body.Length)
            {
                i = SkipWs(body, i);
                if (i >= body.Length) break;

                char c = body[i];
                if (c == '/')
                {
                    int start = ++i;
                    while (i < body.Length && !IsDelimiterOrWs(body[i])) i++;
                    string glyph = PdfUtil.Sub(body, start, i - start);
                    string text = GlyphToUnicode(glyph);
                    if (text != null) fm.Map[code] = text;
                    code++;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < body.Length && char.IsDigit(body[i])) i++;
                    int.TryParse(body.AsSpan(start, i - start), out code);
                    continue;
                }
                i++;
            }
        }

        // グリフ名 → Unicode。よく使われる名前だけを扱う。
        // (uniXXXX / uXXXX 形式と、基本ラテン・記号)
        static string GlyphToUnicode(string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return null;

            // uni3053 のような 4 桁 16 進
            if (glyph.StartsWith("uni") && glyph.Length >= 7)
            {
                string hex = PdfUtil.Sub(glyph, 3, glyph.Length - 3);
                if (hex.Length >= 4)
                {
                    int v = HexToInt(PdfUtil.Sub(hex, 0, 4));
                    if (v > 0) return ((char)v).ToString();
                }
            }
            // u3053 のような形式
            if (glyph.StartsWith("u") && glyph.Length >= 5)
            {
                int v = HexToInt(PdfUtil.Sub(glyph, 1, 4));
                if (v > 0) return ((char)v).ToString();
            }

            // 基本ラテン
            if (glyph.Length == 1) return glyph;

            // よく使われる名前
            switch (glyph)
            {
                case "space": return " ";
                case "exclam": return "!";
                case "quotedbl": return "\"";
                case "numbersign": return "#";
                case "dollar": return "$";
                case "percent": return "%";
                case "ampersand": return "&";
                case "quotesingle": return "'";
                case "parenleft": return "(";
                case "parenright": return ")";
                case "asterisk": return "*";
                case "plus": return "+";
                case "comma": return ",";
                case "hyphen": return "-";
                case "period": return ".";
                case "slash": return "/";
                case "zero": return "0";
                case "one": return "1";
                case "two": return "2";
                case "three": return "3";
                case "four": return "4";
                case "five": return "5";
                case "six": return "6";
                case "seven": return "7";
                case "eight": return "8";
                case "nine": return "9";
                case "colon": return ":";
                case "semicolon": return ";";
                case "less": return "<";
                case "equal": return "=";
                case "greater": return ">";
                case "question": return "?";
                case "at": return "@";
                case "bracketleft": return "[";
                case "backslash": return "\\";
                case "bracketright": return "]";
                case "asciicircum": return "^";
                case "underscore": return "_";
                case "grave": return "`";
                case "braceleft": return "{";
                case "bar": return "|";
                case "braceright": return "}";
                case "asciitilde": return "~";
                case "breve": return "\u02D8";
                case "caron": return "\u02C7";
                case "circumflex": return "\u02C6";
                case "dotaccent": return "\u02D9";
                case "hungarumlaut": return "\u02DD";
                case "ogonek": return "\u02DB";
                case "ring": return "\u02DA";
                case "tilde": return "\u02DC";
                case "endash": return "\u2013";
                case "emdash": return "\u2014";
                case "quoteleft": return "\u2018";
                case "quoteright": return "\u2019";
                case "quotedblleft": return "\u201C";
                case "quotedblright": return "\u201D";
                case "bullet": return "\u2022";
                case "ellipsis": return "\u2026";
                case "dagger": return "\u2020";
                case "daggerdbl": return "\u2021";
                case "perthousand": return "\u2030";
                case "guilsinglleft": return "\u2039";
                case "guilsinglright": return "\u203A";
                case "fraction": return "\u2044";
                case "florin": return "\u0192";
                case "germandbls": return "\u00DF";
                case "AE": return "\u00C6";
                case "ae": return "\u00E6";
                case "OE": return "\u0152";
                case "oe": return "\u0153";
                case "Oslash": return "\u00D8";
                case "oslash": return "\u00F8";
                case "Lslash": return "\u0141";
                case "lslash": return "\u0142";
                case "dotlessi": return "\u0131";
                default:
                    return null;
            }
        }

        static void ParseToUnicode(string cmap, FontMap fm)
        {
            // bfchar: <src> <dst> の組
            // bfrange: <lo> <hi> <dst> または <lo> <hi> [<d1> <d2> ...]
            int i = 0;
            while (i < cmap.Length)
            {
                int bfchar = PdfUtil.SafeIndexOf(cmap, "beginbfchar", i);
                int bfrange = PdfUtil.SafeIndexOf(cmap, "beginbfrange", i);
                if (bfchar < 0 && bfrange < 0) break;

                bool isChar = (bfchar >= 0 && (bfrange < 0 || bfchar < bfrange));
                int start = isChar ? bfchar + "beginbfchar".Length : bfrange + "beginbfrange".Length;
                int endIdx = PdfUtil.SafeIndexOf(cmap, isChar ? "endbfchar" : "endbfrange", start);
                if (endIdx < 0) break;

                string body = PdfUtil.Sub(cmap, start, endIdx - start);
                if (isChar) ParseBfChar(body, fm);
                else ParseBfRange(body, fm);

                i = endIdx + 1;
            }
        }

        static void ParseBfChar(string body, FontMap fm)
        {
            List<string> tokens = HexTokens(body);
            for (int i = 0; i + 1 < tokens.Count; i += 2)
            {
                int src = HexToInt(tokens[i]);
                string dst = HexToString(tokens[i + 1]);
                if (src >= 0) fm.Map[src] = dst;
            }
        }

        static void ParseBfRange(string body, FontMap fm)
        {
            // <lo> <hi> <dst> または <lo> <hi> [<d1> <d2> ...]
            int i = 0;
            while (i < body.Length)
            {
                int loStart = body.IndexOf('<', i);
                if (loStart < 0) break;
                int loEnd = body.IndexOf('>', loStart);
                if (loEnd < 0) break;

                int hiStart = body.IndexOf('<', loEnd);
                if (hiStart < 0) break;
                int hiEnd = body.IndexOf('>', hiStart);
                if (hiEnd < 0) break;

                int lo = HexToInt(PdfUtil.Sub(body, loStart + 1, loEnd - loStart - 1));
                int hi = HexToInt(PdfUtil.Sub(body, hiStart + 1, hiEnd - hiStart - 1));

                int after = hiEnd + 1;

                // 配列形式か
                int bracket = body.IndexOf('[', after);
                int single = body.IndexOf('<', after);
                if (bracket >= 0 && (single < 0 || bracket < single))
                {
                    int close = body.IndexOf(']', bracket);
                    if (close < 0) close = body.Length;
                    string list = PdfUtil.Sub(body, bracket + 1, close - bracket - 1);
                    List<string> tokens = HexTokens(list);
                    for (int k = 0; k < tokens.Count && lo + k <= hi; k++)
                        fm.Map[lo + k] = HexToString(tokens[k]);
                    i = close + 1;
                }
                else if (single >= 0)
                {
                    int singleEnd = body.IndexOf('>', single);
                    if (singleEnd < 0) break;
                    string dstHex = PdfUtil.Sub(body, single + 1, singleEnd - single - 1);
                    byte[] dstBytes = HexToBytes(dstHex);

                    for (int k = lo; k <= hi && k - lo < 65536; k++)
                    {
                        // 最後のバイト(または 16bit 値)を k-lo だけ増やす
                        byte[] b = (byte[])dstBytes.Clone();
                        if (b.Length >= 2)
                        {
                            int v = (b[b.Length - 2] << 8) | b[b.Length - 1];
                            v += (k - lo);
                            b[b.Length - 2] = (byte)(v >> 8);
                            b[b.Length - 1] = (byte)v;
                        }
                        else if (b.Length == 1)
                        {
                            int v = b[0] + (k - lo);
                            b[0] = (byte)v;
                        }
                        fm.Map[k] = DecodeUtf16Be(b);
                    }
                    i = singleEnd + 1;
                }
                else break;
            }
        }

        static List<string> HexTokens(string s)
        {
            List<string> list = new();
            int i = 0;
            while (i < s.Length)
            {
                int start = s.IndexOf('<', i);
                if (start < 0) break;
                int end = s.IndexOf('>', start);
                if (end < 0) break;
                list.Add(PdfUtil.Sub(s, start + 1, end - start - 1));
                i = end + 1;
            }
            return list;
        }

        static int HexToInt(string hex)
        {
            if (hex == null || hex.Length == 0) return -1;
            int v = 0;
            foreach (char c in hex)
            {
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else continue;
                v = (v << 4) | d;
            }
            return v;
        }

        static byte[] HexToBytes(string hex)
        {
            if (hex == null) return Array.Empty<byte>();
            StringBuilder clean = new();
            foreach (char c in hex)
                if (Uri.IsHexDigit(c)) clean.Append(c);
            if (clean.Length % 2 == 1) clean.Append('0');

            byte[] b = new byte[clean.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = (byte)HexToInt(clean.ToString(i * 2, 2));
            return b;
        }

        static string HexToString(string hex)
        {
            return DecodeUtf16Be(HexToBytes(hex));
        }

        static string DecodeUtf16Be(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            if (b.Length == 1) return ((char)b[0]).ToString();
            return Encoding.BigEndianUnicode.GetString(b);
        }

        void CollectPageNodes(int num, HashSet<int> seen, List<PageContent> result,
                              Dictionary<string, PdfObject> inheritedResources)
        {
            if (num < 0 || seen.Contains(num)) return;
            seen.Add(num);

            PdfObject node = GetObject(num);
            if (node == null || node.Dict == null) return;

            // 親から継承する /Resources
            Dictionary<string, PdfObject> res = inheritedResources;
            if (node.Dict.ContainsKey("Resources"))
            {
                PdfObject r = Resolve(node.Dict["Resources"]);
                if (r != null && r.Dict != null) res = r.Dict;
            }

            PdfObject type = node.Dict.ContainsKey("Type") ? node.Dict["Type"] : null;

            if (type != null && type.NameValue == "Page")
            {
                PageContent pc = new();
                if (node.Dict.ContainsKey("Contents"))
                    pc.Contents = ContentsOf(node.Dict["Contents"]);
                pc.Fonts = FontsOf(node.Dict, res);
                result.Add(pc);
                return;
            }

            if (node.Dict.ContainsKey("Kids"))
            {
                PdfObject kids = node.Dict["Kids"];
                if (kids != null && kids.IsArray)
                {
                    string body = kids.ArrayBody;
                    int i = 0;
                    while (i < body.Length)
                    {
                        i = SkipWs(body, i);
                        if (i >= body.Length) break;
                        if (!char.IsDigit(body[i])) { i++; continue; }

                        int start = i;
                        while (i < body.Length && char.IsDigit(body[i])) i++;
                        int childNum = ParseInt(body, start, i);
                        i = SkipWs(body, i);
                        while (i < body.Length && char.IsDigit(body[i])) i++;
                        i = SkipWs(body, i);
                        if (i < body.Length && body[i] == 'R') i++;

                        CollectPageNodes(childNum, seen, result, res);
                    }
                }
            }
        }

        PdfObject Resolve(PdfObject o)
        {
            if (o == null) return null;
            if (!o.IsRef) return o;
            return GetObject(o.RefNum);
        }

        // ---------------- ストリームの復号 ----------------

        /// <summary>ストリームの中身を復号して文字列で返す。</summary>
        public string DecodeStream(PdfObject o)
        {
            if (o == null || o.Dict == null || o.StreamStart <= 0) return null;

            string text = Text;

            int start = o.StreamStart;
            int len = o.Length;
            int end;
            if (len >= 0 && start + len <= text.Length) end = start + len;
            else
            {
                // /Length が無い/壊れている場合は endstream を探す
                end = PdfUtil.SafeIndexOf(text, "endstream", start);
                if (end < 0) return null;
            }

            byte[] raw = new byte[Math.Max(0, end - start)];
            Array.Copy(data, start, raw, 0, raw.Length);

            // /Filter は 1 つまたは配列。前から順に適用する。
            List<string> filters = FilterNames(o);
            if (filters.Count == 0) return Latin1(raw);

            byte[] current = raw;
            foreach (string f in filters)
            {
                current = ApplyFilter(f, current);
                if (current == null) return null;

                // FlateDecode のあとに PNG 予測子が指定されることがある
                // (/DecodeParms の /Predictor)。xref ストリームで多い。
                if (f == "FlateDecode" || f == "Fl")
                {
                    int predictor = PredictorOf(o);
                    if (predictor >= 10)
                    {
                        int colors = DecodeParamInt(o, "Colors", 1);
                        int bits = DecodeParamInt(o, "BitsPerComponent", 8);
                        int columns = DecodeParamInt(o, "Columns", 1);
                        current = Unpredict(current, predictor, colors, bits, columns);
                        if (current == null) return null;
                    }
                }
            }
            return Latin1(current);
        }

        // /DecodeParms (または /DP) の数値。無ければ既定値。
        int DecodeParamInt(PdfObject o, string key, int def)
        {
            if (o.Dict == null) return def;

            PdfObject parms = null;
            if (o.Dict.ContainsKey("DecodeParms")) parms = Resolve(o.Dict["DecodeParms"]);
            else if (o.Dict.ContainsKey("DP")) parms = Resolve(o.Dict["DP"]);
            if (parms == null) return def;

            // 配列ならフィルタと同じ順の最初の要素
            if (parms.IsArray && parms.ArrayBody != null)
            {
                int num = FirstRefIn(parms.ArrayBody);
                if (num >= 0) parms = GetObject(num);
                else
                {
                    Dictionary<string, PdfObject> d = ParseDictBody(
                        parms.ArrayBody.Trim().TrimStart('<').TrimStart('<'));
                    if (d != null && d.ContainsKey(key)) return AsInt(d[key]);
                    return def;
                }
            }
            if (parms.Dict != null && parms.Dict.ContainsKey(key))
                return AsInt(parms.Dict[key]);
            return def;
        }

        int PredictorOf(PdfObject o)
        {
            return DecodeParamInt(o, "Predictor", 1);
        }

        // PNG 予測子(Predictor 10..15)を解除する。
        static byte[] Unpredict(byte[] input, int predictor, int colors, int bits, int columns)
        {
            if (input == null || input.Length == 0) return input;

            int bpp = Math.Max(1, (colors * bits + 7) / 8);
            int rowLen = (colors * bits * columns + 7) / 8;

            // 予測子 10..14 は固定、15 は行ごとに種類が入る
            List<byte> outBytes = new();
            byte[] prior = new byte[rowLen];
            int pos = 0;

            while (pos < input.Length)
            {
                int type;
                if (predictor == 15)
                {
                    if (pos >= input.Length) break;
                    type = input[pos++];
                }
                else type = predictor - 10;

                if (pos + rowLen > input.Length) break;

                byte[] row = new byte[rowLen];
                Array.Copy(input, pos, row, 0, rowLen);
                pos += rowLen;

                switch (type)
                {
                    case 0: break;   // None
                    case 1:          // Sub
                        for (int i = bpp; i < rowLen; i++)
                            row[i] = (byte)(row[i] + row[i - bpp]);
                        break;
                    case 2:          // Up
                        for (int i = 0; i < rowLen; i++)
                            row[i] = (byte)(row[i] + prior[i]);
                        break;
                    case 3:          // Average
                        for (int i = 0; i < rowLen; i++)
                        {
                            int left = (i >= bpp) ? row[i - bpp] : 0;
                            row[i] = (byte)(row[i] + ((left + prior[i]) / 2));
                        }
                        break;
                    case 4:          // Paeth
                        for (int i = 0; i < rowLen; i++)
                        {
                            int left = (i >= bpp) ? row[i - bpp] : 0;
                            int up = prior[i];
                            int upLeft = (i >= bpp) ? prior[i - bpp] : 0;
                            row[i] = (byte)(row[i] + Paeth(left, up, upLeft));
                        }
                        break;
                    default:
                        return null;
                }

                outBytes.AddRange(row);
                prior = row;
            }
            return outBytes.ToArray();
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        static byte[] ApplyFilter(string name, byte[] input)
        {
            switch (name)
            {
                case "FlateDecode":
                case "Fl":
                    try
                    {
                        using MemoryStream ms = new(input);
                        using ZLibStream z = new(ms, CompressionMode.Decompress);
                        using MemoryStream outMs = new();
                        z.CopyTo(outMs);
                        return outMs.ToArray();
                    }
                    catch
                    {
                        // 一部の PDF は zlib ヘッダ無しの raw deflate
                        try
                        {
                            using MemoryStream ms = new(input);
                            using DeflateStream d = new(ms, CompressionMode.Decompress);
                            using MemoryStream outMs = new();
                            d.CopyTo(outMs);
                            return outMs.ToArray();
                        }
                        catch { return null; }
                    }
                case "ASCII85Decode":
                case "A85":
                    return Ascii85(input);
                case "ASCIIHexDecode":
                case "AHx":
                    return AsciiHex(input);
                case "RunLengthDecode":
                case "RL":
                    return RunLength(input);
                case "LZWDecode":
                case "LZW":
                    return Lzw(input, 1);
                default:
                    return null;   // JPX / CCITT / JBIG2 などは未対応
            }
        }

        // ASCII85。Adobe 形式の <~ ~> にも対応する。
        static byte[] Ascii85(byte[] input)
        {
            List<byte> outBytes = new();
            int i = 0;
            int n = input.Length;

            // 先頭の <~ を飛ばす
            if (n >= 2 && input[0] == '<' && input[1] == '~') i = 2;

            uint tuple = 0;
            int count = 0;
            for (; i < n; i++)
            {
                byte b = input[i];
                if (b == '~') break;                       // ~> の終わり
                if (b <= ' ') continue;                     // 空白は無視
                if (b == 'z' && count == 0)
                {
                    outBytes.Add(0); outBytes.Add(0);
                    outBytes.Add(0); outBytes.Add(0);
                    continue;
                }
                if (b < '!' || b > 'u') continue;
                tuple = tuple * 85 + (uint)(b - '!');
                count++;
                if (count == 5)
                {
                    outBytes.Add((byte)(tuple >> 24));
                    outBytes.Add((byte)(tuple >> 16));
                    outBytes.Add((byte)(tuple >> 8));
                    outBytes.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 0)
            {
                // 端数は 'u' で埋めて 4 バイトにし、余りを落とす
                for (int k = count; k < 5; k++) tuple = tuple * 85 + 84;
                outBytes.Add((byte)(tuple >> 24));
                if (count > 1) outBytes.Add((byte)(tuple >> 16));
                if (count > 2) outBytes.Add((byte)(tuple >> 8));
                if (count > 3) outBytes.Add((byte)tuple);
            }
            return outBytes.ToArray();
        }

        static byte[] AsciiHex(byte[] input)
        {
            List<byte> outBytes = new();
            int hi = -1;
            foreach (byte b in input)
            {
                int v;
                if (b >= '0' && b <= '9') v = b - '0';
                else if (b >= 'a' && b <= 'f') v = b - 'a' + 10;
                else if (b >= 'A' && b <= 'F') v = b - 'A' + 10;
                else if (b == '>') break;
                else continue;

                if (hi < 0) hi = v;
                else
                {
                    outBytes.Add((byte)((hi << 4) | v));
                    hi = -1;
                }
            }
            if (hi >= 0) outBytes.Add((byte)(hi << 4));
            return outBytes.ToArray();
        }

        // LZW(TIFF 形式ではなく PDF の LZW)。EarlyChange=1 が既定。
        static byte[] Lzw(byte[] input, int earlyChange)
        {
            List<byte> outBytes = new();

            // 辞書: コード → バイト列。最初は 0..255 の 1 バイト
            List<byte[]> table = new();
            for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i });
            table.Add(Array.Empty<byte>());   // 256 = clear
            table.Add(Array.Empty<byte>());   // 257 = EOD

            int codeBits = 9;
            int bitPos = 0;
            byte[] prev = null;

            while (true)
            {
                int code = ReadBits(input, ref bitPos, codeBits);
                if (code < 0) break;

                if (code == 256)
                {
                    // 辞書をリセット
                    table.Clear();
                    for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i });
                    table.Add(Array.Empty<byte>());
                    table.Add(Array.Empty<byte>());
                    codeBits = 9;
                    prev = null;
                    continue;
                }
                if (code == 257) break;   // EOD

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                }
                else if (prev != null && code == table.Count)
                {
                    // KwKwK ケース
                    entry = new byte[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else
                {
                    break;   // 壊れたデータ
                }

                outBytes.AddRange(entry);

                if (prev != null)
                {
                    byte[] add = new byte[prev.Length + 1];
                    Array.Copy(prev, add, prev.Length);
                    add[prev.Length] = entry[0];
                    table.Add(add);
                }
                prev = entry;

                // 次のコード幅
                int next = table.Count + earlyChange;
                if (next >= 4096) codeBits = 12;
                else if (next >= 2048) codeBits = 12;
                else if (next >= 1024) codeBits = 11;
                else if (next >= 512) codeBits = 10;
                else codeBits = 9;
            }
            return outBytes.ToArray();
        }

        static int ReadBits(byte[] data, ref int bitPos, int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                int byteIndex = bitPos >> 3;
                if (byteIndex >= data.Length) return -1;
                int bitIndex = 7 - (bitPos & 7);
                value = (value << 1) | ((data[byteIndex] >> bitIndex) & 1);
                bitPos++;
            }
            return value;
        }

        static byte[] RunLength(byte[] input)
        {
            List<byte> outBytes = new();
            int i = 0;
            while (i < input.Length)
            {
                int len = input[i++];
                if (len == 128) break;
                if (len < 128)
                {
                    // そのまま len+1 バイト
                    int n = len + 1;
                    for (int k = 0; k < n && i < input.Length; k++) outBytes.Add(input[i++]);
                }
                else
                {
                    // 次の 1 バイトを 257-len 回繰り返す
                    if (i >= input.Length) break;
                    byte b = input[i++];
                    int n = 257 - len;
                    for (int k = 0; k < n; k++) outBytes.Add(b);
                }
            }
            return outBytes.ToArray();
        }

        // /Filter の一覧(配列なら順番どおり)
        public List<string> FiltersOf(PdfObject o)
        {
            return FilterNames(o);
        }

        static List<string> FilterNames(PdfObject o)
        {
            List<string> list = new();
            if (o.Dict == null || !o.Dict.ContainsKey("Filter")) return list;
            PdfObject f = o.Dict["Filter"];
            if (f == null) return list;
            if (f.IsName) { list.Add(f.NameValue); return list; }
            if (f.IsArray && f.ArrayBody != null)
            {
                int i = 0;
                string body = f.ArrayBody;
                while (i < body.Length)
                {
                    int slash = body.IndexOf('/', i);
                    if (slash < 0) break;
                    int start = slash + 1;
                    int end = start;
                    while (end < body.Length && !IsDelimiterOrWs(body[end])) end++;
                    list.Add(PdfUtil.Sub(body, start, end - start));
                    i = end;
                }
            }
            return list;
        }

    }

    // フォントの文字コード → 実際の文字(ToUnicode CMap)
    sealed class FontMap
    {
        // コード → 文字列
        public Dictionary<int, string> Map = new();

        // 2 バイトで 1 文字か(Identity-H など)
        public bool TwoByte;

        public bool HasMap { get { return Map.Count > 0; } }

        // 文字の対応表がまったく無い(グリフ番号しか分からない)
        public bool Undecodable;

        // Adobe-Japan1 の標準 CID 対応表で復元する
        public bool UseJapan1;

        // 診断用(--describe で表示する)
        public string BaseFont = "";
        public string Subtype = "";
        public string Encoding = "";
        public string Ordering = "";
        public bool HasToUnicode;
        public bool HasDifferences;
        public bool HasFontFile2;
        public bool HasFontFile3;
        public int MappedCount;

        // 1 行の説明
        public string Describe()
        {
            StringBuilder sb = new();
            sb.Append(BaseFont.Length > 0 ? BaseFont : "(no BaseFont)");
            sb.Append(" [").Append(Subtype.Length > 0 ? Subtype : "?").Append(']');
            if (Encoding.Length > 0) sb.Append(" enc=").Append(Encoding);
            if (Ordering.Length > 0) sb.Append(" order=").Append(Ordering);
            sb.Append(" twoByte=").Append(TwoByte ? "yes" : "no");
            sb.Append(" toUnicode=").Append(HasToUnicode ? "yes" : "no");
            sb.Append(" differences=").Append(HasDifferences ? "yes" : "no");
            sb.Append(" fontFile2=").Append(HasFontFile2 ? "yes" : "no");
            sb.Append(" fontFile3=").Append(HasFontFile3 ? "yes" : "no");
            sb.Append(" japan1=").Append(UseJapan1 ? "yes" : "no");
            sb.Append(" mapped=").Append(MappedCount);
            if (Undecodable) sb.Append(" UNDECODABLE");
            return sb.ToString();
        }
    }

    sealed class PdfObject
    {
        public Dictionary<string, PdfObject> Dict;
        public int Start;
        public int StreamStart;
        public int Length = -1;

        public bool IsRef;
        public int RefNum;
        public bool IsNumber;
        public int NumberValue;
        public bool IsName;
        public string NameValue;
        public bool IsString;
        public string StringValue;
        public bool IsArray;
        public string ArrayBody;

        public static PdfObject Ref(int num)
        {
            return new PdfObject { IsRef = true, RefNum = num };
        }

        public static PdfObject Number(int v)
        {
            return new PdfObject { IsNumber = true, NumberValue = v };
        }

        public static PdfObject Name(string name)
        {
            return new PdfObject { IsName = true, NameValue = name };
        }

        public static PdfObject Str(string s)
        {
            return new PdfObject { IsString = true, StringValue = s };
        }

        public static PdfObject Array(string body)
        {
            return new PdfObject { IsArray = true, ArrayBody = body };
        }

        public static PdfObject DictObj(Dictionary<string, PdfObject> d)
        {
            return new PdfObject { Dict = d };
        }
    }

    // =======================================================================
    //  内容ストリームから文字を取り出す
    // =======================================================================
    static class TextExtractor
    {
        public static string Run(string content)
        {
            return Run(content, null);
        }

        public static string Run(string content, Dictionary<string, FontMap> fonts)
        {
            if (string.IsNullOrEmpty(content)) return "";

            StringBuilder sb = new();
            List<string> operands = new();

            // 現在のフォント(Tf で切り替わる)
            FontMap currentFont = null;

            // marked content の ActualText。ActualText があった範囲は、
            // 中の描画文字を捨てて ActualText だけを使う。
            List<string> markedStack = new();
            int skipDepth = 0;

            int i = 0;
            while (i < content.Length)
            {
                char c = content[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '%')
                {
                    while (i < content.Length && content[i] != '\n') i++;
                    continue;
                }

                if (c == '(')
                {
                    string s = ReadLiteral(content, ref i);
                    operands.Add(DecodeString(s, currentFont));
                    continue;
                }

                if (c == '<')
                {
                    if (i + 1 < content.Length && content[i + 1] == '<')
                    {
                        // 辞書。BDC の /ActualText を読むため、中身を保持する。
                        int dictStart = i;
                        i += 2;
                        int depth = 1;
                        while (i < content.Length - 1 && depth > 0)
                        {
                            if (content[i] == '<' && content[i + 1] == '<') { depth++; i += 2; }
                            else if (content[i] == '>' && content[i + 1] == '>') { depth--; i += 2; }
                            else i++;
                        }
                        operands.Add(PdfUtil.Sub(content, dictStart, i - dictStart));
                        continue;
                    }
                    string hex = ReadHex(content, ref i, currentFont);
                    operands.Add(hex);
                    continue;
                }

                if (c == '[')
                {
                    // 配列は TJ の要素として使われる。中身を集めておく。
                    i++;
                    StringBuilder arr = new();
                    while (i < content.Length && content[i] != ']')
                    {
                        if (content[i] == '(')
                        {
                            arr.Append(DecodeString(ReadLiteral(content, ref i), currentFont));
                        }
                        else if (content[i] == '<')
                        {
                            arr.Append(ReadHex(content, ref i, currentFont));
                        }
                        else if (content[i] == '-')
                        {
                            // 字間調整。大きい負値は単語間の空白とみなす
                            int start = i;
                            i++;
                            while (i < content.Length && char.IsDigit(content[i])) i++;
                            if (i - start > 3) arr.Append(' ');
                        }
                        else i++;
                    }
                    if (i < content.Length) i++;
                    operands.Add(arr.ToString());
                    continue;
                }

                // 名前(/F1 など)。Tf でフォントを選ぶのに使う。
                if (c == '/')
                {
                    int nameStart = ++i;
                    while (i < content.Length && !char.IsWhiteSpace(content[i])
                           && content[i] != '(' && content[i] != '<' && content[i] != '['
                           && content[i] != ')' && content[i] != '>' && content[i] != ']'
                           && content[i] != '/')
                        i++;
                    operands.Add("/" + PdfUtil.Sub(content, nameStart, i - nameStart));
                    continue;
                }

                // 演算子を読む
                int opStart = i;
                while (i < content.Length && !char.IsWhiteSpace(content[i])
                       && content[i] != '(' && content[i] != '<' && content[i] != '['
                       && content[i] != ')' && content[i] != '>' && content[i] != ']'
                       && content[i] != '/')
                    i++;

                if (i == opStart) { i++; continue; }
                string op = PdfUtil.Sub(content, opStart, i - opStart);

                // 数値はオペランド(サイズ・位置など)。演算子ではない。
                if (IsNumberToken(op))
                {
                    operands.Add(op);
                    continue;
                }

                switch (op)
                {
                    case "Tj":
                        if (skipDepth == 0 && operands.Count > 0)
                        {
                            sb.Append(operands[operands.Count - 1]);
                        }
                        operands.Clear();
                        break;
                    case "TJ":
                        if (skipDepth == 0 && operands.Count > 0)
                        {
                            sb.Append(operands[operands.Count - 1]);
                        }
                        operands.Clear();
                        break;
                    case "Td":
                    case "TD":
                    case "T*":
                        NewLine(sb);
                        operands.Clear();
                        break;
                    case "Tm":
                        NewLine(sb);
                        operands.Clear();
                        break;
                    case "ET":
                        NewLine(sb);
                        operands.Clear();
                        break;
                    case "BDC":
                    case "BMC":
                        {
                            // marked content。ActualText があれば、
                            // 中の描画の代わりにその文字を使う。
                            string actual = null;
                            for (int k = 0; k < operands.Count; k++)
                            {
                                string o = operands[k];
                                if (o.Length > 1 && o[0] == '<' && o[1] == '<')
                                {
                                    string t = FindActualText(o);
                                    if (t != null) { actual = t; break; }
                                }
                            }
                            markedStack.Add(actual);
                            if (actual != null)
                            {
                                // この BDC の中の描画文字は捨てる
                                sb.Append(actual);
                                skipDepth = markedStack.Count;
                            }
                            operands.Clear();
                            break;
                        }
                    case "EMC":
                        if (markedStack.Count > 0)
                            markedStack.RemoveAt(markedStack.Count - 1);
                        // 外側の marked content に戻ったらスキップ解除
                        if (skipDepth > markedStack.Count) skipDepth = 0;
                        operands.Clear();
                        break;
                    case "Tf":
                        // "<font> <size> Tf"。直前の名前からフォントを選ぶ。
                        currentFont = null;
                        if (fonts != null && operands.Count > 0)
                        {
                            for (int k = operands.Count - 1; k >= 0; k--)
                            {
                                string cand = operands[k];
                                if (cand.Length > 0 && cand[0] == '/')
                                {
                                    string key = cand.Substring(1);
                                    if (fonts.ContainsKey(key)) currentFont = fonts[key];
                                    break;
                                }
                            }
                        }
                        operands.Clear();
                        break;
                    default:
                        // 数値などは捨てる(次の Tj で使わない)
                        if (op != "BT" && op != "Tf" && op != "TL" && op != "Tc"
                            && op != "Tw" && op != "Tz" && op != "Ts" && op != "Tr")
                            operands.Clear();
                        break;
                }
            }

            return sb.ToString();
        }

        // "<< ... /ActualText (文字) ... >>" から ActualText を取り出す。
        // 16 進表記 <...> にも対応する。無ければ null。
        static string FindActualText(string dict)
        {
            int idx = dict.IndexOf("/ActualText", StringComparison.Ordinal);
            if (idx < 0) return null;

            int i = idx + "/ActualText".Length;
            while (i < dict.Length && char.IsWhiteSpace(dict[i])) i++;
            if (i >= dict.Length) return null;

            if (dict[i] == '(')
            {
                StringBuilder sb = new();
                int depth = 0;
                i++;
                while (i < dict.Length)
                {
                    char c = dict[i];
                    if (c == '\\')
                    {
                        i++;
                        if (i >= dict.Length) break;
                        char e = dict[i];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '(': sb.Append('('); break;
                            case ')': sb.Append(')'); break;
                            case '\\': sb.Append('\\'); break;
                            default:
                                if (e >= '0' && e <= '7')
                                {
                                    int v = 0, n = 0;
                                    while (n < 3 && i < dict.Length && dict[i] >= '0' && dict[i] <= '7')
                                    {
                                        v = v * 8 + (dict[i] - '0');
                                        i++;
                                        n++;
                                    }
                                    i--;
                                    sb.Append((char)v);
                                }
                                else sb.Append(e);
                                break;
                        }
                        i++;
                        continue;
                    }
                    if (c == '(') { depth++; sb.Append(c); i++; continue; }
                    if (c == ')')
                    {
                        i++;
                        if (depth == 0) break;
                        depth--;
                        sb.Append(c);
                        continue;
                    }
                    sb.Append(c);
                    i++;
                }
                return sb.ToString();
            }

            if (dict[i] == '<')
            {
                StringBuilder hex = new();
                i++;
                while (i < dict.Length && dict[i] != '>')
                {
                    if (!char.IsWhiteSpace(dict[i])) hex.Append(dict[i]);
                    i++;
                }
                byte[] b = HexToBytes(hex.ToString());
                if (b.Length >= 2) return Encoding.BigEndianUnicode.GetString(b);
                return Encoding.Latin1.GetString(b);
            }

            return null;
        }

        // 数値トークンか(12, -3.5, .5 など)
        static bool IsNumberToken(string t)
        {
            if (t.Length == 0) return false;
            int i = 0;
            if (t[0] == '+' || t[0] == '-') i++;
            bool digit = false;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c >= '0' && c <= '9') { digit = true; continue; }
                if (c == '.') continue;
                return false;
            }
            return digit;
        }

        static void NewLine(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
        }

        static string ReadLiteral(string s, ref int i)
        {
            // s[i] == '('
            i++;
            StringBuilder sb = new();
            int depth = 1;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) break;
                    char e = s[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case 'b': sb.Append('\b'); i++; break;
                        case 'f': sb.Append('\f'); i++; break;
                        case '(': sb.Append('('); i++; break;
                        case ')': sb.Append(')'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case '\n': i++; break;
                        case '\r':
                            i++;
                            if (i < s.Length && s[i] == '\n') i++;
                            break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = 0, n = 0;
                                while (n < 3 && i < s.Length && s[i] >= '0' && s[i] <= '7')
                                {
                                    v = v * 8 + (s[i] - '0');
                                    i++;
                                    n++;
                                }
                                sb.Append((char)v);
                            }
                            else { sb.Append(e); i++; }
                            break;
                    }
                    continue;
                }
                if (c == '(') { depth++; sb.Append(c); i++; continue; }
                if (c == ')')
                {
                    depth--;
                    i++;
                    if (depth == 0) break;
                    sb.Append(c);
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static string ReadHex(string s, ref int i)
        {
            return ReadHex(s, ref i, null);
        }

        static string ReadHex(string s, ref int i, FontMap font)
        {
            // s[i] == '<'
            i++;
            StringBuilder hex = new();
            while (i < s.Length && s[i] != '>')
            {
                if (!char.IsWhiteSpace(s[i])) hex.Append(s[i]);
                i++;
            }
            if (i < s.Length) i++;

            byte[] bytes = HexToBytes(hex.ToString());

            // ToUnicode があれば、それで文字に変換する
            if (font != null && (font.HasMap || font.UseJapan1))
                return DecodeWithMap(bytes, font);

            // UTF-16BE (BOM 付き) か、1 バイト文字かを判定する
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            StringBuilder sb = new();
            for (int k = 0; k < bytes.Length; k++)
            {
                byte b = bytes[k];
                // PDFDocEncoding の ASCII 部分だけ扱う
                sb.Append(b < 0x80 ? (char)b : '?');
            }
            return sb.ToString();
        }

        // リテラル文字列を、フォントに応じて文字へ変換する。
        // ToUnicode が無い場合は、そのまま返す(従来の動作)。
        static string DecodeString(string raw, FontMap font)
        {
            if (font == null || (!font.HasMap && !font.UseJapan1) || raw.Length == 0) return raw;
            byte[] bytes = Encoding.Latin1.GetBytes(raw);
            return DecodeWithMap(bytes, font);
        }

        // フォントのコード列を、ToUnicode の対応表で文字列にする。
        static string DecodeWithMap(byte[] bytes, FontMap font)
        {
            StringBuilder sb = new();
            if (font.TwoByte)
            {
                for (int k = 0; k + 1 < bytes.Length; k += 2)
                {
                    int code = (bytes[k] << 8) | bytes[k + 1];
                    if (font.Map.TryGetValue(code, out string ch))
                    {
                        sb.Append(ch);
                    }
                    else if (font.UseJapan1)
                    {
                        // Adobe-Japan1 の標準 CID 対応表
                        string c = AdobeJapan1.ToText(code);
                        if (c != null) sb.Append(c);
                    }
                }
            }
            else
            {
                for (int k = 0; k < bytes.Length; k++)
                {
                    int code = bytes[k];
                    if (font.Map.TryGetValue(code, out string ch))
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        // /Differences は一部のコードだけを上書きする。
                        // 載っていないコードは、元の 1 バイト文字のまま。
                        if (code >= 0x20) sb.Append((char)code);
                    }
                }
            }
            return sb.ToString();
        }

        static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 == 1) hex += "0";
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return Array.Empty<byte>();
                b[i] = (byte)((hi << 4) | lo);
            }
            return b;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
