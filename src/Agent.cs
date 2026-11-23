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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tau
{
    class Agent
    {
        const int MAX_TURNS = 10;   // 1 ターンのツール反復上限

        // ツール反復が上限に達したとき、コンテキストを整理して続行する回数。
        // 上限に達したら即エラーにすると、長い作業が途中で切れてしまう。
        const int MAX_COMPACT_ROUNDS = 2;

        readonly Config cfg;

        public Agent(Config cfg)
        {
            this.cfg = cfg;
        }

        // ---------------- メッセージ生成 ----------------

        static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new();
            m["role"] = role;
            m["content"] = content;
            m["ts"] = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
            return m;
        }

        // ツール実行記録を添えたメッセージ(tau-web と同じ形式で保存する)
        // 注意: 4 引数版へ委譲すると相互再帰で StackOverflow になるため、ここで組み立てる。
        static Dictionary<string, object> Msg(string role, string content, List<object> steps)
        {
            Dictionary<string, object> m = Msg(role, content);
            if (steps != null && steps.Count > 0) m["steps"] = steps;
            return m;
        }

        static Dictionary<string, object> Msg(string role, string content, List<object> steps, string think)
        {
            Dictionary<string, object> m = Msg(role, content);
            if (steps != null && steps.Count > 0) m["steps"] = steps;
            if (think != null && think.Trim().Length > 0) m["think"] = think;
            return m;
        }

        static Dictionary<string, object> ToolMsg(string id, string name, string result)
        {
            Dictionary<string, object> m = new();
            m["role"] = "tool";
            m["tool_call_id"] = id;
            m["name"] = name;
            m["content"] = result;
            return m;
        }

        static Dictionary<string, object> AsstWithToolCalls(LlmResp resp)
        {
            Dictionary<string, object> asst = new();
            asst["role"] = "assistant";
            if (resp.Content != null && resp.Content.Length > 0) asst["content"] = resp.Content;
            asst["tool_calls"] = resp.ToolCalls;
            return asst;
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out object value) ? value : null;
        }

        static string ThinkOf(StringBuilder sb)
        {
            if (sb == null || sb.Length == 0) return null;
            return sb.ToString();
        }

        // ---------------- コンパクション ----------------

        static long TokensOf(object o)
        {
            if (o == null) return 0;
            try { return Json.ToString(o).Length / 2 + 1; }
            catch { return 0; }
        }

        static string ComposeSystem(string systemPrompt, string summary)
        {
            if (summary == null || summary.Trim().Length == 0) return systemPrompt;
            return systemPrompt + "\n\n[Summary of the conversation so far]\n" + summary;
        }

        static List<object> BuildEffectiveMessages(Config cfg, SessionCtx ctx)
        {
            List<object> eff = new();
            // メモリは全セッション共通。システムプロンプトに含めて渡す。
            string sys = ComposeSystem(cfg.SystemPrompt, ctx.Summary);
            eff.Add(Msg("system", Memory.Compose(sys, Memory.Load())));
            if (ctx.ContextStart < 0) ctx.ContextStart = 0;
            for (int i = ctx.ContextStart; i < ctx.Messages.Count; i++)
                eff.Add(ctx.Messages[i]);
            return eff;
        }

        // ターン境界: user または最終回答の assistant のみ切ってよい
        static bool IsValidCutStart(object o)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            if (m == null) return false;
            string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
            if (role == "tool") return false;
            if (role == "user") return true;
            if (role == "assistant")
            {
                if (m.ContainsKey("tool_calls") && m["tool_calls"] != null) return false;
                string c = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                return c.Trim().Length > 0;
            }
            return false;
        }

        static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length > max) return s.Substring(0, max) + "...";
            return s;
        }

        static string RenderTranscript(List<object> span)
        {
            StringBuilder sb = new StringBuilder();
            foreach (object o in span)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                if (role == "system") continue;

                if (role == "user") sb.Append("User: ").Append(content).Append("\n");
                else if (role == "tool")
                {
                    string nn = m.ContainsKey("name") ? Json.AsString(m["name"]) : "";
                    sb.Append("[Tool result] ").Append(nn).Append(": ")
                      .Append(Clip(Json.AsString(m["content"]), 500)).Append("\n");
                }
                else if (role == "assistant")
                {
                    if (m.ContainsKey("tool_calls") && m["tool_calls"] is List<object>)
                    {
                        foreach (object tc in (List<object>)m["tool_calls"])
                        {
                            Dictionary<string, object> c = tc as Dictionary<string, object>;
                            Dictionary<string, object> fn = Get(c, "function") as Dictionary<string, object>;
                            string nm = (fn != null) ? Json.AsString(Get(fn, "name")) : "";
                            string ar = (fn != null) ? Json.AsString(Get(fn, "arguments")) : "";
                            sb.Append("[Tool call] ").Append(nm).Append(" args=")
                              .Append(Clip(ar, 300)).Append("\n");
                        }
                    }
                    else sb.Append("Assistant: ").Append(content).Append("\n");
                }
            }
            return sb.ToString();
        }

        static string Summarize(Config cfg, string prevSummary, List<object> span)
        {
            try
            {
                List<object> body = new();
                body.Add(Msg("system",
                    "You are an assistant that specialises in summarising chat history."
                    + " Produce a concise bullet-point summary that can be reused later as context,"
                    + " keeping the decisions made, the operations performed and their results,"
                    + " the user's requests, file paths and important facts."
                    + " Omit any preamble such as 'Here is the summary'; output only the summary itself."));

                StringBuilder ub = new StringBuilder();
                if (prevSummary != null && prevSummary.Trim().Length > 0)
                    ub.Append("[Previous summary]\n").Append(prevSummary).Append("\n\n");
                ub.Append("[Conversation to summarise now]\n").Append(RenderTranscript(span));
                body.Add(Msg("user", ub.ToString()));

                LlmResp resp = LlmClient.CallOnce(cfg, body, false);   // 要約用はツール不要
                if (resp.Content != null && resp.Content.Trim().Length > 0) return resp.Content.Trim();
                return prevSummary;
            }
            catch
            {
                return prevSummary;
            }
        }

        // トークン超過を検知したら、最近分だけ残し古い分を要約して置換する
        void MaybeCompact(Config cfg, SessionCtx ctx)
        {
            if (ctx.ContextStart >= ctx.Messages.Count) return;
            long threshold = cfg.ContextWindow - cfg.ReserveTokens;
            if (threshold <= 0) return;

            long active = TokensOf(Msg("system", ComposeSystem(cfg.SystemPrompt, ctx.Summary)));
            for (int i = ctx.ContextStart; i < ctx.Messages.Count; i++) active += TokensOf(ctx.Messages[i]);

            if (active <= threshold && ctx.LastTotalTokens <= threshold) return;

            long kept = 0;
            int cut = ctx.Messages.Count;
            for (int i = ctx.Messages.Count - 1; i >= 0; i--)
            {
                long t = TokensOf(ctx.Messages[i]);
                if (kept + t > cfg.KeepRecentTokens) { cut = i; break; }
                kept += t;
                cut = i;
            }
            while (cut < ctx.Messages.Count && !IsValidCutStart(ctx.Messages[cut])) cut++;
            if (cut <= ctx.ContextStart) return;
            if (cut >= ctx.Messages.Count) return;

            List<object> span = ctx.Messages.GetRange(ctx.ContextStart, cut - ctx.ContextStart);
            ConsoleUi.Info("Compacting context (summarising)...");
            ctx.Summary = Summarize(cfg, ctx.Summary, span);
            ctx.ContextStart = cut;
            ctx.LastTotalTokens = 0;
        }

        // しきい値に関係なく、必ずコンテキストを整理する。
        // ツール反復が上限に達したときに使う。古い部分を要約して
        // ContextStart を進め、直近の KeepRecentTokens だけを残す。
        void ForceCompact(Config cfg, SessionCtx ctx)
        {
            if (ctx.ContextStart >= ctx.Messages.Count) return;

            long kept = 0;
            int cut = ctx.Messages.Count;
            for (int i = ctx.Messages.Count - 1; i >= 0; i--)
            {
                long t = TokensOf(ctx.Messages[i]);
                if (kept + t > cfg.KeepRecentTokens) { cut = i; break; }
                kept += t;
                cut = i;
            }
            while (cut < ctx.Messages.Count && !IsValidCutStart(ctx.Messages[cut])) cut++;

            // ツール呼び出しが続くと、適切な切断点が末尾まで進んでしまい
            // 何も要約できない。その場合は直近の 2 組だけを残し、
            // それより前をまとめて要約する。
            if (cut >= ctx.Messages.Count)
            {
                // ツール呼び出しが続くと適切な切断点が無い。
                // その場合は「後ろから N 件を残す」という方針に切り替え、
                // 残す範囲の先頭で切る。要約対象が空にならないことを優先する。
                const int keepTail = 4;   // 直近のメッセージ数
                cut = ctx.Messages.Count - keepTail;
                if (cut <= ctx.ContextStart) cut = ctx.ContextStart + 1;

                // それでも末尾に達するなら、先頭 1 件だけを要約する
                if (cut >= ctx.Messages.Count) cut = ctx.ContextStart + 1;
            }

            if (cut <= ctx.ContextStart) return;
            if (cut >= ctx.Messages.Count) return;

            List<object> span = ctx.Messages.GetRange(ctx.ContextStart, cut - ctx.ContextStart);
            ctx.Summary = Summarize(cfg, ctx.Summary, span);
            ctx.ContextStart = cut;
            ctx.LastTotalTokens = 0;
        }

        // ---------------- エージェント実行 ----------------

        // 戻り値: 最終回答。emit 系のコールバックでストリーミング表示する。
        public string RunStream(SessionCtx ctx, string userMessage,
                                Action<SessionCtx> save,
                                Action<string> onContent,
                                Action<string> onReasoning,
                                Action<string, string> onTool,
                                Func<bool> cancelled)
        {
            ctx.Messages.Add(Msg("user", userMessage));
            List<object> turnSteps = new();
            StringBuilder turnThink = new StringBuilder();

            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx);

            try
            {
                // ツール反復が上限に達したら、コンテキストを整理して
                // 続きを実行させる。整理は MAX_COMPACT_ROUNDS 回まで。
                for (int compacted = 0; compacted <= MAX_COMPACT_ROUNDS; compacted++)
                {
                    for (int turn = 0; turn < MAX_TURNS; turn++)
                    {
                        if (cancelled != null && cancelled())
                        {
                            ctx.Messages.Add(Msg("assistant", "⚠ Cancelled.", turnSteps, ThinkOf(turnThink)));
                            return "⚠ Cancelled.";
                        }

                        List<object> eff = BuildEffectiveMessages(cfg, ctx);
                        LlmResp resp;
                        try
                        {
                            resp = LlmClient.CallStream(cfg, eff, cfg.AllowTools,
                                (d) => { if (onContent != null) onContent(d); },
                                (d) => { turnThink.Append(d); if (onReasoning != null) onReasoning(d); });
                        }
                        catch (LlmCancelledException)
                        {
                            ctx.Messages.Add(Msg("assistant", "⚠ Cancelled.", turnSteps, ThinkOf(turnThink)));
                            return "⚠ Cancelled.";
                        }
                        if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;

                        if (resp.ToolCalls != null && resp.ToolCalls.Count > 0)
                        {
                            ctx.Messages.Add(AsstWithToolCalls(resp));
                            int n = Math.Min(resp.ToolCalls.Count, cfg.MaxToolsPerTurn);

                            for (int k = 0; k < n; k++)
                            {
                                if (cancelled != null && cancelled()) break;

                                Dictionary<string, object> call = resp.ToolCalls[k] as Dictionary<string, object>;
                                if (call == null) continue;

                                string id = Json.AsString(Get(call, "id"));
                                if (id.Length == 0) id = "call_" + k;
                                call["id"] = id;

                                Dictionary<string, object> fn = Get(call, "function") as Dictionary<string, object>;
                                string name = "";
                                string argsJson = "{}";
                                if (fn != null)
                                {
                                    name = Json.AsString(Get(fn, "name"));
                                    object argsVal = fn.ContainsKey("arguments") ? fn["arguments"] : null;
                                    if (argsVal is string) argsJson = (string)argsVal;
                                    else if (argsVal != null) argsJson = Json.ToString(argsVal);
                                }
                                if (name == null || name.Trim().Length == 0) name = ToolExecutor.InferName(fn, argsJson);
                                if (fn != null && name != null && name.Trim().Length > 0) fn["name"] = name;

                                string detail = ToolDisplay.Detail(name, argsJson);
                                turnSteps.Add(ToolDisplay.Step(name, detail));
                                if (onTool != null) onTool(name, detail);

                                string result;
                                try { result = ToolExecutor.Execute(name, argsJson, cfg); }
                                catch (Exception ex) { result = "{\"error\": \"" + Escape(ex.Message) + "\"}"; }

                                ctx.Messages.Add(ToolMsg(id, name, result));
                            }
                            if (save != null) save(ctx);
                            continue;
                        }

                        string final = resp.Content;
                        ctx.Messages.Add(Msg("assistant", final, turnSteps, ThinkOf(turnThink)));
                        return final;
                    }

                    // ツール反復が上限に達した。
                    // ここで諦めると長い作業が途中で切れるので、コンテキストを
                    // 整理(要約)して続きを実行させる。
                    if (compacted >= MAX_COMPACT_ROUNDS) break;

                    ConsoleUi.Info("Compacting context after " + MAX_TURNS
                        + " tool iterations (round " + (compacted + 1) + "/"
                        + MAX_COMPACT_ROUNDS + ")...");
                    ForceCompact(cfg, ctx);

                    // 整理したうえで、続きを求める
                    ctx.Messages.Add(Msg("user",
                        "Continue from where you left off. Do not repeat what you"
                        + " already did; finish the task with a final answer."));
                    if (save != null) save(ctx);
                }

                throw new Exception("The LLM did not produce a final answer (too many tool iterations)");
            }
            finally
            {
                if (save != null) save(ctx);
            }
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
