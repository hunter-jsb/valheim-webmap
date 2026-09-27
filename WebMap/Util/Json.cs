// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WebMap.Util
{
    // A small, allocation-light JSON writer. Unity's JsonUtility cannot write
    // dictionaries or be trusted off the main thread, and everything here is
    // written from worker threads, so the mod carries its own.
    //
    // Usage:  var j = new JsonWriter(); j.BeginObject(); j.Key("x").Value(1f); j.End(); j.ToString()
    internal sealed class JsonWriter
    {
        private readonly StringBuilder sb;
        private readonly Stack<char> open = new Stack<char>();   // '{' or '['
        private readonly Stack<bool> first = new Stack<bool>();  // per container: nothing written yet?
        private bool afterKey;

        public JsonWriter(int capacity = 256) { sb = new StringBuilder(capacity); }

        private void Sep()
        {
            if (afterKey) { afterKey = false; return; }
            if (open.Count == 0) return;
            if (first.Peek()) { first.Pop(); first.Push(false); }
            else sb.Append(',');
        }

        public JsonWriter BeginObject() { Sep(); sb.Append('{'); open.Push('{'); first.Push(true); return this; }
        public JsonWriter BeginArray()  { Sep(); sb.Append('['); open.Push('['); first.Push(true); return this; }
        public JsonWriter End()
        {
            char c = open.Pop(); first.Pop();
            sb.Append(c == '{' ? '}' : ']');
            return this;
        }

        public JsonWriter Key(string k) { Sep(); Str(k); sb.Append(':'); afterKey = true; return this; }

        public JsonWriter Value(string s) { Sep(); Str(s); return this; }
        public JsonWriter Value(bool b)   { Sep(); sb.Append(b ? "true" : "false"); return this; }
        public JsonWriter Value(int v)    { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(long v)   { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(float v, int decimals = 2)  { Sep(); Num(v, decimals); return this; }
        public JsonWriter Value(double v, int decimals = 2) { Sep(); Num(v, decimals); return this; }
        public JsonWriter Null() { Sep(); sb.Append("null"); return this; }
        public JsonWriter Raw(string json) { Sep(); sb.Append(json); return this; }

        public JsonWriter Prop(string k, string v)  => Key(k).Value(v);
        public JsonWriter Prop(string k, bool v)    => Key(k).Value(v);
        public JsonWriter Prop(string k, int v)     => Key(k).Value(v);
        public JsonWriter Prop(string k, long v)    => Key(k).Value(v);
        public JsonWriter Prop(string k, float v, int decimals = 2)  => Key(k).Value(v, decimals);
        public JsonWriter Prop(string k, double v, int decimals = 2) => Key(k).Value(v, decimals);
        public JsonWriter PropRaw(string k, string json) => Key(k).Raw(json);

        private void Num(double v, int decimals)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append('0'); return; }
            if (decimals <= 0 || v == Math.Floor(v)) { sb.Append(((long)v).ToString(CultureInfo.InvariantCulture)); return; }
            sb.Append(Math.Round(v, decimals).ToString("0.################", CultureInfo.InvariantCulture));
        }

        private void Str(string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public override string ToString() => sb.ToString();
        public int Length => sb.Length;
    }

    // Minimal JSON reader for the files the mod persists (stats.json, markers.json).
    // Produces Dictionary<string, object>, List<object>, string, double, bool, null.
    internal static class JsonParser
    {
        public static object Parse(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text)
            => Parse(text) as Dictionary<string, object> ?? new Dictionary<string, object>();

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("json: unexpected end");
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't' && Match(s, i, "true")) { i += 4; return true; }
            if (c == 'f' && Match(s, i, "false")) { i += 5; return false; }
            if (c == 'n' && Match(s, i, "null")) { i += 4; return null; }
            return ParseNum(s, ref i);
        }

        private static bool Match(string s, int i, string w) => string.CompareOrdinal(s, i, w, 0, w.Length) == 0;

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = ParseStr(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("json: ':' expected");
                i++;
                d[k] = ParseValue(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("json: ',' or '}' expected");
            }
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            var l = new List<object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new FormatException("json: ',' or ']' expected");
            }
        }

        private static string ParseStr(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("json: string expected");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("json: unterminated string");
        }

        private static double ParseNum(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (start == i) throw new FormatException("json: unexpected '" + s[i] + "'");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // Convenience accessors
        public static double Num(Dictionary<string, object> d, string k, double def = 0)
            => d != null && d.TryGetValue(k, out var v) && v is double x ? x : def;
        public static string Str(Dictionary<string, object> d, string k, string def = "")
            => d != null && d.TryGetValue(k, out var v) && v is string x ? x : def;
        public static bool Bool(Dictionary<string, object> d, string k, bool def = false)
            => d != null && d.TryGetValue(k, out var v) && v is bool x ? x : def;
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        public static List<object> Arr(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as List<object> : null;
    }
}
