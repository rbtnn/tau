// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  メモリ機能
//
//  システムプロンプトとは別に、エージェントが自分で読み書きできる
//  「覚え書き」を保持する。すべてのセッションで共通。
//
//  保存先: %APPDATA%\tau-cli\memory.json
//  容量:   Config.MemoryMaxChars で上限を設ける(超えたら書き込みを拒否)
//
//  エージェントは memory_read / memory_write / memory_append /
//  memory_clear の 4 つのツールで操作する。メモリの内容は
//  システムプロンプトに含めて渡すので、常に参照できる。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tau
{
    static class Memory
    {
        // メモリの保存先。設定ディレクトリと同じ場所に置く。
        public static string Path()
        {
            return System.IO.Path.Combine(Config.TauDataDir(), "memory.json");
        }

        // ---------------- 読み書き ----------------

        /// <summary>メモリの本文を読む。無ければ空文字列。</summary>
        public static string Load()
        {
            string path = Path();
            try
            {
                if (!File.Exists(path)) return "";
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (raw.Trim().Length == 0) return "";
                Dictionary<string, object> d = Json.Parse(raw) as Dictionary<string, object>;
                if (d == null) return "";
                return Json.AsString(d.ContainsKey("text") ? d["text"] : "");
            }
            catch { return ""; }
        }

        /// <summary>メモリを保存する。上限を超える場合は false。</summary>
        public static bool Save(string text, Config cfg, out string error)
        {
            error = null;
            text = text ?? "";

            int max = (cfg != null && cfg.MemoryMaxChars > 0)
                ? cfg.MemoryMaxChars : 8000;

            if (text.Length > max)
            {
                error = "memory is too long (" + text.Length + " chars, limit "
                    + max + "). Shorten it or raise memoryMaxChars.";
                return false;
            }

            try
            {
                string path = Path();
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                Dictionary<string, object> d = new();
                d["text"] = text;
                d["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                File.WriteAllText(path, Json.ToString(d), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to save memory: " + ex.Message;
                return false;
            }
        }

        /// <summary>メモリを空にする。</summary>
        public static bool Clear(Config cfg, out string error)
        {
            return Save("", cfg, out error);
        }

        // ---------------- システムプロンプトへの埋め込み ----------------

        /// <summary>
        /// メモリをシステムプロンプトに足す。
        /// 空なら何もしない。
        /// </summary>
        public static string Compose(string systemPrompt, string memory)
        {
            if (memory == null || memory.Trim().Length == 0) return systemPrompt;

            StringBuilder sb = new();
            sb.Append(systemPrompt);
            sb.Append("\n\n[Memory]\n");
            sb.Append("Notes you saved in earlier conversations (shared across sessions).\n");
            sb.Append("You can update them with the memory_* tools.\n\n");
            sb.Append(memory.Trim());
            return sb.ToString();
        }

        // ---------------- 残量の案内 ----------------

        /// <summary>上限に対する使用量を表す文字列。</summary>
        public static string Usage(string text, Config cfg)
        {
            int max = (cfg != null && cfg.MemoryMaxChars > 0)
                ? cfg.MemoryMaxChars : 8000;
            int used = (text ?? "").Length;
            return used + "/" + max + " chars";
        }
    }
}
