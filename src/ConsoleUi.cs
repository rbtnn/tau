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
    static class ConsoleUi
    {
        static bool color = DetectColor();

        public static bool ColorEnabled { get { return color; } }

        static bool DetectColor()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("NO_COLOR") != null) return false;
                // Windows 10 以降 / Unix 系は概ね ANSI が使える。
                // リダイレクト時は色を付けない。
                if (Console.IsOutputRedirected) return false;
                return true;
            }
            catch { return false; }
        }

        // ---- 出力先の差し替え ----
        //
        // 通常はコンソールへ書く。--web のときは WebUi がここを差し替え、
        // 同じ文字列を SSE のログイベントとして流す。
        // 呼び出し側 (200 箇所以上) は一切変更しなくて済む。
        static Action<string> sink;

        /// <summary>出力先を差し替える。null を渡すとコンソールに戻る。</summary>
        public static void SetSink(Action<string> writer, bool useColor)
        {
            sink = writer;
            color = useColor;
        }

        public static void ResetSink()
        {
            sink = null;
            color = DetectColor();
        }

        // ---- 既定の文字色/背景色 ----
        //
        // ANSI の ESC[0m は「端末の既定色」に戻す。Windows のコンソールでは
        // 既定色が黒背景+黒文字になっていることがあり、そのままでは
        // 何も見えなくなる。起動時の色を控えておき、描画の前に戻す。
        static bool haveColors;
        static ConsoleColor defaultFore = ConsoleColor.Gray;
        static ConsoleColor defaultBack = ConsoleColor.Black;

        /// <summary>今の文字色/背景色を控える(起動時に 1 度だけ呼ぶ)。</summary>
        public static void SaveDefaultColors()
        {
            try
            {
                defaultFore = Console.ForegroundColor;
                defaultBack = Console.BackgroundColor;
                haveColors = true;
            }
            catch { }
        }

        /// <summary>控えておいた既定色に戻す。控えが無ければ何もしない。</summary>
        public static void ApplyDefaultColors()
        {
            if (!haveColors) return;
            try
            {
                Console.ForegroundColor = defaultFore;
                Console.BackgroundColor = defaultBack;
            }
            catch { }
        }

        /// <summary>
        /// 既定色が読める組み合わせか。
        /// 文字色と背景色が同じなら、そのままでは見えない。
        /// </summary>
        public static bool DefaultColorsAreReadable()
        {
            if (!haveColors) return true;
            return defaultFore != defaultBack;
        }

        static void Raw(string s)
        {
            Action<string> w = sink;
            if (w != null) { w(s); return; }
            try { Console.Write(s); } catch { }
        }

        static void Write(string code, string s)
        {
            if (color) Raw("\u001b[" + code + "m" + s + "\u001b[0m");
            else Raw(s);
        }

        public static void Dim(string s) { Write("90", s); }
        public static void Muted(string s) { Write("37", s); }
        public static void Accent(string s) { Write("36", s); }
        public static void Danger(string s) { Write("31", s); }
        public static void Bold(string s) { Write("1", s); }
        public static void Think(string s) { Write("35", s); }

        public static void Info(string s)
        {
            Raw("  ");
            Dim("· " + s);
            Raw("\n");
        }

        // ツール実行の 1 行表示
        public static void Tool(string name, string detail)
        {
            Raw("  ");
            Dim("▶ Tool: ");
            Accent(name + (detail.Length > 0 ? ("  " + detail) : ""));
            Raw("\n");
        }

        // ---- 罫線 ----
        //
        // 入力の前後に細い線を引いて、入力と応答の境目を分かりやすくする。
        // 幅は端末の桁数に合わせる(取得できなければ 60)。

        // 罫線の文字数。端末の桁数を「─」の表示幅で割って求める。
        //
        // 「─」は端末によって 1 桁にも 2 桁にもなる(Ambiguous)。
        // 文字数で端末幅ぶん書くと、2 桁の端末では折り返してしまう。
        static int LineWidth()
        {
            int width = 60;
            try
            {
                int w = Console.WindowWidth;
                if (w > 0 && w <= 10000) width = Math.Max(10, w - 1);
            }
            catch { }

            // 実測した幅があればそれで割る(無ければ 1 桁とみなす)
            int charWidth = MarkdownRenderer.MeasuredWidthOf('\u2500');
            if (charWidth <= 0) charWidth = 1;

            int n = width / charWidth;
            return n > 0 ? n : 1;
        }

        /// <summary>罫線の文字列(テストできるよう分離)。</summary>
        public static string RuleText(int width)
        {
            if (width < 1) width = 1;
            return new string('─', width);
        }

        /// <summary>入力の前に引く線。</summary>
        public static void RuleTop()
        {
            Dim(RuleText(LineWidth()) + "\n");
        }

        /// <summary>入力の後に引く線。</summary>
        public static void RuleBottom()
        {
            Dim(RuleText(LineWidth()) + "\n");
        }

        public static void Banner(Config cfg, SessionCtx ctx, bool resumed)
        {
            Raw("\n");
            Bold("tau");
            Muted(" - console AI agent");
            Raw("\n");
            Raw("  Model  : " + cfg.Model + "\n");
            Raw("  Tools  : " + (cfg.AllowTools ? "on" : "off") + "\n");
            Raw("  Config : " + cfg.ConfigPath + "\n");
            if (ctx != null)
                Raw("  Session: " + ctx.Id + "  (" + ctx.Title + ")"
                    + (resumed ? "  (resumed)" : "") + "\n");
            Raw("\n");
            Dim("  /help for commands, /exit to quit\n");
            Raw("\n");
        }
    }

    // =======================================================================
    //  対話ループ
    // =======================================================================
}
