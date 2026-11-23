// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  --web で起動したときの HTTP サーバ。
//  緊急用の最小構成で、できるのは次の 4 つだけ:
//
//    GET  /                     画面(index.html)
//    GET  /api/sessions         セッション一覧
//    GET  /api/sessions/{id}    1 セッションの履歴
//    POST /api/chat/stream      チャット(SSE で逐次返す)
//
//  設定の参照・変更はしない。tau-cli の設定(tau.json)をそのまま使う。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Tau
{
    // =======================================================================
    //  ブラウザに返す SSE のイベント名
    // =======================================================================
    static class WebEvents
    {
        public const string Think = "think";     // 思考(reasoning)
        public const string Tool = "tool";       // ツール実行
        public const string Delta = "delta";     // 本文の差分
        public const string Done = "done";       // 正常終了
        public const string Error = "error";     // エラー
        public const string Log = "log";         // ConsoleUi からの補助表示
    }

    // =======================================================================
    //  Web サーバ本体
    // =======================================================================
    class WebServer
    {
        readonly Config cfg;
        readonly SessionStore store = new();

        HttpListener listener;
        volatile bool running;

        // セッションの読み書きは複数タブから同時に来るので保護する。
        readonly object sessionLock = new();

        // 同時に 1 ターンだけ実行する(二重送信を防ぐ)。
        readonly object turnLock = new();
        volatile bool busy;
        volatile string busySession = "";

        public int Port { get; private set; }
        public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

        public WebServer(Config cfg)
        {
            this.cfg = cfg;
        }

        // ---------------- 起動 / 停止 ----------------

        public void Start(int port)
        {
            listener = new HttpListener();
            // ループバック限定。Windows でも URL ACL が要らない。
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // ポートが埋まっているときは別の空きポートで開く。
                listener = new HttpListener();
                port = FreePort();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex2)
                {
                    throw new Exception("Could not start the web server on 127.0.0.1:" + port
                        + " (" + ex2.Message + "). Original error: " + ex.Message);
                }
            }
            Port = port;
            running = true;

            Thread t = new Thread(AcceptLoop) { IsBackground = true, Name = "tau-web-accept" };
            t.Start();
        }

        static int FreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
        }

        void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    if (running) continue;
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (!running) break;
                    continue;
                }

                Thread t = new Thread(() => Handle(ctx)) { IsBackground = true };
                t.Start();
            }
        }

        // ---------------- ルーティング ----------------

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                string path = (req.Url == null) ? "/" : req.Url.AbsolutePath;
                string method = req.HttpMethod ?? "GET";

                // 別サイトからの書き込みを防ぐ(ブラウザで開いたページからのみ許可)
                if (method == "POST" && !OriginAllowed(req))
                {
                    SendJson(res, 403, ErrorJson("forbidden", "cross-origin request refused"));
                    return;
                }

                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    SendHtml(res, LoadIndexHtml());
                    return;
                }

                if (method == "GET" && path == "/api/sessions")
                {
                    SendJson(res, 200, Json.ToString(SessionsJson()));
                    return;
                }

                if (method == "GET" && path.StartsWith("/api/sessions/"))
                {
                    string id = Uri.UnescapeDataString(path.Substring("/api/sessions/".Length));
                    HandleGetSession(res, id);
                    return;
                }

                if (method == "POST" && path == "/api/chat/stream")
                {
                    HandleChatStream(ctx);
                    return;
                }

                if (method == "GET" && path == "/api/health")
                {
                    SendJson(res, 200, Json.ToString(HealthJson()));
                    return;
                }

                SendJson(res, 404, ErrorJson("not_found", "no such endpoint: " + path));
            }
            catch (Exception ex)
            {
                try { SendJson(res, 500, ErrorJson("internal", ex.Message)); } catch { }
            }
        }

        bool OriginAllowed(HttpListenerRequest req)
        {
            string origin = req.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;   // curl 等
            return origin.Contains("127.0.0.1") || origin.Contains("localhost");
        }

        // ---------------- 画面 ----------------

        static string LoadIndexHtml()
        {
            Assembly asm = typeof(WebServer).Assembly;
            // 埋め込みリソース名は RootNamespace とパスから決まる。
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("www.index.html", StringComparison.Ordinal))
                {
                    using Stream s = asm.GetManifestResourceStream(name);
                    if (s == null) break;
                    using StreamReader r = new(s, Encoding.UTF8);
                    return r.ReadToEnd();
                }
            }
            return "<!doctype html><meta charset=\"utf-8\"><h1>tau</h1>"
                 + "<p>index.html is missing from this build.</p>";
        }

        // ---------------- セッション一覧 ----------------

        Dictionary<string, object> SessionsJson()
        {
            List<object> list;
            lock (sessionLock)
            {
                list = store.List();
            }

            // 更新日時の新しい順
            list.Sort((a, b) =>
            {
                string ta = (a as Dictionary<string, object>)?["updatedAt"] is string x ? x : "";
                string tb = (b as Dictionary<string, object>)?["updatedAt"] is string y ? y : "";
                return tb.CompareTo(ta);
            });

            List<object> view = new();
            foreach (object o in list)
            {
                Dictionary<string, object> s = o as Dictionary<string, object>;
                if (s == null) continue;
                Dictionary<string, object> v = new();
                v["id"] = Json.AsString(s.ContainsKey("id") ? s["id"] : "");
                v["title"] = Json.AsString(s.ContainsKey("title") ? s["title"] : "");
                v["updatedAt"] = Json.AsString(s.ContainsKey("updatedAt") ? s["updatedAt"] : "");
                v["count"] = Json.AsInt(s.ContainsKey("count") ? s["count"] : 0);
                view.Add(v);
            }

            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["sessions"] = view;
            d["busy"] = busy;
            d["busySession"] = busySession;
            return d;
        }

        // ---------------- 1 セッションの履歴 ----------------

        void HandleGetSession(HttpListenerResponse res, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                SendJson(res, 400, ErrorJson("bad_request", "session id is required"));
                return;
            }

            Dictionary<string, object> meta;
            lock (sessionLock)
            {
                meta = store.Load(id);
            }
            if (meta == null)
            {
                SendJson(res, 404, ErrorJson("not_found", "no such session: " + id));
                return;
            }

            List<object> view = new();
            if (meta.ContainsKey("messages") && meta["messages"] is List<object> msgs)
            {
                foreach (object o in msgs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    string role = Json.AsString(m.ContainsKey("role") ? m["role"] : "");

                    // ツール結果は画面に出さない
                    if (role == "tool") continue;

                    string content = Json.AsString(m.ContainsKey("content") ? m["content"] : "");
                    bool hasToolCalls = m.ContainsKey("tool_calls");
                    // ツール呼び出しだけの assistant メッセージは本文が空なので飛ばす
                    if (role == "assistant" && (hasToolCalls || content.Length == 0)) continue;

                    Dictionary<string, object> mv = new();
                    mv["role"] = role;
                    mv["content"] = content;
                    if (m.ContainsKey("think")) mv["think"] = Json.AsString(m["think"]);
                    if (m.ContainsKey("steps") && m["steps"] is List<object>) mv["steps"] = m["steps"];
                    view.Add(mv);
                }
            }

            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["id"] = id;
            d["title"] = Json.AsString(meta.ContainsKey("title") ? meta["title"] : "");
            d["messages"] = view;
            SendJson(res, 200, Json.ToString(d));
        }

        Dictionary<string, object> HealthJson()
        {
            Dictionary<string, object> d = new();
            d["ok"] = true;
            d["busy"] = busy;
            d["busySession"] = busySession;
            return d;
        }

        // ---------------- チャット (SSE) ----------------

        void HandleChatStream(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;

            string message = "";
            string sessionId = "";
            try
            {
                if (req.HasEntityBody)
                {
                    using StreamReader r = new(req.InputStream, Encoding.UTF8);
                    string body = r.ReadToEnd();
                    if (Json.Parse(body) is Dictionary<string, object> m)
                    {
                        message = Json.AsString(m.ContainsKey("message") ? m["message"] : "");
                        sessionId = Json.AsString(m.ContainsKey("session") ? m["session"] : "");
                    }
                }
            }
            catch (Exception ex)
            {
                SendJson(res, 400, ErrorJson("bad_request", "invalid JSON body: " + ex.Message));
                return;
            }

            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.SendChunked = true;
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("X-Accel-Buffering", "no");
            Stream outStream = res.OutputStream;

            void Emit(string ev, string data)
            {
                try
                {
                    byte[] b = Encoding.UTF8.GetBytes("event: " + ev + "\ndata: " + data + "\n\n");
                    outStream.Write(b, 0, b.Length);
                    outStream.Flush();
                }
                catch { /* ブラウザが切った */ }
            }

            if (message.Trim().Length == 0)
            {
                Emit(WebEvents.Error, ErrorJson("bad_request", "message is required"));
                Emit(WebEvents.Done, "{}");
                CloseQuietly(res);
                return;
            }

            if (busy)
            {
                Emit(WebEvents.Error, ErrorJson("busy", "another turn is running"));
                Emit(WebEvents.Done, "{}");
                CloseQuietly(res);
                return;
            }

            if (sessionId.Trim().Length == 0) sessionId = store.NewId();

            // セッションを読み込む(無ければ新規として扱う)
            SessionCtx sctx = new();
            sctx.Id = sessionId;
            sctx.Title = "New conversation";
            lock (sessionLock)
            {
                Dictionary<string, object> meta = store.Load(sessionId);
                if (meta != null)
                {
                    sctx.Title = Json.AsString(meta.ContainsKey("title") ? meta["title"] : "New conversation");
                    sctx.Summary = Json.AsString(meta.ContainsKey("summary") ? meta["summary"] : "");
                    sctx.ContextStart = Json.AsInt(meta.ContainsKey("contextStart") ? meta["contextStart"] : 0);
                    if (meta.ContainsKey("messages") && meta["messages"] is List<object> msgs)
                        sctx.Messages = msgs;
                }
            }

            bool locked = false;
            try
            {
                Monitor.TryEnter(turnLock, 0, ref locked);
                if (!locked)
                {
                    Emit(WebEvents.Error, ErrorJson("busy", "another turn is running"));
                    Emit(WebEvents.Done, "{}");
                    CloseQuietly(res);
                    return;
                }

                busy = true;
                busySession = sessionId;

                // タイトルは最初の発言から作る(コンソール版と同じ規則)
                if (sctx.Title == "New conversation" || sctx.Title.Trim().Length == 0)
                    sctx.Title = ShortTitle(message);

                // ConsoleUi の出力を SSE の log イベントへ回す
                ConsoleUi.SetSink(txt => Emit(WebEvents.Log, LogJson(txt)), false);

                Agent agent = new(cfg);
                string reply = agent.RunStream(
                    sctx,
                    message,
                    c => { lock (sessionLock) { Save(c); } },
                    d => Emit(WebEvents.Delta, TextJson(d)),
                    d => Emit(WebEvents.Think, TextJson(d)),
                    (name, detail) =>
                    {
                        Dictionary<string, object> t = new();
                        t["name"] = name;
                        t["detail"] = detail;
                        Emit(WebEvents.Tool, Json.ToString(t));
                    },
                    () => false);   // 中止はしない(最小構成)

                lock (sessionLock) { Save(sctx); }

                Dictionary<string, object> done = new();
                done["session"] = sctx.Id;
                done["title"] = sctx.Title;
                done["reply"] = reply ?? "";
                Emit(WebEvents.Done, Json.ToString(done));
            }
            catch (Exception ex)
            {
                Emit(WebEvents.Error, ErrorJson("llm_error", LlmClient.DescribeError(ex)));
                Emit(WebEvents.Done, "{}");
            }
            finally
            {
                ConsoleUi.ResetSink();
                busy = false;
                busySession = "";
                if (locked) Monitor.Exit(turnLock);
                CloseQuietly(res);
            }
        }

        void Save(SessionCtx c)
        {
            store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
        }

        static string ShortTitle(string message)
        {
            string t = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return "New conversation";
            if (t.Length > 40) t = t.Substring(0, 40) + "…";
            return t;
        }

        // ---------------- 補助 ----------------

        static string TextJson(string s)
        {
            return Json.ToString(new Dictionary<string, object> { ["t"] = s ?? "" });
        }

        static string LogJson(string s)
        {
            // ConsoleUi は ANSI エスケープを付けない設定なので、そのまま渡せる
            return Json.ToString(new Dictionary<string, object> { ["t"] = s ?? "" });
        }

        static string ErrorJson(string code, string message)
        {
            Dictionary<string, object> d = new();
            d["ok"] = false;
            d["code"] = code;
            d["message"] = message ?? "";
            return Json.ToString(d);
        }

        static void SendJson(HttpListenerResponse res, int status, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = b.Length;
            try
            {
                res.OutputStream.Write(b, 0, b.Length);
            }
            catch { }
            CloseQuietly(res);
        }

        static void SendHtml(HttpListenerResponse res, string html)
        {
            byte[] b = Encoding.UTF8.GetBytes(html);
            res.StatusCode = 200;
            res.ContentType = "text/html; charset=utf-8";
            res.ContentLength64 = b.Length;
            try
            {
                res.OutputStream.Write(b, 0, b.Length);
            }
            catch { }
            CloseQuietly(res);
        }

        static void CloseQuietly(HttpListenerResponse res)
        {
            try { res.OutputStream.Close(); } catch { }
            try { res.Close(); } catch { }
        }
    }
}
