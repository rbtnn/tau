// ---------------------------------------------------------------------------
//  pdf2text - PDF からテキストを取り出す (tau の補助ツール)
//
//  PdfUtil - 壊れた PDF でも落ちないための小さな道具
//
//  壊れた PDF では、辞書の終わりが本文より後ろになったり、
//  参照先のオフセットがファイルの外を指したりする。Substring に
//  範囲外の値を渡すと ArgumentOutOfRangeException になり、
//  "Index was out of range" として利用者に見えてしまう。
//  ここで範囲を丸めてから Substring する。
// ---------------------------------------------------------------------------
using System;

namespace Pdf2Text
{
    static class PdfUtil
    {
        /// <summary>範囲外でも例外にしない Substring。</summary>
        public static string Sub(string s, int start, int len)
        {
            if (s == null) return "";
            if (start < 0) { len += start; start = 0; }
            if (len < 0) len = 0;
            if (start > s.Length) start = s.Length;
            if (start + len > s.Length) len = s.Length - start;
            return s.Substring(start, len);
        }

        /// <summary>範囲外でも例外にしない IndexOf。</summary>
        public static int SafeIndexOf(string s, string value, int start)
        {
            if (s == null || value == null) return -1;
            if (start < 0) start = 0;
            if (start > s.Length) return -1;
            return s.IndexOf(value, start, StringComparison.Ordinal);
        }
    }
}
