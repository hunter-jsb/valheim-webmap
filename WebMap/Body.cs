using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WebMap
{
    // The site's write bodies, read without a JSON library: the Worker only ever
    // forwards one flat object of strings and numbers.
    internal static class Body
    {
        // a string member, escapes honoured; null when absent
        public static string Str(string body, string key)
        {
            var m = Regex.Match(body ?? "", "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return Regex.Replace(m.Groups[1].Value, @"\\(u[0-9a-fA-F]{4}|.)", mm =>
            {
                string v = mm.Groups[1].Value;
                if (v[0] == 'u') return ((char)Convert.ToInt32(v.Substring(1), 16)).ToString();
                switch (v) { case "n": return "\n"; case "t": return "\t"; case "r": return "\r"; case "b": return "\b"; case "f": return "\f"; default: return v; }
            });
        }

        // a number member; NaN when absent or not a number
        public static double Num(string body, string key)
        {
            var m = Regex.Match(body ?? "", "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : double.NaN;
        }

        public static bool Has(string body, string key) => Regex.IsMatch(body ?? "", "\"" + key + "\"\\s*:");

        // a bool member; false when absent
        public static bool Bool(string body, string key) => Regex.IsMatch(body ?? "", "\"" + key + "\"\\s*:\\s*true");

        // A JSON array's top-level elements as substrings (Discord answers arrays of
        // objects), brace/bracket-depth aware so a nested value's own commas and
        // braces never split it wrong. Empty for anything that isn't an array.
        public static List<string> Items(string json)
        {
            var items = new List<string>();
            json = (json ?? "").Trim();
            if (json.Length < 2 || json[0] != '[') return items;
            int depth = 0, start = -1; bool inStr = false, esc = false;
            for (int i = 1; i < json.Length - 1; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{' || c == '[') { if (depth == 0) start = i; depth++; }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && start >= 0) { items.Add(json.Substring(start, i - start + 1)); start = -1; }
                }
            }
            return items;
        }

        // the array a key holds, brackets and all, or null
        public static string Arr(string json, string key)
        {
            var m = Regex.Match(json ?? "", "\"" + key + "\"\\s*:\\s*\\[");
            if (!m.Success) return null;
            int i = m.Index + m.Length - 1, depth = 0; bool inStr = false, esc = false;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return json.Substring(i, j - i + 1); }
            }
            return null;
        }

        // A nested object member's own substring (e.g. a message's "author"), brace
        // matched from its opening {; null when the key is absent or not an object.
        public static string Obj(string json, string key)
        {
            var m = Regex.Match(json ?? "", "\"" + key + "\"\\s*:\\s*\\{");
            if (!m.Success) return null;
            int i = m.Index + m.Length - 1, depth = 0; bool inStr = false, esc = false;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return json.Substring(i, j - i + 1); }
            }
            return null;
        }
    }
}
