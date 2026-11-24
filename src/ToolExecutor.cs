// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  agentweb (Web UI 版) とは独立した実装です。
//  コードは agentweb を参考にしていますが、共通化はしていません。
//  HTTP サーバ / Web UI を持たず、標準入出力だけで対話します。
//
//  ビルド: csc.exe /nologo /out:tau.exe src\*.cs
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tau
{
    // =======================================================================
    //  ツール実行 (Function Calling)
    // =======================================================================
    static class ToolExecutor
    {
        const int MAX_OUTPUT = 32000;   // ツール結果として LLM に返す最大文字数

        internal static int MaxOutputForTests { get { return MAX_OUTPUT; } }

        // read_document が 1 回で返す文字数。MAX_OUTPUT より小さくして、
        // JSON 全体(キー・エスケープ)を含めてもコンテキストに収める。
        const int CHUNK_SIZE = 16000;

        internal static int ChunkSizeForTests { get { return CHUNK_SIZE; } }

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key)) return d[key];
            return null;
        }

        internal static string NormalizeNamePublic(string name) { return NormalizeName(name); }

        static string NormalizeName(string name)
        {
            if (name == null) return "";
            switch (name.Trim().ToLowerInvariant())
            {
                case "shell":
                case "cmd":
                case "execute":
                case "run":
                case "exec":
                case "run_command":
                case "execute_command":
                case "command":
                    return "shell";
                case "read_file":
                case "read":
                case "cat":
                case "readfile":
                    return "read_file";
                case "write_file":
                case "write":
                case "writefile":
                    return "write_file";
                case "list_dir":
                case "ls":
                case "dir":
                case "list_directory":
                case "listdir":
                    return "list_dir";
                case "curl":
                case "http_request":
                case "http":
                case "fetch":
                    return "curl";
                case "read_document":
                case "read_pdf":
                case "read_docx":
                case "read_xlsx":
                case "document":
                case "extract_text":
                    return "read_document";
                default:
                    return name;
            }
        }

        // プロキシが function.name を返さない場合に、引数の形からツール名を推定する
        public static string InferName(Dictionary<string, object> fn, string argsJson)
        {
            if (fn != null && fn.ContainsKey("name"))
            {
                string nm = Json.AsString(fn["name"]);
                if (nm != null && nm.Trim().Length > 0) return NormalizeName(nm);
            }
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new();

            if (args.ContainsKey("cmd")) return "shell";
            if (args.ContainsKey("sheet")) return "read_document";
            if (args.ContainsKey("content") && args.ContainsKey("path")) return "write_file";
            if (args.ContainsKey("pattern")) return "list_dir";
            if (args.ContainsKey("path")) return "read_file";
            return "shell";
        }

        public static string Execute(string name, string argsJson, Config cfg)
        {
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new();

            if (name == null || name.Trim().Length == 0) name = InferName(null, argsJson);
            name = NormalizeName(name);

            switch (name)
            {
                case "shell":
                    return Shell(Json.AsString(Get(args, "cmd")), cfg.ToolTimeoutSec);
                case "read_file":
                    return ReadFile(Json.AsString(Get(args, "path")));
                case "write_file":
                    return WriteFile(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "content")));
                case "list_dir":
                    return ListDir(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "pattern")));
                case "curl":
                    return Curl(cfg, Json.AsString(Get(args, "url")),
                        Json.AsString(Get(args, "method")),
                        args.ContainsKey("headers") ? args["headers"] : null,
                        Json.AsString(Get(args, "body")));
                case "read_document":
                    return ReadDocument(Json.AsString(Get(args, "path")),
                        Json.AsString(Get(args, "sheet")),
                        Json.AsInt(Get(args, "maxRows")),
                        Json.AsInt(Get(args, "offset")),
                        Json.AsInt(Get(args, "limit")));
                case "memory_read":
                    return MemoryRead(cfg);
                case "memory_write":
                    return MemoryWrite(Json.AsString(Get(args, "text")), cfg);
                case "memory_append":
                    return MemoryAppend(Json.AsString(Get(args, "text")), cfg);
                case "memory_clear":
                    return MemoryClear(cfg);
                default:
                    return Err("unknown tool: " + name
                        + " (available: shell/read_file/write_file/list_dir/curl"
                        + "/read_document"
                        + "/memory_read/memory_write/memory_append/memory_clear)");
            }
        }

        // ---------------- ツール定義 ----------------

        static Dictionary<string, object> Func(string name, string desc,
                                               string[] required, string[][] props)
        {
            Dictionary<string, object> f = new();
            f["name"] = name;
            f["description"] = desc;

            Dictionary<string, object> params_ = new();
            params_["type"] = "object";
            Dictionary<string, object> p = new();
            for (int i = 0; i < props.Length; i++)
            {
                Dictionary<string, object> d = new();
                d["type"] = props[i][1];
                d["description"] = props[i][2];
                p[props[i][0]] = d;
            }
            params_["properties"] = p;
            params_["required"] = new List<object>(required);
            f["parameters"] = params_;

            Dictionary<string, object> tool = new();
            tool["type"] = "function";
            tool["function"] = f;
            return tool;
        }

        public static List<object> Definitions()
        {
            List<object> tools = new();

            tools.Add(Func("shell",
                "Run a command on the local machine. "
                + "Returns stdout, stderr and the exit code. "
                + "Use it for any shell command, including file and directory operations "
                + "and running programs.",
                new string[] { "cmd" },
                new string[][] {
                    new string[] { "cmd", "string", "Command line to run (e.g. dir /b)" }
                }));

            tools.Add(Func("read_file",
                "Read the contents of a file as text.",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "Path of the file to read" }
                }));

            tools.Add(Func("write_file",
                "Write text to the given path, overwriting any existing file.",
                new string[] { "path", "content" },
                new string[][] {
                    new string[] { "path", "string", "Path of the file to write" },
                    new string[] { "content", "string", "Text to write" }
                }));

            tools.Add(Func("read_document",
                "Read the text of a PDF, Word (.docx) or Excel (.xlsx) file. "
                + "No Office installation is needed; the file is parsed directly. "
                + "For Excel, every sheet is returned with cells separated by tabs. "
                + "Use it instead of read_file for these formats. "
                + "Long documents are returned in chunks: when the result says "
                + "truncated=true, call it again with the nextOffset value to read the rest.",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "Path of the .pdf, .docx or .xlsx file" },
                    new string[] { "sheet", "string",
                        "Excel only: the sheet name to read. Defaults to all sheets" },
                    new string[] { "maxRows", "integer",
                        "Excel only: maximum rows per sheet. Defaults to 200" },
                    new string[] { "offset", "integer",
                        "Start position in characters. Use nextOffset from the previous result to continue" },
                    new string[] { "limit", "integer",
                        "Maximum characters to return. Defaults to 16000" }
                }));

            tools.Add(Func("list_dir",
                "List the files and subdirectories in a directory.",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "Directory to list" },
                    new string[] { "pattern", "string",
                        "Glob pattern to filter files (e.g. *.txt). Defaults to *" }
                }));

            tools.Add(Func("curl",
                "Send an HTTP/HTTPS request and return the response. "
                + "Uses the configured proxy when one is set.",
                new string[] { "url" },
                new string[][] {
                    new string[] { "url", "string", "URL to request" },
                    new string[] { "method", "string",
                        "HTTP method (e.g. GET, POST). Defaults to POST when a body is given, otherwise GET" },
                    new string[] { "headers", "object", "Additional HTTP headers" },
                    new string[] { "body", "string", "Request body" }
                }));

            // ---- メモリ ----
            //
            // システムプロンプトとは別に、セッションをまたいで残したい
            // 覚え書きを読み書きする。容量には上限がある。
            tools.Add(Func("memory_read",
                "Read your saved memory. The memory is shared across all sessions "
                + "and is already included in your system prompt, but you can call "
                + "this to see the exact current contents and remaining capacity.",
                new string[] { },
                new string[][] { }));

            tools.Add(Func("memory_write",
                "Replace your entire saved memory with new text. Use it to record "
                + "facts worth keeping across sessions (user preferences, project "
                + "conventions, decisions). Keep it short; it has a size limit.",
                new string[] { "text" },
                new string[][] {
                    new string[] { "text", "string", "The new memory contents (replaces everything)" }
                }));

            tools.Add(Func("memory_append",
                "Append a line to your saved memory, keeping what is already there. "
                + "Prefer this over memory_write when adding a new note.",
                new string[] { "text" },
                new string[][] {
                    new string[] { "text", "string", "Text to append (a newline is added)" }
                }));

            tools.Add(Func("memory_clear",
                "Erase your saved memory. Only do this when the user asks for it.",
                new string[] { },
                new string[][] { }));

            return tools;
        }

        // ---------------- 各ツールの実装 ----------------

        static string Shell(string cmd, int timeoutSec)
        {
            if (cmd == null || cmd.Trim().Length == 0) return Err("cmd is empty");
            try
            {
                bool win = Path.DirectorySeparatorChar == '\\';
                // /bin/sh の -c はコマンド全体を一つの引数として受け取る。
                // cmd.exe は独自の引用符規則を持つため従来のコマンドラインを保つ。
                ProcessStartInfo psi = win
                    ? new ProcessStartInfo("cmd.exe", "/c " + cmd)
                    : new ProcessStartInfo("/bin/sh");
                if (!win)
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(cmd);
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using Process p = Process.Start(psi);
                System.Threading.Tasks.Task<byte[]> so = CopyToBytesAsync(p.StandardOutput.BaseStream);
                System.Threading.Tasks.Task<byte[]> se = CopyToBytesAsync(p.StandardError.BaseStream);

                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch { }
                    return Err("timed out after " + timeoutSec + "s: " + cmd);
                }
                p.WaitForExit();

                Dictionary<string, object> d = new();
                d["ok"] = true;
                d["exitCode"] = p.ExitCode;
                d["stdout"] = Clip(DecodeBytes(so.Result));
                d["stderr"] = Clip(DecodeBytes(se.Result));
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("shell failed: " + ex.Message);
            }
        }

        static string ReadFile(string path)
        {
            if (path == null || path.Length == 0) return Err("path is not specified");
            try
            {
                if (!File.Exists(path)) return Err("file does not exist: " + path);
                Dictionary<string, object> d = new();
                d["ok"] = true;
                d["content"] = Clip(DecodeBytes(File.ReadAllBytes(path)));
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("read_file failed: " + ex.Message);
            }
        }

        static string WriteFile(string path, string content)
        {
            if (path == null || path.Length == 0) return Err("path is not specified");
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, content == null ? "" : content, Encoding.UTF8);
                Dictionary<string, object> d = new();
                d["ok"] = true;
                d["written"] = (content == null ? "" : content).Length;
                d["path"] = Path.GetFullPath(path);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("write_file failed: " + ex.Message);
            }
        }

        // PDF / Word / Excel を、COM を使わずに読む。
        static string ReadDocument(string path, string sheet, int maxRows,
                                   int offset, int limit)
        {
            if (path == null || path.Trim().Length == 0) return Err("path is not specified");
            path = path.Trim();

            if (!File.Exists(path)) return Err("file does not exist: " + path);

            string kind = OfficeReader.Kind(path);
            if (kind.Length == 0)
            {
                return Err("unsupported file type: " + Path.GetExtension(path)
                    + " (read_document handles .pdf, .docx and .xlsx)");
            }
            if (kind == "legacy")
            {
                return Err("the old binary format (" + Path.GetExtension(path)
                    + ") is not supported. Open it once in Office and save it as "
                    + "PDF, .docx or .xlsx.");
            }

            string text = null;
            string error = null;
            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["path"] = Path.GetFullPath(path);
            d["type"] = kind;

            switch (kind)
            {
                case "pdf":
                    text = PdfReader.ReadText(path, out error);
                    d["pages"] = PdfReader.CountPages(path);
                    break;
                case "word":
                    text = OfficeReader.ReadWord(path, out error);
                    break;
                case "excel":
                    if (maxRows <= 0) maxRows = 200;
                    text = OfficeReader.ReadExcel(path, sheet, maxRows, out error);
                    d["maxRows"] = maxRows;
                    break;
                default:
                    return Err("read_document does not support " + kind + " files yet");
            }

            if (text == null)
            {
                return Err(error ?? "could not read the document");
            }

            int total = text.Length;
            d["length"] = total;

            // 大きな文書は分割して返す。1 回で全部を返すと、
            // コンテキストを溢れさせるため。offset を変えて続きを読む。
            int start = offset > 0 ? offset : 0;
            if (start > total) start = total;

            int len = limit > 0 ? limit : CHUNK_SIZE;
            if (len > MAX_OUTPUT) len = MAX_OUTPUT;
            if (start + len > total) len = total - start;

            d["offset"] = start;
            d["returned"] = len;
            d["content"] = text.Substring(start, len);

            bool more = start + len < total;
            d["truncated"] = more;
            if (more)
            {
                d["nextOffset"] = start + len;
                d["hint"] = "The document is longer than one result. Call "
                    + "read_document again with offset=" + (start + len)
                    + " to read the rest.";
            }
            return Json.ToString(d);
        }

        static string ListDir(string path, string pattern)
        {
            if (path == null || path.Length == 0) path = ".";
            if (pattern == null || pattern.Length == 0) pattern = "*";
            try
            {
                StringBuilder list = new StringBuilder();
                List<object> entries = new();

                string[] dirs = Directory.GetDirectories(path);
                foreach (string den in dirs)
                {
                    string name = Path.GetFileName(den);
                    list.Append("[DIR ]  ").Append(name).Append("/\n");
                    Dictionary<string, object> e = new();
                    e["type"] = "dir"; e["name"] = name; e["path"] = den;
                    entries.Add(e);
                }

                string[] files = Directory.GetFiles(path, pattern);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    long size = new FileInfo(f).Length;
                    list.Append("[FILE]  ").Append(name).Append("  ").Append(HumanSize(size)).Append("\n");
                    Dictionary<string, object> e = new();
                    e["type"] = "file"; e["name"] = name; e["size"] = size; e["path"] = f;
                    entries.Add(e);
                }

                Dictionary<string, object> d = new();
                d["ok"] = true;
                d["path"] = Path.GetFullPath(path);
                d["total"] = dirs.Length + files.Length;
                d["list"] = list.ToString();
                d["entries"] = entries;
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("list_dir failed: " + ex.Message);
            }
        }

        static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        static string Curl(Config cfg, string url, string method, object headersObj, string body)
        {
            if (url == null || url.Trim().Length == 0) return Err("url is not specified");
            if (method == null || method.Trim().Length == 0)
                method = (body != null && body.Length > 0) ? "POST" : "GET";
            method = method.Trim().ToUpperInvariant();

            try
            {
                LlmClient.ApplyTls();   // HTTPS 先でも TLS 1.2 以上を使えるようにする

                SocketsHttpHandler handler = new SocketsHttpHandler();
                handler.AllowAutoRedirect = false;
                ProxyHelper.ApplyHttp(handler, cfg);

                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(cfg.ToolTimeoutSec);

                    using (HttpRequestMessage req =
                           new HttpRequestMessage(new HttpMethod(method), url.Trim()))
                    {
                        Dictionary<string, object> headers = headersObj as Dictionary<string, object>;
                        if (headers != null)
                        {
                            foreach (KeyValuePair<string, object> kv in headers)
                            {
                                string v = Json.AsString(kv.Value);
                                // Content-Type はコンテンツ側のヘッダ
                                if (String.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                req.Headers.TryAddWithoutValidation(kv.Key, v);
                            }
                        }

                        if (body != null && body.Length > 0)
                        {
                            byte[] data = Encoding.UTF8.GetBytes(body);
                            req.Content = new ByteArrayContent(data);

                            string ctype = "application/json";
                            if (headers != null)
                            {
                                foreach (KeyValuePair<string, object> kv in headers)
                                    if (String.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                                        ctype = Json.AsString(kv.Value);
                            }
                            req.Content.Headers.ContentType =
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctype);
                        }

                        using (HttpResponseMessage resp = client.Send(req,
                            HttpCompletionOption.ResponseContentRead))
                        {
                            byte[] raw = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            string respBody = DecodeBytes(raw);

                            Dictionary<string, object> d = new();
                            d["ok"] = true;
                            d["status"] = (int)resp.StatusCode;
                            d["url"] = url.Trim();
                            d["body"] = Clip(respBody);
                            return Json.ToString(d);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Err("curl failed: " + ex.Message);
            }
        }

        // ---------------- 文字コード判定 ----------------

        // エラー応答の本文読み取り用に外部から使えるようにしたもの
        public static string DecodeBytesPublic(byte[] data) { return DecodeBytes(data); }
        public static byte[] ReadAllBytesPublic(Stream s) { return ReadAllBytes(s); }

        static string DecodeBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try
            {
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                    return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
                if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                    return Encoding.Unicode.GetString(data, 2, data.Length - 2);
                if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            }
            catch { }
            try
            {
                var u8 = new UTF8Encoding(false, true);
                return u8.GetString(data);
            }
            catch (DecoderFallbackException) { }
            try
            {
                int cp = Console.OutputEncoding.CodePage;
                if (cp > 0 && cp != 65001) return Encoding.GetEncoding(cp).GetString(data);
            }
            catch { }
            try { return Encoding.Default.GetString(data); } catch { }
            try { return Encoding.GetEncoding(28591).GetString(data); } catch { }
            return Encoding.ASCII.GetString(data);
        }

        static System.Threading.Tasks.Task<byte[]> CopyToBytesAsync(Stream s)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            });
        }

        static byte[] ReadAllBytes(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string Clip(string s)
        {
            if (s == null) return "";
            if (s.Length <= MAX_OUTPUT) return s;

            // 上限を超えたら切り詰める。結果全体が上限を超えないよう、
            // 案内文の分を差し引いて切る。
            string note = "\n...[output truncated: the result was longer than "
                + MAX_OUTPUT + " characters]";
            int keep = MAX_OUTPUT - note.Length;
            if (keep < 0) keep = 0;
            return s.Substring(0, keep) + note;
        }

        // ---------------- メモリ ----------------

        static string MemoryRead(Config cfg)
        {
            string text = Memory.Load();
            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["memory"] = text;
            d["usage"] = Memory.Usage(text, cfg);
            return Json.ToString(d);
        }

        static string MemoryWrite(string text, Config cfg)
        {
            string error;
            if (!Memory.Save(text, cfg, out error)) return Err(error);

            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["usage"] = Memory.Usage(text, cfg);
            return Json.ToString(d);
        }

        static string MemoryAppend(string text, Config cfg)
        {
            if (string.IsNullOrWhiteSpace(text)) return Err("text is empty");

            string current = Memory.Load();
            StringBuilder sb = new(current);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            sb.Append(text.Trim());

            string error;
            if (!Memory.Save(sb.ToString(), cfg, out error)) return Err(error);

            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["usage"] = Memory.Usage(sb.ToString(), cfg);
            return Json.ToString(d);
        }

        static string MemoryClear(Config cfg)
        {
            string error;
            if (!Memory.Clear(cfg, out error)) return Err(error);

            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["usage"] = Memory.Usage("", cfg);
            return Json.ToString(d);
        }

        static string Err(string message)
        {
            Dictionary<string, object> d = new();
            d["error"] = message;
            return Json.ToString(d);
        }
    }
}
