using System.Collections.Generic;

namespace Tau
{
    // 実行時・保存する steps・履歴再表示で同じ書式を使う。
    static class ToolDisplay
    {
        public static string Step(string name, string detail)
        {
            return "Tool: " + (string.IsNullOrEmpty(name) ? "?" : name)
                + (string.IsNullOrEmpty(detail) ? "" : "  " + detail);
        }

        public static string Detail(string name, string argsJson)
        {
            Dictionary<string, object> args;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { return ""; }
            if (args == null) return "";

            string cmd = Arg(args, "cmd");
            string path = Arg(args, "path");
            string pattern = Arg(args, "pattern");
            string url = Arg(args, "url");
            string method = Arg(args, "method");

            if (name == "shell") return cmd;
            if (name == "read_file" || name == "write_file" || name == "list_dir")
                return pattern.Length > 0 ? path + "  [" + pattern + "]" : path;
            if (name == "curl") return (method.Length > 0 ? method + " " : "") + url;
            if (cmd.Length > 0) return cmd;
            if (path.Length > 0) return path;
            return url;
        }

        static string Arg(Dictionary<string, object> args, string key)
        {
            return args.TryGetValue(key, out object value) ? Json.AsString(value).Trim() : "";
        }
    }
}
