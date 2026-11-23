using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Tau;

namespace Tau.Tests
{
    /// <summary>
    /// テスト用の OpenAI 互換モックサーバ。
    /// test_tau.py が Python の HTTPServer でやっていたことを C# で行う。
    /// </summary>
    public sealed class MockLlm : IDisposable
    {
        readonly HttpListener listener;
        readonly Thread thread;
        volatile bool running;

        /// <summary>受け取ったリクエストボディ(検証用)。</summary>
        public List<Dictionary<string, object>> Requests { get; } = new();

        /// <summary>ポート番号。</summary>
        public int Port { get; }

        /// <summary>chat/completions の URL。</summary>
        public string Url { get { return "http://127.0.0.1:" + Port + "/v1/chat/completions"; } }

        /// <summary>
        /// 応答を組み立てる関数。
        /// 引数はリクエストの JSON、戻り値は SSE で流す delta の並び。
        /// </summary>
        public Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>> Reply;

        /// <summary>HTTP ステータスを強制的に変える(エラー試験用)。0 なら 200。</summary>
        public int ForceStatus;

        /// <summary>レスポンス本文をそのまま返す(エラー試験用)。null なら通常。</summary>
        public string ForceBody;

        /// <summary>途中で接続を切る(切断試験用)。</summary>
        public bool CutOff;

        public MockLlm()
        {
            Port = FreePort();
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            listener.Start();
            running = true;
            thread = new Thread(AcceptLoop) { IsBackground = true };
            thread.Start();
        }

        static int FreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { if (!running) break; continue; }

                try { Handle(ctx); }
                catch { }
                finally
                {
                    try { ctx.Response.OutputStream.Close(); } catch { }
                    try { ctx.Response.Close(); } catch { }
                }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            string body = "";
            try
            {
                using StreamReader r = new(ctx.Request.InputStream, Encoding.UTF8);
                body = r.ReadToEnd();
            }
            catch { }

            Dictionary<string, object> req = null;
            try { req = Json.Parse(body) as Dictionary<string, object>; } catch { }
            if (req != null)
            {
                lock (Requests) { Requests.Add(req); }
            }

            HttpListenerResponse res = ctx.Response;

            if (ForceStatus != 0 || ForceBody != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(ForceBody ?? "{}");
                res.StatusCode = ForceStatus != 0 ? ForceStatus : 200;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = b.Length;
                res.OutputStream.Write(b, 0, b.Length);
                return;
            }

            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.SendChunked = true;
            Stream o = res.OutputStream;

            IEnumerable<Dictionary<string, object>> deltas;
            try
            {
                deltas = Reply != null
                    ? Reply(req ?? new Dictionary<string, object>())
                    : DefaultReply(req ?? new Dictionary<string, object>());
            }
            catch
            {
                deltas = new List<Dictionary<string, object>>();
            }

            foreach (Dictionary<string, object> d in deltas)
            {
                if (CutOff) return;   // 接続を切る(OutputStream は呼び出し側で閉じる)

                Dictionary<string, object> chunk = new();
                chunk["id"] = "c";
                chunk["object"] = "chat.completion.chunk";
                chunk["model"] = "m";
                List<object> choices = new();
                Dictionary<string, object> choice = new();
                choice["index"] = 0;
                choice["delta"] = d;
                choice["finish_reason"] = null;
                choices.Add(choice);
                chunk["choices"] = choices;

                byte[] b = Encoding.UTF8.GetBytes(
                    "data: " + Json.ToString(chunk) + "\n\n");
                o.Write(b, 0, b.Length);
                o.Flush();
            }

            if (!CutOff)
            {
                byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                o.Write(done, 0, done.Length);
                o.Flush();
            }
        }

        /// <summary>既定の応答: 本文を 1 チャンクで返す。</summary>
        public static IEnumerable<Dictionary<string, object>> DefaultReply(
            Dictionary<string, object> req)
        {
            List<Dictionary<string, object>> list = new();
            list.Add(Delta("content", "OK"));
            return list;
        }

        /// <summary>本文だけを返す応答を作る。</summary>
        public static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            Text(string text)
        {
            return req =>
            {
                List<Dictionary<string, object>> list = new();
                list.Add(Delta("content", text));
                return list;
            };
        }

        /// <summary>思考と本文を返す応答を作る。</summary>
        public static Func<Dictionary<string, object>, IEnumerable<Dictionary<string, object>>>
            ThinkThenText(string think, string text)
        {
            return req =>
            {
                List<Dictionary<string, object>> list = new();
                list.Add(Delta("reasoning_content", think));
                list.Add(Delta("content", text));
                return list;
            };
        }

        static Dictionary<string, object> Delta(string key, string value)
        {
            Dictionary<string, object> d = new();
            d[key] = value;
            return d;
        }

        /// <summary>ツール呼び出しの delta を作る。</summary>
        public static Dictionary<string, object> ToolCall(string id, string name, string argsJson,
                                                          int index = 0)
        {
            List<object> calls = new();
            Dictionary<string, object> c = new();
            c["index"] = index;
            c["id"] = id;
            c["type"] = "function";
            Dictionary<string, object> fn = new();
            fn["name"] = name;
            fn["arguments"] = argsJson;
            c["function"] = fn;
            calls.Add(c);

            Dictionary<string, object> d = new();
            d["tool_calls"] = calls;
            return d;
        }

        /// <summary>リクエストに含まれる role=tool のメッセージ数。</summary>
        public static int ToolResultCount(Dictionary<string, object> req)
        {
            if (req == null || !req.ContainsKey("messages")) return 0;
            if (!(req["messages"] is List<object> msgs)) return 0;
            int n = 0;
            foreach (object o in msgs)
            {
                if (o is Dictionary<string, object> m
                    && m.ContainsKey("role")
                    && Json.AsString(m["role"]) == "tool") n++;
            }
            return n;
        }

        /// <summary>受け取ったリクエストを消す。</summary>
        public void ClearRequests()
        {
            lock (Requests) { Requests.Clear(); }
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }
}
