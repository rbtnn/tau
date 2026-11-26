// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  InputHistory - 入力した行の履歴(Ctrl-P / Ctrl-N で呼び出す)
//
//  端末のシェルと同じ感覚で、前に打った行を呼び出せるようにする。
//  保存はしない(セッションを再開したら履歴も空から始まる)。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Tau
{
    static class InputHistory
    {
        // 新しいものが末尾。上限を超えたら古いものから捨てる。
        const int MAX = 200;

        static readonly List<string> items = new();

        // 今どこを見ているか。-1 は「編集中の行(履歴ではない)」。
        static int index = -1;

        // 履歴をたどる前に編集していた内容。末尾で戻ったときに復元する。
        static string draft = "";

        /// <summary>履歴の件数(テスト用)。</summary>
        public static int Count { get { return items.Count; } }

        /// <summary>履歴を空にする。</summary>
        public static void Clear()
        {
            items.Clear();
            index = -1;
            draft = "";
        }

        /// <summary>
        /// 確定した行を履歴に加える。
        /// 空行と、直前と同じ行は加えない(シェルと同じ)。
        /// </summary>
        public static void Add(string line)
        {
            // 次の入力のために位置を戻しておく
            index = -1;
            draft = "";

            if (string.IsNullOrEmpty(line)) return;
            if (items.Count > 0 && items[items.Count - 1] == line) return;

            items.Add(line);
            while (items.Count > MAX) items.RemoveAt(0);
        }

        /// <summary>
        /// 1 つ前(古い)の履歴を返す。無ければ null。
        /// current は編集中の行(履歴の末尾から戻るときに使う)。
        /// </summary>
        public static string Previous(string current)
        {
            if (items.Count == 0) return null;

            if (index < 0)
            {
                // 初回は編集中の行を控えておく
                draft = current ?? "";
                index = items.Count - 1;
            }
            else if (index > 0)
            {
                index--;
            }
            else
            {
                return null;   // これ以上古いものは無い
            }
            return items[index];
        }

        /// <summary>
        /// 1 つ後(新しい)の履歴を返す。末尾まで戻ったら編集中の行を返す。
        /// 既に最新なら null。
        /// </summary>
        public static string Next(string current)
        {
            if (items.Count == 0 || index < 0) return null;

            if (index < items.Count - 1)
            {
                index++;
                return items[index];
            }

            // 履歴の末尾より後ろは、たどる前に編集していた行
            index = -1;
            string d = draft;
            draft = "";
            return d;
        }

        /// <summary>履歴をたどるのをやめる(行を確定したときなど)。</summary>
        public static void Reset()
        {
            index = -1;
            draft = "";
        }

        /// <summary>今たどっている位置(テスト用)。-1 なら編集中の行。</summary>
        public static int Position { get { return index; } }
    }
}
