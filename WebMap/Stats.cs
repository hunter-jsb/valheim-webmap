using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace WebMap
{
    // Per-player tallies for the site's players page: what the server sees
    // anyway, added up -- joins, deaths, chat, distance covered, and what is
    // standing in the world with their name on it. No time played, by choice.
    //
    // Keyed by character name, the one identity every source shares. Builds
    // carry the character's player id instead; it is learned from the player's
    // own ZDO while they are online and remembered from then on.
    //
    // Touched from the game thread (joins, deaths, chat, snapshots) and from the
    // sweep's pool thread (standing counts), so every touch takes the one lock.
    // Persisted beside the world's map data, so a restart loses nothing.
    internal static class Stats
    {
        private class P
        {
            public string name;
            public int joins, deaths, chat, hops;
            public double dist;                          // metres, snapshot to snapshot
            public int pieces, portals, ships, graves;   // standing now, from the last sweep
            public long lastSeen;                        // unix seconds
            public bool online, hasPos;
            public float lx, lz;
        }

        // Further than this between two snapshots a second apart is a portal or a
        // respawn, not a walk: it counts as a hop and not as distance.
        private const float TeleportM = 100f;
        private const int SaveEveryMs = 60_000;

        private static readonly object gate = new object();
        private static readonly Dictionary<string, P> byName = new Dictionary<string, P>(StringComparer.Ordinal);
        private static readonly Dictionary<long, string> nameOfId = new Dictionary<long, string>();
        private static string path;
        private static long since;
        private static bool dirty;
        private static int lastSave = Environment.TickCount;
        private static volatile string json = "{\"since\":0,\"players\":[]}";
        private static volatile bool jsonStale = true;

        // Per sweep: filled on the game thread during the walk, read on the pool
        // thread in PublishSweep. The next walk cannot start before that returns.
        private struct Standing { public int pieces, portals, ships; }
        private static readonly Dictionary<long, Standing> sweepById = new Dictionary<long, Standing>();
        private static readonly Dictionary<string, int> sweepGraves = new Dictionary<string, int>();

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Names land in JSON and in a tab-separated file, so control characters
        // are dropped at the door rather than escaped in two places.
        private static string Clean(string name) => name.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");

        private static P Get(string name)                     // caller holds gate
        {
            name = Clean(name);
            if (!byName.TryGetValue(name, out var p)) { p = new P { name = name }; byName[name] = p; }
            return p;
        }

        private static void Touch(P p) { p.lastSeen = Now(); dirty = true; jsonStale = true; }

        public static void Join(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate) { var p = Get(name); p.joins++; p.online = true; p.hasPos = false; Touch(p); }
        }

        public static void Leave(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                // a peer rejected at the handshake leaves without ever joining: not a player
                if (!byName.TryGetValue(Clean(name), out var p)) return;
                p.online = false; p.hasPos = false; Touch(p);
            }
        }

        public static void Death(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate) { var p = Get(name); p.deaths++; p.hasPos = false; Touch(p); }
        }

        public static void Chat(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate) { var p = Get(name); p.chat++; Touch(p); }
        }

        // Game thread, once per player per snapshot: learn the id, add up the walk.
        public static void Seen(string name, long playerId, Vector3 pos)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                var p = Get(name);
                if (playerId != 0L) nameOfId[playerId] = name;
                p.online = true;
                if (p.hasPos)
                {
                    float dx = pos.x - p.lx, dz = pos.z - p.lz;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d < TeleportM) p.dist += d;
                    else p.hops++;
                }
                p.lx = pos.x; p.lz = pos.z; p.hasPos = true;
                p.lastSeen = Now(); dirty = true; jsonStale = true;
            }
        }

        // Sweep: game thread during the walk, then the pool thread once.
        public static void BeginSweep() { sweepById.Clear(); sweepGraves.Clear(); }

        public static void ObservePiece(long creator, bool portal, bool ship)
        {
            sweepById.TryGetValue(creator, out var s);
            if (ship) s.ships++; else { s.pieces++; if (portal) s.portals++; }
            sweepById[creator] = s;
        }

        public static void ObserveGrave(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            sweepGraves.TryGetValue(owner, out int n);
            sweepGraves[owner] = n + 1;
        }

        public static void PublishSweep()
        {
            lock (gate)
            {
                foreach (var p in byName.Values) { p.pieces = 0; p.portals = 0; p.ships = 0; p.graves = 0; }
                foreach (var kv in sweepById)
                {
                    if (!nameOfId.TryGetValue(kv.Key, out string name)) continue;   // a builder never seen online
                    var p = Get(name);
                    p.pieces = kv.Value.pieces; p.portals = kv.Value.portals; p.ships = kv.Value.ships;
                }
                foreach (var kv in sweepGraves) Get(kv.Key).graves = kv.Value;
                jsonStale = true;
            }
        }

        // Pins are counted from the live pin list at read time; they are the one
        // tally kept elsewhere.
        public static string Json(Dictionary<string, int> pinsByName)
        {
            if (!jsonStale) return json;
            lock (gate)
            {
                var list = new List<P>(byName.Values);
                list.Sort((a, b) => b.lastSeen.CompareTo(a.lastSeen));
                var sb = new StringBuilder();
                sb.Append("{\"since\":").Append(since).Append(",\"players\":[");
                bool first = true;
                foreach (var p in list)
                {
                    pinsByName.TryGetValue(p.name, out int pins);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(FormattableString.Invariant(
                        $"{{\"name\":\"{Esc(p.name)}\",\"online\":{(p.online ? "true" : "false")},\"joins\":{p.joins},\"deaths\":{p.deaths},\"chat\":{p.chat},\"dist_m\":{Math.Round(p.dist)},\"hops\":{p.hops},\"pins\":{pins},\"pieces\":{p.pieces},\"portals\":{p.portals},\"ships\":{p.ships},\"graves\":{p.graves},\"last_seen\":{p.lastSeen}}}"));
                }
                sb.Append("]}");
                json = sb.ToString();
                jsonStale = false;
                return json;
            }
        }

        private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // One tab-separated line per player plus the id map: nothing to parse
        // but Split. Standing counts are not saved; the next sweep recounts them.
        public static void Load(string worldDataPath)
        {
            lock (gate)
            {
                path = Path.Combine(worldDataPath, "stats.tsv");
                byName.Clear(); nameOfId.Clear();
                since = Now();
                try
                {
                    string file = File.Exists(path) ? path : File.Exists(path + ".new") ? path + ".new" : null;
                    if (file == null) return;
                    foreach (string line in File.ReadAllLines(file))
                    {
                        var f = line.Split('\t');
                        if (f.Length >= 2 && f[0] == "since") long.TryParse(f[1], out since);
                        else if (f.Length >= 3 && f[0] == "id" && long.TryParse(f[1], out long id)) nameOfId[id] = f[2];
                        else if (f.Length >= 8 && f[0] == "p")
                        {
                            var p = Get(f[1]);
                            int.TryParse(f[2], out p.joins); int.TryParse(f[3], out p.deaths);
                            int.TryParse(f[4], out p.chat); int.TryParse(f[5], out p.hops);
                            double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out p.dist);
                            long.TryParse(f[7], out p.lastSeen);
                        }
                    }
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: stats not loaded: " + e.Message); }
                finally { jsonStale = true; }
            }
        }

        public static void MaybeSave()
        {
            if (!dirty || unchecked((uint)(Environment.TickCount - lastSave)) < SaveEveryMs) return;
            Save();
        }

        public static void Save()
        {
            lock (gate)
            {
                if (path == null) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("since\t").Append(since).Append('\n');
                    foreach (var kv in nameOfId) sb.Append("id\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                    foreach (var p in byName.Values)
                        sb.Append(FormattableString.Invariant($"p\t{p.name}\t{p.joins}\t{p.deaths}\t{p.chat}\t{p.hops}\t{p.dist:0.#}\t{p.lastSeen}\n"));
                    File.WriteAllText(path + ".new", sb.ToString());
                    if (File.Exists(path)) File.Replace(path + ".new", path, null);   // one rename, never a gap
                    else File.Move(path + ".new", path);
                    dirty = false;
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: stats not saved: " + e.Message); }
                finally { lastSave = Environment.TickCount; }   // a failing disk is retried a minute later, not every second
            }
        }
    }
}
