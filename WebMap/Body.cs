using System;
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
    }
}
