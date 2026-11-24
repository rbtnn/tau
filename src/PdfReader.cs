// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  PDF のテキスト抽出。
//
//  COM / 外部ライブラリは使わず、PDF の構文を直接読む。
//  対象は「テキストを持つ普通の PDF」。スキャン画像だけの PDF は
//  文字を取り出せないので、その旨を返す。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Tau
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

                StringBuilder sb = new();
                int pages = 0;

                foreach (PdfObject content in doc.PageContents())
                {
                    string stream = doc.DecodeStream(content);
                    if (stream == null) continue;
                    pages++;
                    string text = TextExtractor.Run(stream);
                    if (text.Trim().Length > 0)
                    {
                        if (sb.Length > 0) sb.Append("\n\n");
                        sb.Append(text);
                    }
                }

                string result = Normalize(sb.ToString());
                if (result.Length == 0)
                {
                    error = "no text could be extracted. This PDF may be a scan "
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

        /// <summary>ページ数を数える。</summary>
        public static int CountPages(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                PdfDocument doc = PdfDocument.Parse(data);
                if (doc == null) return 0;
                int n = 0;
                foreach (PdfObject c in doc.PageContents()) if (c != null) n++;
                return n;
            }
            catch { return 0; }
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

        // オブジェクト番号 → オブジェクトの開始位置
        readonly Dictionary<int, int> offsets = new();

        // トレーラの /Root
        int rootRef = -1;


        PdfDocument(byte[] data)
        {
            this.data = data;
        }

        public static PdfDocument Parse(byte[] data)
        {
            PdfDocument doc = new(data);
            if (!doc.ScanObjects()) return null;
            doc.ReadTrailer();
            return doc;
        }

        // ---------------- オブジェクトの索引 ----------------

        bool ScanObjects()
        {
            string text = Latin1(data);

            // "N G obj" を探す。単純な走査で十分(壊れた PDF では諦める)。
            int pos = 0;
            while (true)
            {
                int idx = text.IndexOf("obj", pos, StringComparison.Ordinal);
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

        static bool IsWhite(char c)
        {
            return c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == '\0';
        }

        static string Latin1(byte[] b)
        {
            return Encoding.Latin1.GetString(b);
        }

        void ReadTrailer()
        {
            string text = Latin1(data);
            int idx = text.LastIndexOf("trailer", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int brace = text.IndexOf("<<", idx, StringComparison.Ordinal);
                if (brace >= 0)
                {
                    Dictionary<string, PdfObject> dict = ParseDictAt(brace);
                    if (dict != null && dict.ContainsKey("Root"))
                    {
                        int r = RefOf(dict["Root"]);
                        if (r >= 0) rootRef = r;
                    }
                }
            }

            // 古い PDF は trailer が無いことがある。Catalog を直接探す。
            if (rootRef < 0)
            {
                foreach (int num in offsets.Keys)
                {
                    PdfObject o = GetObject(num);
                    if (o != null && o.Dict != null && o.Dict.ContainsKey("Pages"))
                    {
                        rootRef = num;
                        break;
                    }
                }
            }
        }

        // ---------------- オブジェクトの取り出し ----------------

        public PdfObject GetObject(int num)
        {
            if (!offsets.TryGetValue(num, out int pos)) return null;
            string text = Latin1(data);

            // "<num> <gen> obj" の後ろから辞書を読む
            int obj = text.IndexOf("obj", pos, StringComparison.Ordinal);
            if (obj < 0) return null;
            int start = obj + 3;
            while (start < text.Length && IsWhite(text[start])) start++;

            return ParseValueAt(text, start);
        }

        PdfObject ParseValueAt(string text, int start)
        {
            if (start >= text.Length) return null;

            if (string.CompareOrdinal(text, start, "<<", 0, 2) == 0)
            {
                Dictionary<string, PdfObject> dict = ParseDictAt(start);
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
                    o.Length = dict.ContainsKey("Length") ? AsInt(dict["Length"]) : -1;
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

        Dictionary<string, PdfObject> ParseDictAt(int start)
        {
            string text = Latin1(data);
            int end = SkipDict(text, start);
            string body = text.Substring(start + 2, Math.Max(0, end - start - 4));
            return ParseDictBody(body);
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
                string key = body.Substring(nameStart, i - nameStart);

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
                        return PdfObject.Name(s.Substring(start, i - start));
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
                        string raw = s.Substring(start, Math.Max(0, i - start));
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
                        return PdfObject.Array(s.Substring(start, Math.Max(0, i - start)));
                    }
                case '<':
                    {
                        if (i + 1 < s.Length && s[i + 1] == '<')
                        {
                            int end = SkipDict(s, i);
                            next = end;
                            return PdfObject.DictObj(ParseDictBody(
                                s.Substring(i + 2, Math.Max(0, end - i - 4))));
                        }
                        int start = i + 1;
                        int e = s.IndexOf('>', start);
                        if (e < 0) e = s.Length;
                        next = Math.Min(s.Length, e + 1);
                        return PdfObject.Str(s.Substring(start, e - start));
                    }
                default:
                    {
                        // true / false / null / 演算子などは読み飛ばす
                        int start = i;
                        while (i < s.Length && !IsDelimiterOrWs(s[i])) i++;
                        next = i;
                        return PdfObject.Name(s.Substring(start, i - start));
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

        // ---------------- ページの内容ストリーム ----------------

        /// <summary>全ページの内容ストリーム(辞書付き)を順に返す。</summary>
        public IEnumerable<PdfObject> PageContents()
        {
            List<PdfObject> result = new();
            HashSet<int> seen = new();

            if (rootRef >= 0)
            {
                PdfObject catalog = GetObject(rootRef);
                if (catalog != null && catalog.Dict != null
                    && catalog.Dict.ContainsKey("Pages"))
                {
                    CollectPages(RefOf(catalog.Dict["Pages"]), seen, result);
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
                    PdfObject c = Resolve(o.Dict["Contents"]);
                    if (c != null) result.Add(c);
                }
            }
            return result;
        }

        void CollectPages(int num, HashSet<int> seen, List<PdfObject> result)
        {
            if (num < 0 || seen.Contains(num)) return;
            seen.Add(num);

            PdfObject node = GetObject(num);
            if (node == null || node.Dict == null) return;

            PdfObject type = node.Dict.ContainsKey("Type") ? node.Dict["Type"] : null;

            if (type != null && type.NameValue == "Page")
            {
                if (node.Dict.ContainsKey("Contents"))
                {
                    PdfObject c = Resolve(node.Dict["Contents"]);
                    if (c != null) result.Add(c);
                }
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

                        CollectPages(childNum, seen, result);
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

            string text = Latin1(data);

            int start = o.StreamStart;
            int len = o.Length;
            int end;
            if (len >= 0 && start + len <= text.Length) end = start + len;
            else
            {
                // /Length が無い/壊れている場合は endstream を探す
                end = text.IndexOf("endstream", start, StringComparison.Ordinal);
                if (end < 0) return null;
            }

            byte[] raw = new byte[Math.Max(0, end - start)];
            Array.Copy(data, start, raw, 0, raw.Length);

            string filter = FilterName(o);
            if (filter == "FlateDecode")
            {
                try
                {
                    using MemoryStream ms = new(raw);
                    using ZLibStream z = new(ms, CompressionMode.Decompress);
                    using MemoryStream outMs = new();
                    z.CopyTo(outMs);
                    return Latin1(outMs.ToArray());
                }
                catch
                {
                    // 一部の PDF は zlib ヘッダ無しの raw deflate
                    try
                    {
                        using MemoryStream ms = new(raw);
                        using DeflateStream d = new(ms, CompressionMode.Decompress);
                        using MemoryStream outMs = new();
                        d.CopyTo(outMs);
                        return Latin1(outMs.ToArray());
                    }
                    catch { return null; }
                }
            }
            if (filter == null || filter == "")
                return Latin1(raw);
            return null;   // 未対応の圧縮
        }

        static string FilterName(PdfObject o)
        {
            if (o.Dict == null || !o.Dict.ContainsKey("Filter")) return null;
            PdfObject f = o.Dict["Filter"];
            if (f == null) return null;
            if (f.IsName) return f.NameValue;
            if (f.IsArray && f.ArrayBody != null)
            {
                int i = f.ArrayBody.IndexOf('/');
                if (i >= 0)
                {
                    int start = i + 1;
                    int end = start;
                    while (end < f.ArrayBody.Length && !IsDelimiterOrWs(f.ArrayBody[end])) end++;
                    return f.ArrayBody.Substring(start, end - start);
                }
            }
            return null;
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
            if (string.IsNullOrEmpty(content)) return "";

            StringBuilder sb = new();
            List<string> operands = new();

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
                    operands.Add(s);
                    continue;
                }

                if (c == '<')
                {
                    if (i + 1 < content.Length && content[i + 1] == '<')
                    {
                        // 辞書は読み飛ばす
                        i += 2;
                        int depth = 1;
                        while (i < content.Length - 1 && depth > 0)
                        {
                            if (content[i] == '<' && content[i + 1] == '<') { depth++; i += 2; }
                            else if (content[i] == '>' && content[i + 1] == '>') { depth--; i += 2; }
                            else i++;
                        }
                        continue;
                    }
                    string hex = ReadHex(content, ref i);
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
                            arr.Append(ReadLiteral(content, ref i));
                        }
                        else if (content[i] == '<')
                        {
                            arr.Append(ReadHex(content, ref i));
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

                // 演算子を読む
                int opStart = i;
                while (i < content.Length && !char.IsWhiteSpace(content[i])
                       && content[i] != '(' && content[i] != '<' && content[i] != '['
                       && content[i] != ')' && content[i] != '>' && content[i] != ']'
                       && content[i] != '/')
                    i++;

                if (i == opStart) { i++; continue; }
                string op = content.Substring(opStart, i - opStart);

                switch (op)
                {
                    case "Tj":
                        if (operands.Count > 0)
                        {
                            sb.Append(operands[operands.Count - 1]);
                        }
                        operands.Clear();
                        break;
                    case "TJ":
                        if (operands.Count > 0)
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
            // s[i] == '<'
            i++;
            StringBuilder hex = new();
            while (i < s.Length && s[i] != '>')
            {
                if (!char.IsWhiteSpace(s[i])) hex.Append(s[i]);
                i++;
            }
            if (i < s.Length) i++;

            // UTF-16BE (BOM 付き) か、1 バイト文字かを判定する
            byte[] bytes = HexToBytes(hex.ToString());
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
