using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WebMap
{
    // The settings an admin changes from the site. The host rewrites BepInEx's config
    // on every restart, so these live in a file of the mod's own beside the map data
    // and override the config at load and as they change. Each knows whether it
    // applies at once or at the next restart, and who last set it.
    internal static class Settings
    {
        private const string FileName = "settings.tsv";
        private class Entry
        {
            public string key, kind, desc;
            public bool live, secret;
            public Func<string> get; public Func<string, string> set;   // set returns what was wrong, or null
        }
        private class Given { public string value, by; public long t; }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Given> given = new Dictionary<string, Given>();
        private static readonly Dictionary<string, string> defaults = new Dictionary<string, string>();
        private static string dir;
        private static bool restartDue;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string F(float v) => v.ToString("0.###", Inv);
        private static string I(int v) => v.ToString(Inv);
        private static string B(bool v) => v ? "true" : "false";
        private static Func<string, string> Float(Action<float> put, float lo, float hi) => s =>
            float.TryParse(s, NumberStyles.Float, Inv, out float v) && v >= lo && v <= hi ? Run(() => put(v)) : $"a number from {F(lo)} to {F(hi)}";
        private static Func<string, string> Int(Action<int> put, int lo, int hi) => s =>
            int.TryParse(s, NumberStyles.Integer, Inv, out int v) && v >= lo && v <= hi ? Run(() => put(v)) : $"a whole number from {lo} to {hi}";
        private static Func<string, string> OneOf(Action<int> put, params int[] allowed) => s =>
            int.TryParse(s, NumberStyles.Integer, Inv, out int v) && Array.IndexOf(allowed, v) >= 0 ? Run(() => put(v)) : "one of " + string.Join(", ", Array.ConvertAll(allowed, I));
        private static Func<string, string> Bool(Action<bool> put) => s =>
            s == "true" || s == "false" ? Run(() => put(s == "true")) : "true or false";
        private static Func<string, string> Text(Action<string> put, int max, Func<string, bool> ok, string want) => s =>
            s.Length <= max && ok(s) ? Run(() => put(s)) : want;
        private static string Run(Action a) { a(); return null; }

        private static readonly Entry[] entries = {
            new Entry { key = "explore_radius", kind = "float", live = true, desc = "How far around a player the fog lifts, in metres.",
                        get = () => F(WebMapConfig.EXPLORE_RADIUS), set = Float(v => WebMapConfig.EXPLORE_RADIUS = v, 10, 500) },
            new Entry { key = "update_fog_texture_interval", kind = "float", live = true, desc = "Seconds between fog updates.",
                        get = () => F(WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL), set = Float(v => WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL = v, 0.5f, 60) },
            new Entry { key = "save_fog_texture_interval", kind = "float", live = true, desc = "Seconds between saves of the fog to disk.",
                        get = () => F(WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL), set = Float(v => WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL = v, 5, 600) },
            new Entry { key = "player_update_interval", kind = "float", live = true, desc = "Seconds between player snapshots: positions, health, the tallies.",
                        get = () => F(WebMapConfig.PLAYER_UPDATE_INTERVAL), set = Float(v => WebMapConfig.PLAYER_UPDATE_INTERVAL = v, 0.5f, 10) },
            new Entry { key = "max_pins_per_user", kind = "int", live = true, desc = "Pins one player may keep from in-game chat; the oldest go first.",
                        get = () => I(WebMapConfig.MAX_PINS_PER_USER), set = Int(v => WebMapConfig.MAX_PINS_PER_USER = v, 0, 500) },
            new Entry { key = "max_messages", kind = "int", live = true, desc = "Chat and event lines the map keeps.",
                        get = () => I(WebMapConfig.MAX_MESSAGES), set = Int(v => WebMapConfig.MAX_MESSAGES = v, 10, 1000) },
            new Entry { key = "always_map", kind = "bool", live = true, desc = "Lift the fog where players walk even when they hide their position; their spot stays hidden.",
                        get = () => B(WebMapConfig.ALWAYS_MAP), set = Bool(v => WebMapConfig.ALWAYS_MAP = v) },
            new Entry { key = "always_visible", kind = "bool", live = true, desc = "Show every player on the map whether or not they chose to be visible.",
                        get = () => B(WebMapConfig.ALWAYS_VISIBLE), set = Bool(v => WebMapConfig.ALWAYS_VISIBLE = v) },
            new Entry { key = "show_vehicles", kind = "bool", live = true, desc = "Boats and carts on the map.",
                        get = () => B(WebMapConfig.SHOW_VEHICLES), set = Bool(v => WebMapConfig.SHOW_VEHICLES = v) },
            new Entry { key = "announce_name", kind = "string", live = true, desc = "The name announcements are shouted under.",
                        get = () => WebMapConfig.ANNOUNCE_NAME, set = Text(v => WebMapConfig.ANNOUNCE_NAME = v, 24, v => v.Trim().Length > 0, "a name of up to 24 characters") },
            new Entry { key = "default_zoom", kind = "int", live = true, desc = "The bundled viewer's zoom on arrival, in percent.",
                        get = () => I(WebMapConfig.DEFAULT_ZOOM), set = Int(v => WebMapConfig.DEFAULT_ZOOM = v, 10, 500) },
            new Entry { key = "discord_webhook", kind = "string", secret = true, desc = "The Discord webhook that gets joins, leaves and deaths. Empty turns it off.",
                        get = () => WebMapConfig.DISCORD_WEBHOOK, set = Text(v => WebMapConfig.DISCORD_WEBHOOK = v, 300,
                            v => v.Length == 0 || v.StartsWith("https://discord.com/api/webhooks/") || v.StartsWith("https://discordapp.com/api/webhooks/"), "a Discord webhook URL, or empty") },
            new Entry { key = "texture_size", kind = "int", desc = "Pixels across the fog, forest and structure layers. 2048 is the world at 12 m a pixel; changing it starts the fog over.",
                        get = () => I(WebMapConfig.TEXTURE_SIZE), set = OneOf(v => WebMapConfig.TEXTURE_SIZE = v, 1024, 2048, 4096) },
            new Entry { key = "pixel_size", kind = "int", desc = "Metres a texture pixel covers.",
                        get = () => I(WebMapConfig.PIXEL_SIZE), set = Int(v => WebMapConfig.PIXEL_SIZE = v, 4, 64) },
            new Entry { key = "render_size", kind = "int", desc = "Pixels across the world render. 4096 takes a couple of minutes at start.",
                        get = () => I(WebMapConfig.RENDER_SIZE), set = OneOf(v => WebMapConfig.RENDER_SIZE = v, 1024, 2048, 4096, 8192) },
            new Entry { key = "debug", kind = "bool", live = true, desc = "Log every snapshot and request.",
                        get = () => B(WebMapConfig.DEBUG), set = Bool(v => WebMapConfig.DEBUG = v) },
        };

        // After the config file has been read: remember its values as the defaults,
        // then lay the given ones over them.
        public static void Load(string mapDataPath)
        {
            dir = mapDataPath;
            lock (gate)
            {
                defaults.Clear(); given.Clear();
                foreach (var e in entries) defaults[e.key] = e.get();
                try
                {
                    string p = Path.Combine(dir, FileName);
                    if (File.Exists(p))
                        foreach (string line in File.ReadAllLines(p))
                        {
                            var f = line.Split('\t');
                            if (f.Length < 2) continue;
                            long t = 0; if (f.Length > 3) long.TryParse(f[3], NumberStyles.Integer, Inv, out t);
                            given[f[0]] = new Given { value = f[1], by = f.Length > 2 ? f[2] : "", t = t };
                        }
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: settings not loaded: " + e.Message); }
                int applied = 0;
                foreach (var e in entries)
                    if (given.TryGetValue(e.key, out var g) && e.set(g.value) == null) applied++;
                if (applied > 0) ZLog.Log($"WebMap: {applied} settings from the site applied over the config");
            }
        }

        public static string Json()
        {
            var sb = new StringBuilder("{\"settings\":[");
            lock (gate)
            {
                int n = 0;
                foreach (var e in entries)
                {
                    if (n++ > 0) sb.Append(',');
                    given.TryGetValue(e.key, out var g);
                    string value = e.get();
                    sb.Append("{\"key\":\"").Append(e.key).Append("\",\"kind\":\"").Append(e.kind).Append("\",\"desc\":\"").Append(Esc(e.desc))
                      .Append("\",\"live\":").Append(e.live ? "true" : "false").Append(",\"secret\":").Append(e.secret ? "true" : "false")
                      .Append(",\"value\":\"").Append(Esc(e.secret ? "" : value)).Append("\",\"set\":").Append(e.secret && value.Length > 0 ? "true" : "false")
                      .Append(",\"default\":\"").Append(Esc(e.secret ? "" : defaults[e.key])).Append("\",\"given\":").Append(g != null ? "true" : "false")
                      .Append(",\"by\":\"").Append(Esc(g != null ? g.by : "")).Append("\",\"t\":").Append(g != null ? g.t : 0).Append('}');
                }
                sb.Append("],\"restart\":").Append(restartDue ? "true" : "false").Append('}');
            }
            return sb.ToString();
        }

        // A change from the site. An empty value returns the config's own. Returns what
        // was wrong, or null with the line for the log and whether a restart is due.
        public static string Set(string key, string value, string by, out string log, out bool needsRestart)
        {
            log = null; needsRestart = false;
            Entry e = null;
            foreach (var x in entries) if (x.key == key) { e = x; break; }
            if (e == null) return "no such setting";
            value = (value ?? "").Trim();
            lock (gate)
            {
                string want = value.Length == 0 ? defaults[key] : value;
                string err = e.set(want);
                if (err != null) return err;
                if (value.Length == 0) given.Remove(key);
                else given[key] = new Given { value = value, by = by ?? "", t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (!e.live) { restartDue = true; needsRestart = true; }
                Save();
                string shown = e.secret ? (value.Length == 0 ? "off" : "set") : (value.Length == 0 ? defaults[key] + " (the config's own)" : value);
                log = $"{(string.IsNullOrEmpty(by) ? "someone" : by)} set {key} to {shown}" + (e.live ? "" : ", from the next restart");
                ZLog.Log("WebMap: " + log);
            }
            return null;
        }

        private static void Save()        // under gate
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in given)
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.value.Replace('\t', ' ').Replace('\n', ' ')).Append('\t')
                      .Append((kv.Value.by ?? "").Replace('\t', ' ')).Append('\t').Append(kv.Value.t.ToString(Inv)).Append('\n');
                string p = Path.Combine(dir, FileName), tmp = p + ".new";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: settings not saved: " + e.Message); }
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // for the tests: a clean slate in a folder of their own
        internal static void ResetForTests(string mapDataPath) { restartDue = false; Load(mapDataPath); }
    }
}
