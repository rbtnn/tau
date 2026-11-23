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
using System.Net.Security;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tau
{
    static class LlmClient
    {
        // 処理中のリクエスト(Ctrl+C で中断するため)
        static volatile CancellationTokenSource currentCts;

        // 使い回す HttpClient(接続プールを共有する)
        static HttpClient client;
        static readonly object clientLock = new object();

        // 設定が変わったら作り直す
        static string clientKey = "";

        // ---- デバッグ出力 ----
        // 環境変数 TAU_DEBUG=1 で有効になる。既定は無効(通常利用では出さない)。
        static bool debugOn = (Environment.GetEnvironmentVariable("TAU_DEBUG") ?? "") == "1";

        public static bool DebugOn { get { return debugOn; } }

        public static void EnableDebug() { debugOn = true; }

        public static void Dbg(string msg)
        {
            if (!debugOn) return;
            Console.Error.WriteLine("[debug] " + msg);
        }

        // 例外の連鎖を 1 行にまとめる(型名とメッセージ)
        public static string ExceptionChain(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 6)
            {
                if (depth > 0) sb.Append("\n           <- ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // ---- TLS ----

        static readonly object tlsLock = new object();
        static bool tlsApplied;

        // 実行環境の TLS 関連情報をまとめる(デバッグ用)
        public static string DescribeEnvironment()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.Append("runtime=").Append(Environment.Version);
                sb.Append(" mono=").Append(Type.GetType("Mono.Runtime") != null ? "yes" : "no");
                sb.Append(" os=").Append(Environment.OSVersion);
            }
            catch { }
            try
            {
                sb.Append(" TLS=").Append(CurrentTls);
            }
            catch { }
            try
            {
                sb.Append(" rti=").Append(
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            }
            catch { }
            return sb.ToString();
        }

        // 使う TLS バージョン。
        // TLS 1.2 以上を明示する。.NET 10 の既定は OS に任せる形なので、
        // 古い TLS 1.0/1.1 しか使わないサーバ以外はこれで足りる。
        public static SslProtocols PreferredTls
        {
            get { return SslProtocols.Tls12 | SslProtocols.Tls13; }
        }

        // HttpClient のハンドラに TLS の設定を反映する。
        public static void ApplyTlsTo(SocketsHttpHandler handler)
        {
            try
            {
                handler.SslOptions.EnabledSslProtocols = PreferredTls;
                handler.SslOptions.RemoteCertificateValidationCallback =
                    (sender, cert, chain, errors) =>
                    {
                        return (errors == SslPolicyErrors.None);
                    };
            }
            catch (Exception ex)
            {
                Dbg("TLS: failed to apply: " + ex.Message);
            }
        }

        // 互換用: 何もしない(設定はハンドラ単位で行う)
        public static void ApplyTls()
        {
            lock (tlsLock)
            {
                tlsApplied = true;
            }
        }

        static string SafeCurrentTls()
        {
            return PreferredTls.ToString() + "(" + (int)PreferredTls + ")";
        }

        // 現在の TLS 設定(エラー表示用)
        public static string CurrentTls { get { return SafeCurrentTls(); } }

        // TLS 1.2 の適用に成功したか
        public static bool TlsApplied { get { lock (tlsLock) { return tlsApplied; } } }

        // TLS を再設定できるか(呼び出し側は同じ要求を 1 度だけ再試行する)。
        //
        // Tls12 固定なので「上げる段」は無い。ただし要求前に ApplyTls() が
        // 呼ばれていない可能性があるため、未適用なら 1 度だけ再試行を許す。
        public static bool EscalateTls()
        {
            lock (tlsLock)
            {
                if (tlsApplied) return false;   // すでに適用済みなら再試行しない
                ApplyTls();
                return true;
            }
        }

        // TLS ハンドシェイク失敗かどうか
        public static bool IsTlsFailure(Exception ex)
        {
            if (ex == null) return false;

            // 証明書の問題は TLS バージョンの話ではないので除外する
            if (ex is System.Security.Authentication.AuthenticationException)
            {
                string m0 = ex.Message ?? "";
                if (m0.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0
                    || m0.IndexOf("証明書", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                return true;
            }

            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 4)
            {
                string n = e.GetType().FullName ?? "";
                if (n.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("SslStream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Tls", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                if (e is ArgumentException)
                {
                    string m = e.Message ?? "";
                    if (m.IndexOf("SslProtocol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.IndexOf("sslProtocolType", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                e = e.InnerException;
                depth++;
            }
            return false;
        }

        public static void Cancel()
        {
            CancellationTokenSource c = currentCts;
            if (c != null)
            {
                try { c.Cancel(); } catch { }
            }
        }

        // HttpClient を(設定に応じて)用意する。
        // 設定が変わったら作り直す。
        static HttpClient GetClient(Config cfg)
        {
            string key = (cfg.ApiBaseUrl ?? "") + "|" + cfg.UseSystemProxy + "|"
                + (cfg.ProxyUrl ?? "") + "|" + (cfg.ProxyUser ?? "");

            lock (clientLock)
            {
                if (client != null && clientKey == key) return client;

                if (client != null)
                {
                    try { client.Dispose(); } catch { }
                    client = null;
                }

                SocketsHttpHandler handler = new SocketsHttpHandler();

                // TLS の設定
                ApplyTlsTo(handler);

                // プロキシ設定
                try
                {
                    ProxyHelper.ApplyHttp(handler, cfg);
                }
                catch (Exception ex)
                {
                    Dbg("proxy setup failed: " + ex.Message);
                }

                // リダイレクトは追わない(API は 3xx を返さない)
                handler.AllowAutoRedirect = false;

                // 接続を使い回す
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30);

                // 応答をバッファしない(SSE を逐次読むため)
                handler.ResponseDrainTimeout = TimeSpan.FromSeconds(2);

                client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(180);

                client.DefaultRequestHeaders.UserAgent.ParseAdd("tau/1.0");
                clientKey = key;
                return client;
            }
        }

        // リクエストを組み立てる
        static HttpRequestMessage BuildRequest(Config cfg, byte[] payload)
        {
            ApplyTls();

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiBaseUrl);
            req.Content = new ByteArrayContent(payload);
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (!string.IsNullOrEmpty(cfg.SecretKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.SecretKey);

            if (DebugOn)
            {
                Dbg("--- request settings ---");
                Dbg("  URL      : " + cfg.ApiBaseUrl);
                Dbg("  Model    : " + cfg.Model);
                Dbg("  TLS      : " + CurrentTls);
                Dbg("  Proxy    : " + DescribeProxy(cfg));
                Dbg("  Body     : " + payload.Length + " bytes");
                Dbg("----------------------");
            }
            return req;
        }

        // プロキシ設定を 1 行で表す
        static string DescribeProxy(Config cfg)
        {
            if (!cfg.UseSystemProxy && string.IsNullOrEmpty(cfg.ProxyUrl))
                return "direct connection";
            if (string.IsNullOrEmpty(cfg.ProxyUrl))
                return "system default";
            return cfg.ProxyUrl;
        }

        // WebException の内容を、原因が分かる形に整形する
        public static string DescribeError(Exception ex)
        {
            if (ex == null) return "";

            HttpRequestException hex = ex as HttpRequestException;
            if (hex != null && hex.StatusCode != null)
                return hex.Message;   // ステータスと本文は生成時に含めている

            StringBuilder sb = new StringBuilder();
            sb.Append(ex.Message);

            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 3)
            {
                sb.Append(" <- ").Append(inner.Message);
                inner = inner.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // API に送るメッセージから、保存専用メタデータ(ts/steps/think)を取り除く
        static List<object> ApiMessages(List<object> messages)
        {
            List<object> apiMessages = new();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) { apiMessages.Add(o); continue; }
                Dictionary<string, object> c = new();
                foreach (KeyValuePair<string, object> kv in m)
                    if (kv.Key != "ts" && kv.Key != "steps" && kv.Key != "think") c[kv.Key] = kv.Value;
                apiMessages.Add(c);
            }
            return apiMessages;
        }

        static Dictionary<string, object> BuildBody(Config cfg, List<object> messages, bool useTools, bool stream)
        {
            Dictionary<string, object> body = new();
            body["model"] = cfg.Model;
            body["messages"] = ApiMessages(messages);
            body["temperature"] = 0.7;
            if (stream) body["stream"] = true;
            if (useTools && cfg.AllowTools)
            {
                body["tools"] = ToolExecutor.Definitions();
                body["tool_choice"] = "auto";
            }
            return body;
        }

        // ---- 非ストリーミング呼び出し(要約など) ----
        public static LlmResp CallOnce(Config cfg, List<object> messages, bool useTools)
        {
            const int MAX_ATTEMPTS = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return CallOnceInternal(cfg, messages, useTools);
                }
                catch (WebException wex)
                {
                    Dbg("WebException(CallOnce): status=" + wex.Status
                        + " (attempt " + attempt + "/" + MAX_ATTEMPTS + ")");

                    if (!IsRetryable(wex) || attempt >= MAX_ATTEMPTS) throw;

                    if (IsTlsFailure(wex) && EscalateTls())
                        ConsoleUi.Info("Changing the TLS setting and retrying...");
                    else
                        ConsoleUi.Info("Retrying the connection (" + attempt + "/" + (MAX_ATTEMPTS - 1) + ")...");

                    System.Threading.Thread.Sleep(500 * attempt);
                }
            }
        }

        static LlmResp CallOnceInternal(Config cfg, List<object> messages, bool useTools)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, false);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpClient c = GetClient(cfg);
            CancellationTokenSource cts = new CancellationTokenSource();
            currentCts = cts;

            string responseBody;
            try
            {
                using (HttpRequestMessage req = BuildRequest(cfg, payload))
                using (HttpResponseMessage resp = c.Send(req, HttpCompletionOption.ResponseContentRead,
                                                          cts.Token))
                {
                    responseBody = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                    {
                        string preview = responseBody ?? "";
                        if (preview.Length > 400) preview = preview.Substring(0, 400) + "...";
                        throw new HttpRequestException(
                            "LLM API error: HTTP " + (int)resp.StatusCode
                            + " " + resp.ReasonPhrase + " " + preview,
                            null, resp.StatusCode);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new LlmCancelledException();
            }
            finally
            {
                currentCts = null;
                try { cts.Dispose(); } catch { }
            }

            return ParseOnce(responseBody);
        }

        static LlmResp ParseOnce(string responseBody)
        {
            object parsed = Json.Parse(responseBody);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null) throw new Exception("LLM API returned a malformed response");

            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                if (ed != null && ed.ContainsKey("message"))
                    throw new Exception("LLM API error: " + Json.AsString(ed["message"]));
                throw new Exception("LLM API error: " + Json.ToString(root["error"]));
            }

            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>) ||
                ((List<object>)root["choices"]).Count == 0)
                throw new Exception("LLM API response has no choices");

            Dictionary<string, object> first =
                ((List<object>)root["choices"])[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message"))
                throw new Exception("LLM API response has no message");

            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null) throw new Exception("LLM API response has no message");

            LlmResp r = new LlmResp();
            r.Content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (msg.ContainsKey("reasoning_content")) r.Reasoning = Json.AsString(msg["reasoning_content"]);
            else if (msg.ContainsKey("reasoning")) r.Reasoning = Json.AsString(msg["reasoning"]);

            if (root.ContainsKey("usage") && root["usage"] is Dictionary<string, object>)
            {
                Dictionary<string, object> u = (Dictionary<string, object>)root["usage"];
                if (u.ContainsKey("total_tokens")) r.TotalTokens = Json.AsInt(u["total_tokens"]);
            }
            return r;
        }

        // ---- ストリーミング呼び出し ----
        class StreamAcc
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        // TLS ハンドシェイクに失敗したら、設定を一段上げて 1 度だけ再試行する。
        // (SystemDefault が効かない環境では TLS 1.2 を明示すると通ることが多い)
        public static LlmResp CallStream(Config cfg, List<object> messages, bool useTools,
                                         Action<string> contentCb, Action<string> reasoningCb)
        {
            // TLS / 接続の一時的な失敗は再試行する。
            // SecureChannelFailure は一時的なことも多いため、
            // TLS 設定を変えられなくても数回は試す。
            const int MAX_ATTEMPTS = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return CallStreamOnce(cfg, messages, useTools, contentCb, reasoningCb);
                }
                catch (Exception ex)
                {
                    // 中止は再試行しない
                    if (ex is LlmCancelledException) throw;

                    bool retryable = IsRetryable(ex);
                    Dbg("attempt " + attempt + "/" + MAX_ATTEMPTS
                        + " failed: " + ex.GetType().Name + " retryable=" + retryable);
                    Dbg("  chain: " + ExceptionChain(ex));

                    if (!retryable || attempt >= MAX_ATTEMPTS) throw;

                    if (IsTlsFailure(ex) && EscalateTls())
                        ConsoleUi.Info("Changing the TLS setting and retrying...");
                    else
                        ConsoleUi.Info("Retrying the connection (" + attempt + "/" + (MAX_ATTEMPTS - 1) + ")...");

                    // 少し待ってから再試行する(指数バックオフ)
                    System.Threading.Thread.Sleep(500 * attempt);
                }
            }
        }

        // 再試行する価値のある失敗か。
        //  - 接続系の一時的な失敗 → 再試行する
        //  - HTTP エラー(401/429/500 など) → 再試行しても無駄なのでしない
        public static bool IsRetryable(Exception ex)
        {
            if (ex == null) return false;
            if (ex is LlmCancelledException) return false;

            // HTTP ステータスが返っている場合は再試行しない
            HttpRequestException hex = ex as HttpRequestException;
            if (hex != null && hex.StatusCode != null) return false;

            if (ex is HttpRequestException) return true;
            if (ex is System.Net.Sockets.SocketException) return true;
            if (ex is IOException) return true;
            if (ex is TaskCanceledException) return true;   // タイムアウト
            if (ex is System.Security.Authentication.AuthenticationException) return true;

            // 内部例外も見る
            Exception e = ex.InnerException;
            int depth = 0;
            while (e != null && depth < 4)
            {
                if (e is System.Net.Sockets.SocketException) return true;
                if (e is IOException) return true;
                if (e is TaskCanceledException) return true;
                e = e.InnerException;
                depth++;
            }
            return false;
        }

        static LlmResp CallStreamOnce(Config cfg, List<object> messages, bool useTools,
                                      Action<string> contentCb, Action<string> reasoningCb)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, true);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpClient c = GetClient(cfg);
            CancellationTokenSource cts = new CancellationTokenSource();
            currentCts = cts;

            StringBuilder content = new StringBuilder();
            Dictionary<int, StreamAcc> acc = new();

            try
            {
                Dbg("connecting: " + cfg.ApiBaseUrl);
                using (HttpRequestMessage req = BuildRequest(cfg, payload))
                using (HttpResponseMessage resp = c.Send(req,
                    HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    Dbg("sent (" + payload.Length + " bytes), waiting for response...");

                    if (DebugOn)
                    {
                        Dbg("response: HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                        Dbg("  ContentType  : " + resp.Content.Headers.ContentType);
                        Dbg("  ContentLength: " + resp.Content.Headers.ContentLength);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        string errBody = resp.Content.ReadAsStringAsync(cts.Token)
                            .GetAwaiter().GetResult();
                        if (errBody == null) errBody = "";
                        if (errBody.Length > 400) errBody = errBody.Substring(0, 400) + "...";
                        throw new HttpRequestException(
                            "LLM API error: HTTP " + (int)resp.StatusCode
                            + " " + resp.ReasonPhrase + " " + errBody,
                            null, resp.StatusCode);
                    }

                    string ctype = resp.Content.Headers.ContentType == null
                        ? "" : resp.Content.Headers.ContentType.ToString();

                    using (Stream rs = resp.Content.ReadAsStream(cts.Token))
                    using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                    {
                        if (ctype.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            // プロキシ等が stream:true を無視して通常の JSON を返した場合
                            string bodyText = sr.ReadToEnd();
                            ParseNonStreamJson(bodyText, content, acc, cfg, contentCb, reasoningCb);
                        }
                        else
                        {
                            bool sawDone = false;   // [DONE] を受け取ったか(正常終了の目印)
                            int lineNo = 0;
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                lineNo++;
                                if (DebugOn && lineNo <= 5)
                                    Dbg("SSE[" + lineNo + "]: "
                                        + (line.Length > 160 ? line.Substring(0, 160) + "..." : line));
                                if (line.Length == 0) continue;
                                if (!line.StartsWith("data:")) continue;
                                string data = line.Substring(5).Trim();
                                if (data == "[DONE]") { sawDone = true; break; }

                                object parsed;
                                try { parsed = Json.Parse(data); }
                                catch { continue; }

                                Dictionary<string, object> root = parsed as Dictionary<string, object>;
                                if (root == null || !root.ContainsKey("choices") ||
                                    !(root["choices"] is List<object>)) continue;
                                List<object> choices = (List<object>)root["choices"];
                                if (choices.Count == 0) continue;
                                Dictionary<string, object> ch = choices[0] as Dictionary<string, object>;
                                if (ch == null || !ch.ContainsKey("delta")) continue;
                                Dictionary<string, object> delta = ch["delta"] as Dictionary<string, object>;
                                if (delta == null) continue;

                                if (delta.ContainsKey("content") && delta["content"] != null)
                                {
                                    string t = Json.AsString(delta["content"]);
                                    if (t.Length > 0)
                                    {
                                        content.Append(t);
                                        if (contentCb != null) contentCb(t);
                                    }
                                }
                                if (cfg.EmitReasoning)
                                {
                                    string rt = DeltaText(delta, "reasoning_content");
                                    if (rt.Length == 0) rt = DeltaText(delta, "reasoning");
                                    if (rt.Length > 0 && reasoningCb != null) reasoningCb(rt);
                                }
                                if (delta.ContainsKey("function_call") && delta["function_call"] is Dictionary<string, object>)
                                {
                                    Dictionary<string, object> fc = (Dictionary<string, object>)delta["function_call"];
                                    StreamAcc cur;
                                    if (!acc.ContainsKey(0)) { cur = new StreamAcc(); acc[0] = cur; }
                                    else cur = acc[0];
                                    if (fc.ContainsKey("name") && fc["name"] != null) cur.Name += Json.AsString(fc["name"]);
                                    if (fc.ContainsKey("arguments") && fc["arguments"] != null) cur.Args.Append(Json.AsString(fc["arguments"]));
                                }
                                if (delta.ContainsKey("tool_calls") && delta["tool_calls"] is List<object>)
                                {
                                    foreach (object tco in (List<object>)delta["tool_calls"])
                                    {
                                        Dictionary<string, object> tc = tco as Dictionary<string, object>;
                                        if (tc == null) continue;
                                        int idx = tc.ContainsKey("index") ? Json.AsInt(tc["index"]) : acc.Count;
                                        StreamAcc cur;
                                        if (!acc.ContainsKey(idx)) { cur = new StreamAcc(); acc[idx] = cur; }
                                        else cur = acc[idx];
                                        if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                                        Dictionary<string, object> fn =
                                            (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
                                        if (fn != null)
                                        {
                                            if (fn.ContainsKey("name") && fn["name"] != null) cur.Name += Json.AsString(fn["name"]);
                                            if (fn.ContainsKey("arguments") && fn["arguments"] != null) cur.Args.Append(Json.AsString(fn["arguments"]));
                                        }
                                    }
                                }
                            }

                            Dbg("SSE read finished: lines=" + lineNo + " sawDone=" + sawDone
                                + " contentLen=" + content.Length);

                            // [DONE] を受け取らずにストリームが終わった場合は、
                            // 応答が途中で切れている(通信断・サーバ側のタイムアウト等)。
                            // 黙って途中までの本文を確定させない。
                            if (!sawDone && content.Length > 0)
                            {
                                throw new Exception("LLM API (stream) error: the response was cut off"
                                    + " (" + content.Length + " characters received)."
                                    + " Please try again.");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new LlmCancelledException();
            }
            catch (HttpRequestException)
            {
                // 接続断・TLS 失敗など。再試行の判断ができるようそのまま投げる。
                throw;
            }
            catch (IOException iex)
            {
                // ストリーム読み取り中の切断(「受信時に予期しないエラー」など)
                throw new Exception("LLM API (stream) error: the connection was lost: " + iex.Message
                    + " (" + content.Length + " characters received)"
                    + ". A network or proxy timeout, or the server aborting the response, is likely.");
            }
            finally
            {
                currentCts = null;
                try { cts.Dispose(); } catch { }
            }

            List<object> toolCalls = new();
            List<int> idxs = new List<int>(acc.Keys);
            idxs.Sort();
            foreach (int ix in idxs)
            {
                StreamAcc sa = acc[ix];
                if (sa.Name.Length == 0) continue;
                Dictionary<string, object> fn = new();
                fn["name"] = sa.Name;
                fn["arguments"] = sa.Args.ToString();
                Dictionary<string, object> call = new();
                call["id"] = sa.Id.Length > 0 ? sa.Id : "call_" + ix;
                call["type"] = "function";
                call["function"] = fn;
                toolCalls.Add(call);
            }

            LlmResp r = new LlmResp();
            r.Content = content.ToString();
            r.ToolCalls = (toolCalls.Count > 0) ? toolCalls : null;
            return r;
        }

        static string DeltaText(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.ContainsKey(key)) return "";
            object v = delta[key];
            if (v is string) return (string)v;
            return "";
        }

        // 非SSE(通常の JSON)応答を解釈する。stream:true が無視された場合のフォールバック。
        static void ParseNonStreamJson(string body, StringBuilder contentOut,
                                       Dictionary<int, StreamAcc> acc, Config cfg,
                                       Action<string> contentCb, Action<string> reasoningCb)
        {
            if (body == null || body.Length == 0) return;
            object o;
            try { o = Json.Parse(body); }
            catch { return; }

            Dictionary<string, object> root = o as Dictionary<string, object>;
            if (root == null) return;
            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                throw new Exception("LLM API error: "
                    + (ed != null && ed.ContainsKey("message") ? Json.AsString(ed["message"]) : Json.ToString(root["error"])));
            }
            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>)) return;
            List<object> choices = (List<object>)root["choices"];
            if (choices.Count == 0) return;
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message")) return;
            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null) return;

            string content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (content.Length > 0)
            {
                contentOut.Append(content);
                if (contentCb != null) contentCb(content);
            }
            if (cfg.EmitReasoning)
            {
                string rc = "";
                if (msg.ContainsKey("reasoning_content")) rc = Json.AsString(msg["reasoning_content"]);
                else if (msg.ContainsKey("reasoning")) rc = Json.AsString(msg["reasoning"]);
                if (rc.Length > 0 && reasoningCb != null) reasoningCb(rc);
            }

            if (msg.ContainsKey("tool_calls") && msg["tool_calls"] is List<object>)
            {
                List<object> tcs = (List<object>)msg["tool_calls"];
                for (int i = 0; i < tcs.Count; i++)
                {
                    Dictionary<string, object> tc = tcs[i] as Dictionary<string, object>;
                    if (tc == null) continue;
                    StreamAcc cur = new StreamAcc();
                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                    Dictionary<string, object> fn =
                        (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
                    if (fn != null)
                    {
                        if (fn.ContainsKey("name") && fn["name"] != null) cur.Name = Json.AsString(fn["name"]);
                        if (fn.ContainsKey("arguments"))
                        {
                            object av = fn["arguments"];
                            cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                        }
                    }
                    acc[i] = cur;
                }
            }
            else if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
            {
                Dictionary<string, object> fc = (Dictionary<string, object>)msg["function_call"];
                StreamAcc cur = new StreamAcc();
                cur.Name = fc.ContainsKey("name") ? Json.AsString(fc["name"]) : "";
                if (fc.ContainsKey("arguments"))
                {
                    object av = fc["arguments"];
                    cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                }
                acc[0] = cur;
            }
        }
    }
}
