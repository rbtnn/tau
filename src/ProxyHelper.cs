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
    static class ProxyHelper
    {
        /// <summary>
        /// 指定 URL に接続するときに使うプロキシを返す。無ければ null。
        ///
        /// 診断(--diag)の TLS ハンドシェイクは TCP 接続を直接行うため、
        /// プロキシの有無を自分で判断する必要がある。
        /// SocketsHttpHandler と同じ規則(明示設定 → システム既定)で選ぶ。
        /// </summary>
        public static IWebProxy ForUrl(Config cfg, Uri uri)
        {
            if (cfg == null || uri == null) return null;

            if (cfg.ProxyUrl != null && cfg.ProxyUrl.Trim().Length > 0)
            {
                WebProxy p = new WebProxy(cfg.ProxyUrl.Trim());
                if (cfg.ProxyUser != null && cfg.ProxyUser.Length > 0)
                    p.Credentials = new NetworkCredential(cfg.ProxyUser, cfg.ProxyPass);
                else if (cfg.ProxyPass != null && cfg.ProxyPass.Length > 0)
                    p.Credentials = new NetworkCredential("", cfg.ProxyPass);
                else
                    p.UseDefaultCredentials = true;
                return p;
            }

            if (!cfg.UseSystemProxy) return null;   // 直接接続

            try
            {
                IWebProxy sys = WebRequest.DefaultWebProxy;
                if (sys == null) return null;
                Uri target = sys.GetProxy(uri);
                // プロキシを使わない設定なら null が返る
                if (target == null || target == uri) return null;
                return sys;
            }
            catch { return null; }
        }

        /// <summary>この URL の接続先(プロキシがあればプロキシ)を返す。</summary>
        public static Uri ConnectTarget(Config cfg, Uri uri)
        {
            IWebProxy proxy = ForUrl(cfg, uri);
            if (proxy == null) return uri;
            try
            {
                Uri target = proxy.GetProxy(uri);
                if (target == null) return uri;
                return target;
            }
            catch { return uri; }
        }

        // HttpClient のハンドラにプロキシ設定を反映する。
        public static void ApplyHttp(SocketsHttpHandler handler, Config cfg)
        {
            if (cfg.ProxyUrl != null && cfg.ProxyUrl.Trim().Length > 0)
            {
                WebProxy p = new WebProxy(cfg.ProxyUrl.Trim());
                if (cfg.ProxyUser != null && cfg.ProxyUser.Length > 0)
                    p.Credentials = new NetworkCredential(cfg.ProxyUser, cfg.ProxyPass);
                else if (cfg.ProxyPass != null && cfg.ProxyPass.Length > 0)
                    p.Credentials = new NetworkCredential("", cfg.ProxyPass);
                else
                    p.UseDefaultCredentials = true;   // 社内(Windows 統合認証)プロキシ向け

                handler.Proxy = p;
                handler.UseProxy = true;
            }
            else if (!cfg.UseSystemProxy)
            {
                // 直接接続(システムプロキシを無視)
                handler.UseProxy = false;
            }
            // UseSystemProxy が true なら既定のシステムプロキシが使われる
        }
    }

    // =======================================================================
    //  LLM API クライアント (OpenAI 互換)
    // =======================================================================
}
