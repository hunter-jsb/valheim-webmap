using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace WebMap
{
    // Per-player tallies for the site's players page: what the server sees
    // anyway, added up -- joins, deaths, chat, distance covered, what is standing
    // in the world with their name on it, the gear they carry and the look they
    // were last seen in. No time played as a figure, by choice, though the seconds
    // with each chest class add up to it.
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
            // distance and deaths by the biome they happened in (Features' classes)
            public double[] mBiome = new double[Biomes];
            public int[] dBiome = new int[Biomes];
            // health under a tenth and back over three tenths without dying is a close call
            public int closeCalls; public float lowest = 1f; public bool low; public float dipMin;
            public double sinceDeath, bestStreak;        // metres walked since the last death, and the longest such walk
            // corpse runs: reached the spot (ok), died within 200 m of it first (failed),
            // the grave gone without them ever getting there (rescued)
            public int runsOk, runsFailed, runsRescued;
            public double runM, runBestM;                // metres walked between a death and reaching it
            public List<Spot> open = new List<Spot>();   // deaths not yet reached
            public int kills, trees, rocks;              // what Deeds credits them with
            // seconds per hand family and chest class, the latest set worn, and hits by kind (Gear's)
            public double[] hand = new double[Gear.HandNames.Length], armor = new double[Gear.ArmorNames.Length];
            public string[] worn = new string[Gear.Slots.Length];
            public int[] hits = new int[Gear.HitNames.Length];
            public double[] diet = new double[Gear.DietNames.Length]; public float foodHp, foodSt, foodEitr;   // seconds per diet, and the latest food-borne figures
            public string look; public int yaw;          // RigExporter's look JSON as last seen, for a page to draw them offline
            public int[] made = new int[Kitchen.MadeNames.Length];   // what Kitchen credits them with
            public Dictionary<string, int> dishes = new Dictionary<string, int>(StringComparer.Ordinal);   // cooked, brewed or smelted, by product
        }
        // a death spot: where, how far the walk had come, and whether a sweep has seen a grave there
        private class Spot { public float x, z; public double distAt; public long t; public bool seen; public int sweep; }
        private const int Biomes = 11;   // classes 1..10: Meadows through Mistlands (0x200 -> 10); 0 is water
        private const float ReachM = 8f, FailM = 200f, GraveM = 30f;
        private struct Boss { public string key; public long t; }
        private static readonly List<Boss> bosses = new List<Boss>();

        // Further than this between two snapshots a second apart is a portal or a
        // respawn, not a walk: it counts as a hop and not as distance.
        private const float TeleportM = 100f;
        private const int SaveEveryMs = 60_000;

        private static readonly object gate = new object();
        private static readonly Dictionary<string, P> byName = new Dictionary<string, P>(StringComparer.Ordinal);
        private static readonly Dictionary<long, string> nameOfId = new Dictionary<long, string>();
        // Where people died, newest last; the last snapshot's position, taken a second before
        private struct D { public string name; public float x, z; public long t; }
        private const int MaxDeaths = 500;
        private static readonly List<D> deaths = new List<D>();
        private static volatile string deathsJson = "{\"deaths\":[],\"count\":0}";
        public static volatile float[] DeathPositions = new float[0];   // x, z pairs, for what a place has seen
        private static volatile bool deathsStale = true;
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
        private static readonly List<(string name, float x, float z)> sweepGraveAt = new List<(string, float, float)>();

        // The world's kitchen, credited or not, and each station's share by prefab and place.
        private class Kit { public string kind; public float x, z; public int[] made = new int[Kitchen.MadeNames.Length]; }
        private static readonly int[] madeAll = new int[Kitchen.MadeNames.Length];
        private static readonly Dictionary<string, Kit> kits = new Dictionary<string, Kit>(StringComparer.Ordinal);
        private const int MaxKits = 256, MaxDishes = 64, KitsShown = 40;

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
            lock (gate)
            {
                var p = Get(name); p.deaths++;
                if (p.sinceDeath > p.bestStreak) p.bestStreak = p.sinceDeath;
                p.sinceDeath = 0; p.low = false;
                if (p.hasPos)
                {
                    deaths.Add(new D { name = p.name, x = p.lx, z = p.lz, t = Now() });
                    if (deaths.Count > MaxDeaths) deaths.RemoveAt(0);
                    deathsStale = true;
                    int c = Features.ClassAt(p.lx, p.lz);
                    if (c >= 0 && c < Biomes) p.dBiome[c]++;
                    // dying beside your own grave is the corpse run that failed
                    foreach (var o in p.open)
                        if (Dist(o.x, o.z, p.lx, p.lz) < FailM) { p.runsFailed++; break; }
                    p.open.Add(new Spot { x = p.lx, z = p.lz, distAt = p.dist, t = Now(), sweep = sweeps });
                    if (p.open.Count > 20) p.open.RemoveAt(0);
                }
                p.hasPos = false; Touch(p);
            }
        }
        private static double Dist(float ax, float az, float bx, float bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        // The world's boss keys, read on the game thread each sweep; a new one is a boss down.
        public static void ObserveKeys(IEnumerable<string> keys)
        {
            if (keys == null) return;
            lock (gate)
            {
                foreach (var k in keys)
                {
                    if (k == null || !k.StartsWith("defeated_")) continue;
                    bool known = false;
                    foreach (var b in bosses) if (b.key == k) { known = true; break; }
                    if (known) continue;
                    bosses.Add(new Boss { key = k, t = Now() });
                    dirty = true; jsonStale = true;
                    ZLog.Log("WebMap: boss down: " + k);
                }
            }
        }

        public static string DeathsJson()
        {
            if (!deathsStale) return deathsJson;
            lock (gate)
            {
                var sb = new StringBuilder("{\"deaths\":[");
                for (int i = 0; i < deaths.Count; i++)
                {
                    var d = deaths[i];
                    if (i > 0) sb.Append(',');
                    sb.Append(FormattableString.Invariant($"{{\"n\":\"{Esc(d.name)}\",\"x\":{d.x:0.#},\"z\":{d.z:0.#},\"t\":{d.t}}}"));
                }
                sb.Append("],\"count\":").Append(deaths.Count).Append('}');
                var pos = new float[deaths.Count * 2];
                for (int i = 0; i < deaths.Count; i++) { pos[i * 2] = deaths[i].x; pos[i * 2 + 1] = deaths[i].z; }
                DeathPositions = pos;
                deathsJson = sb.ToString(); deathsStale = false;
                return deathsJson;
            }
        }

        public static void Chat(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate) { var p = Get(name); p.chat++; Touch(p); }
        }

        public static void Deed(string name, Deeds.Kind kind)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                var p = Get(name);
                if (kind == Deeds.Kind.Kill) p.kills++; else if (kind == Deeds.Kind.Tree) p.trees++; else p.rocks++;
                Touch(p);
            }
        }

        // Kitchen's credits: nobody's when the maker is unknown, and the world's all the same. A
        // negative n takes a dish back -- done, then left on the fire to burn. Not a sighting,
        // so last_seen stays: the placer may have logged off before their dish was done.
        public static void Made(string who, Kitchen.Made what, string item, int n, string station, float x, float z)
        {
            int k = (int)what;
            lock (gate)
            {
                madeAll[k] = Math.Max(0, madeAll[k] + n);
                var s = KitAt(station ?? "", x, z);
                s.made[k] = Math.Max(0, s.made[k] + n);
                if (!string.IsNullOrEmpty(who))
                {
                    var p = Get(who);
                    p.made[k] = Math.Max(0, p.made[k] + n);
                    if (!string.IsNullOrEmpty(item) && what != Kitchen.Made.Burnt && what != Kitchen.Made.Honey)
                    {
                        item = Clean(item);
                        p.dishes.TryGetValue(item, out int c);
                        if (c + n <= 0) p.dishes.Remove(item);
                        else if (c > 0 || p.dishes.Count < MaxDishes) p.dishes[item] = c + n;
                    }
                }
                dirty = true; jsonStale = true;
            }
        }

        private static Kit KitAt(string kind, float x, float z)   // caller holds gate
        {
            string key = FormattableString.Invariant($"{kind}@{x:0.#},{z:0.#}");
            if (kits.TryGetValue(key, out Kit s)) return s;
            if (kits.Count >= MaxKits)                            // the quietest makes room
            {
                string least = null; long ln = long.MaxValue;
                foreach (var kv in kits) { long t = 0; foreach (int v in kv.Value.made) t += v; if (t < ln) { ln = t; least = kv.Key; } }
                kits.Remove(least);
            }
            s = new Kit { kind = Clean(kind), x = x, z = z };
            kits[key] = s;
            return s;
        }
        private static int Busy(Kit s) => s.made[(int)Kitchen.Made.Cooked] + s.made[(int)Kitchen.Made.Brewed] + s.made[(int)Kitchen.Made.Smelted] + s.made[(int)Kitchen.Made.Honey];

        // Game thread, once per player per snapshot. Each hand adds the seconds to
        // its family, a family in both hands once; both hands empty is none.
        public static void Wore(string name, Gear.Hand right, Gear.Hand left, bool twoHanded, Gear.Armor armor, string[] worn, float seconds)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                var p = Get(name);
                if (right != Gear.Hand.None) p.hand[(int)right] += seconds;
                if (left != Gear.Hand.None && left != right) p.hand[(int)left] += seconds;
                if (right == Gear.Hand.None && left == Gear.Hand.None) p.hand[(int)Gear.Hand.None] += seconds;
                if (twoHanded) p.hand[(int)Gear.Hand.TwoHanded] += seconds;
                p.armor[(int)armor] += seconds;
                for (int i = 0; i < p.worn.Length; i++) p.worn[i] = i < worn.Length && !string.IsNullOrEmpty(worn[i]) ? Clean(worn[i]) : null;
                dirty = true; jsonStale = true;
            }
        }
        public static void Fed(string name, Gear.Diet diet, float hp, float st, float eitr, float seconds)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                var p = Get(name);
                p.diet[(int)diet] += seconds;
                p.foodHp = hp; p.foodSt = st; p.foodEitr = eitr;
                dirty = true; jsonStale = true;
            }
        }

        // Game thread, as /state's players are built. A look changes only with the gear, so
        // the same one a second later marks nothing for a rebuild or a save.
        public static void Looked(string name, string look, int yaw)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(look)) return;
            lock (gate)
            {
                var p = Get(name);
                p.yaw = yaw;
                if (look == p.look) return;
                p.look = look; dirty = true; jsonStale = true;
            }
        }

        public static void Struck(string name, Gear.Hit kind, bool backstab)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                var p = Get(name);
                p.hits[(int)kind]++;
                if (backstab) p.hits[(int)Gear.Hit.Backstab]++;
                Touch(p);
            }
        }

        // A builder's name from the player id their pieces carry; null for one never seen online.
        public static string NameOf(long playerId) { lock (gate) return nameOfId.TryGetValue(playerId, out var n) ? n : null; }

        // Game thread, once per player per snapshot: learn the id, add up the walk.
        public static void Seen(string name, long playerId, Vector3 pos, float health = -1f, float maxHealth = -1f)
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
                    if (d < TeleportM)
                    {
                        p.dist += d; p.sinceDeath += d;
                        int c = Features.ClassAt(pos.x, pos.z);
                        if (c >= 0 && c < Biomes) p.mBiome[c] += d;
                    }
                    else p.hops++;
                }
                // back at a death spot: the run is done, however far it took
                for (int i = p.open.Count - 1; i >= 0; i--)
                {
                    var o = p.open[i];
                    if (Dist(o.x, o.z, pos.x, pos.z) >= ReachM) continue;
                    double walked = Math.Max(0, p.dist - o.distAt);
                    p.runsOk++; p.runM += walked; if (walked > p.runBestM) p.runBestM = walked;
                    p.open.RemoveAt(i);
                }
                if (maxHealth > 0f && health >= 0f)
                {
                    float hp = health / maxHealth;
                    if (!p.low && hp > 0f && hp < 0.1f) { p.low = true; p.dipMin = hp; }
                    else if (p.low)
                    {
                        if (hp < p.dipMin) p.dipMin = hp;
                        if (hp >= 0.3f) { p.low = false; p.closeCalls++; if (p.dipMin < p.lowest) p.lowest = p.dipMin; }
                    }
                }
                p.lx = pos.x; p.lz = pos.z; p.hasPos = true;
                p.lastSeen = Now(); dirty = true; jsonStale = true;
            }
        }

        // Sweep: game thread during the walk, then the pool thread once.
        private static int sweeps;         // walks begun; a death spot remembers which it fell in
        public static void BeginSweep() { sweepById.Clear(); sweepGraves.Clear(); sweepGraveAt.Clear(); sweeps++; }

        public static void ObservePiece(long creator, bool portal, bool ship)
        {
            sweepById.TryGetValue(creator, out var s);
            if (ship) s.ships++; else { s.pieces++; if (portal) s.portals++; }
            sweepById[creator] = s;
        }

        public static void ObserveGrave(string owner, float x = float.NaN, float z = float.NaN)
        {
            if (string.IsNullOrEmpty(owner)) return;
            sweepGraves.TryGetValue(owner, out int n);
            sweepGraves[owner] = n + 1;
            if (!float.IsNaN(x)) sweepGraveAt.Add((Clean(owner), x, z));
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
                // a death spot whose grave a sweep saw, and now sees no more, without the
                // owner ever getting back: somebody else emptied it
                foreach (var p in byName.Values)
                    for (int i = p.open.Count - 1; i >= 0; i--)
                    {
                        var o = p.open[i];
                        bool standing = false;
                        foreach (var g in sweepGraveAt)
                            if (g.name == p.name && Dist(g.x, g.z, o.x, o.z) < GraveM) { standing = true; break; }
                        if (standing) { o.seen = true; continue; }
                        // a death during this walk: its tombstone may have landed behind the walk
                        if (!o.seen && o.sweep >= sweeps) continue;
                        if (o.seen) p.runsRescued++;
                        p.open.RemoveAt(i);      // never seen by a whole sweep: nothing was ever lying there
                    }
                jsonStale = true;
            }
        }

        // Pins are counted from the live pin list at read time; they are the one
        // tally kept elsewhere, so a pin placed from the site while nobody is on
        // must rebuild the JSON too.
        private static Dictionary<string, int> jsonPins;
        public static string Json(Dictionary<string, int> pinsByName)
        {
            if (!jsonStale && SamePins(pinsByName, jsonPins)) return json;
            lock (gate)
            {
                jsonPins = pinsByName;
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
                        $"{{\"name\":\"{Esc(p.name)}\",\"online\":{(p.online ? "true" : "false")},\"joins\":{p.joins},\"deaths\":{p.deaths},\"chat\":{p.chat},\"dist_m\":{Math.Round(p.dist)},\"hops\":{p.hops},\"pins\":{pins},\"pieces\":{p.pieces},\"portals\":{p.portals},\"ships\":{p.ships},\"graves\":{p.graves},\"kills\":{p.kills},\"trees\":{p.trees},\"rocks\":{p.rocks},\"last_seen\":{p.lastSeen}"));
                    sb.Append(",\"biomes\":[");
                    bool bf = true;
                    for (int c = 0; c < Biomes; c++)       // 0 is the sea
                    {
                        if (p.mBiome[c] < 1 && p.dBiome[c] == 0) continue;
                        if (!bf) sb.Append(','); bf = false;
                        sb.Append(FormattableString.Invariant($"{{\"biome\":\"{Features.BiomeName((byte)c)}\",\"m\":{Math.Round(p.mBiome[c])},\"deaths\":{p.dBiome[c]}}}"));
                    }
                    sb.Append(FormattableString.Invariant($"],\"close_calls\":{p.closeCalls},\"lowest_hp\":{(p.closeCalls > 0 ? p.lowest : 1f):0.###}"));
                    sb.Append(FormattableString.Invariant($",\"streak_m\":{{\"best\":{Math.Round(Math.Max(p.bestStreak, p.sinceDeath))},\"now\":{Math.Round(p.sinceDeath)}}}"));
                    sb.Append(FormattableString.Invariant($",\"runs\":{{\"ok\":{p.runsOk},\"failed\":{p.runsFailed},\"rescued\":{p.runsRescued},\"open\":{p.open.Count}}}"));
                    sb.Append(FormattableString.Invariant($",\"run_m\":{{\"total\":{Math.Round(p.runM)},\"best\":{Math.Round(p.runBestM)}}}"));
                    sb.Append(",\"gear\":{\"hand\":"); Seconds(sb, p.hand, Gear.HandNames);
                    sb.Append(",\"armor\":"); Seconds(sb, p.armor, Gear.ArmorNames);
                    sb.Append(",\"diet\":"); Seconds(sb, p.diet, Gear.DietNames);
                    sb.Append(FormattableString.Invariant($",\"food\":{{\"hp\":{Math.Round(p.foodHp)},\"st\":{Math.Round(p.foodSt)},\"eitr\":{Math.Round(p.foodEitr)}}}"));
                    sb.Append(",\"worn\":{");
                    bool wf = true;
                    for (int i = 0; i < p.worn.Length; i++)
                    {
                        if (p.worn[i] == null) continue;
                        if (!wf) sb.Append(','); wf = false;
                        sb.Append('"').Append(Gear.Slots[i]).Append("\":\"").Append(Esc(p.worn[i])).Append('"');
                    }
                    sb.Append("},\"hits\":{");
                    for (int i = 0; i < p.hits.Length; i++)
                        sb.Append(i > 0 ? ",\"" : "\"").Append(Gear.HitNames[i]).Append("\":").Append(p.hits[i]);
                    sb.Append("}}");
                    sb.Append(",\"kitchen\":{"); Made(sb, p.made);
                    sb.Append(",\"dishes\":{");
                    var dishes = new List<KeyValuePair<string, int>>(p.dishes);
                    dishes.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
                    for (int i = 0; i < dishes.Count; i++)
                        sb.Append(i > 0 ? ",\"" : "\"").Append(Esc(dishes[i].Key)).Append("\":").Append(dishes[i].Value);
                    sb.Append("}}");
                    if (p.look != null) sb.Append(",\"look\":").Append(p.look).Append(",\"yaw\":").Append(p.yaw);
                    sb.Append('}');
                }
                sb.Append("],\"bosses\":[");
                for (int i = 0; i < bosses.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(FormattableString.Invariant($"{{\"key\":\"{Esc(bosses[i].key)}\",\"t\":{bosses[i].t}}}"));
                }
                sb.Append("],\"kitchen\":{"); Made(sb, madeAll);
                sb.Append(",\"stations\":[");
                var busiest = new List<Kit>(kits.Values);
                busiest.Sort((a, b) => Busy(b).CompareTo(Busy(a)));
                for (int i = 0; i < busiest.Count && i < KitsShown; i++)
                {
                    var s = busiest[i];
                    if (i > 0) sb.Append(',');
                    sb.Append(FormattableString.Invariant($"{{\"kind\":\"{Esc(s.kind)}\",\"x\":{s.x:0.#},\"z\":{s.z:0.#},\"cooked\":{s.made[(int)Kitchen.Made.Cooked]},\"brewed\":{s.made[(int)Kitchen.Made.Brewed]},\"smelted\":{s.made[(int)Kitchen.Made.Smelted]},\"honey\":{s.made[(int)Kitchen.Made.Honey]}}}"));
                }
                sb.Append("]}}");
                json = sb.ToString();
                jsonStale = false;
                return json;
            }
        }

        private static bool SamePins(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            if (a == null || b == null || a.Count != b.Count) return a == b;
            foreach (var kv in a) if (!b.TryGetValue(kv.Key, out int n) || n != kv.Value) return false;
            return true;
        }

        private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // "cooked":n,... "smelted":n, inside an object the caller opened
        private static void Made(StringBuilder sb, int[] made)
        {
            for (int i = 0; i < made.Length; i++) sb.Append(i > 0 ? ",\"" : "\"").Append(Kitchen.MadeNames[i]).Append("\":").Append(made[i]);
        }

        // whole seconds by name, the empty ones left out
        private static void Seconds(StringBuilder sb, double[] secs, string[] names)
        {
            sb.Append('{');
            bool first = true;
            for (int i = 0; i < secs.Length; i++)
            {
                double v = Math.Round(secs[i]);
                if (v < 1) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append('"').Append(names[i]).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        // One tab-separated line per player plus the id map: nothing to parse
        // but Split. Standing counts are not saved; the next sweep recounts them.
        public static void Load(string worldDataPath)
        {
            lock (gate)
            {
                path = Path.Combine(worldDataPath, "stats.tsv");
                byName.Clear(); nameOfId.Clear(); deaths.Clear(); bosses.Clear(); kits.Clear(); Array.Clear(madeAll, 0, madeAll.Length);
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
                            if (f.Length >= 17)
                            {
                                int.TryParse(f[8], out p.closeCalls);
                                float.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out p.lowest);
                                double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out p.sinceDeath);
                                double.TryParse(f[11], NumberStyles.Float, CultureInfo.InvariantCulture, out p.bestStreak);
                                int.TryParse(f[12], out p.runsOk); int.TryParse(f[13], out p.runsFailed); int.TryParse(f[14], out p.runsRescued);
                                double.TryParse(f[15], NumberStyles.Float, CultureInfo.InvariantCulture, out p.runM);
                                double.TryParse(f[16], NumberStyles.Float, CultureInfo.InvariantCulture, out p.runBestM);
                            }
                            if (f.Length >= 20) { int.TryParse(f[17], out p.kills); int.TryParse(f[18], out p.trees); int.TryParse(f[19], out p.rocks); }
                        }
                        else if (f.Length >= 4 && f[0] == "m")        // metres and deaths in one biome
                        {
                            var p = Get(f[1]);
                            if (int.TryParse(f[2], out int c) && c >= 0 && c < Biomes)
                            {
                                double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p.mBiome[c]);
                                if (f.Length >= 5) int.TryParse(f[4], out p.dBiome[c]);
                            }
                        }
                        else if (f.Length >= 4 && (f[0] == "h" || f[0] == "a" || f[0] == "e"))   // seconds with a hand family, in a chest class, or eating a diet
                        {
                            var p = Get(f[1]);
                            double[] into = f[0] == "h" ? p.hand : f[0] == "a" ? p.armor : p.diet;
                            int k = Array.IndexOf(f[0] == "h" ? Gear.HandNames : f[0] == "a" ? Gear.ArmorNames : Gear.DietNames, f[2]);
                            if (k >= 0) double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out into[k]);
                        }
                        else if (f.Length >= 5 && f[0] == "n")        // the food-borne figures as last seen
                        {
                            var p = Get(f[1]);
                            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out p.foodHp);
                            float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p.foodSt);
                            float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out p.foodEitr);
                        }
                        else if (f.Length >= 2 && f[0] == "w")        // the set last worn, empty where nothing was
                        {
                            var p = Get(f[1]);
                            for (int i = 0; i < p.worn.Length && i + 2 < f.Length; i++) p.worn[i] = f[i + 2].Length > 0 ? f[i + 2] : null;
                        }
                        else if (f.Length >= 2 + Gear.HitNames.Length && f[0] == "x")   // hits by kind
                        {
                            var p = Get(f[1]);
                            for (int i = 0; i < p.hits.Length; i++) int.TryParse(f[i + 2], out p.hits[i]);
                        }
                        else if (f.Length >= 4 && f[0] == "l" && f[3].StartsWith("{", StringComparison.Ordinal) && f[3].EndsWith("}", StringComparison.Ordinal))   // the look last seen, whole
                        {
                            var p = Get(f[1]);
                            int.TryParse(f[2], out p.yaw); p.look = f[3];
                        }
                        else if (f.Length >= 2 + Kitchen.MadeNames.Length && f[0] == "c")   // what the kitchen credited them with
                        {
                            var p = Get(f[1]);
                            for (int i = 0; i < p.made.Length; i++) int.TryParse(f[i + 2], out p.made[i]);
                        }
                        else if (f.Length >= 4 && f[0] == "f" && int.TryParse(f[3], out int dn) && dn > 0)   // a dish, mead or bar, and how many
                            Get(f[1]).dishes[f[2]] = dn;
                        else if (f.Length >= 1 + Kitchen.MadeNames.Length && f[0] == "k")   // the world's kitchen
                            for (int i = 0; i < madeAll.Length; i++) int.TryParse(f[i + 1], out madeAll[i]);
                        else if (f.Length >= 4 + Kitchen.MadeNames.Length && f[0] == "s")   // a station's share: prefab, x, z, then the five
                        {
                            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                            float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
                            var s = KitAt(f[1], x, z);
                            for (int i = 0; i < s.made.Length; i++) int.TryParse(f[i + 4], out s.made[i]);
                        }
                        else if (f.Length >= 7 && f[0] == "g")        // a death not yet reached
                        {
                            var o = new Spot();
                            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out o.x);
                            float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out o.z);
                            double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out o.distAt);
                            long.TryParse(f[5], out o.t); o.seen = f[6] == "1";
                            Get(f[1]).open.Add(o);
                        }
                        else if (f.Length >= 3 && f[0] == "b")
                        {
                            long.TryParse(f[2], out long t);
                            bosses.Add(new Boss { key = f[1], t = t });
                        }
                        else if (f.Length >= 5 && f[0] == "d")
                        {
                            var d = new D { name = f[1] };
                            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out d.x);
                            float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d.z);
                            long.TryParse(f[4], out d.t);
                            deaths.Add(d);
                        }
                    }
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: stats not loaded: " + e.Message); }
                finally { jsonStale = true; deathsStale = true; }
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
                    {
                        sb.Append(FormattableString.Invariant($"p\t{p.name}\t{p.joins}\t{p.deaths}\t{p.chat}\t{p.hops}\t{p.dist:0.#}\t{p.lastSeen}"));
                        sb.Append(FormattableString.Invariant($"\t{p.closeCalls}\t{p.lowest:0.###}\t{p.sinceDeath:0.#}\t{p.bestStreak:0.#}\t{p.runsOk}\t{p.runsFailed}\t{p.runsRescued}\t{p.runM:0.#}\t{p.runBestM:0.#}\t{p.kills}\t{p.trees}\t{p.rocks}\n"));
                        for (int c = 0; c < Biomes; c++)
                            if (p.mBiome[c] >= 1 || p.dBiome[c] > 0)
                                sb.Append(FormattableString.Invariant($"m\t{p.name}\t{c}\t{p.mBiome[c]:0.#}\t{p.dBiome[c]}\n"));
                        foreach (var o in p.open)
                            sb.Append(FormattableString.Invariant($"g\t{p.name}\t{o.x:0.#}\t{o.z:0.#}\t{o.distAt:0.#}\t{o.t}\t{(o.seen ? 1 : 0)}\n"));
                        for (int i = 0; i < p.hand.Length; i++)
                            if (p.hand[i] > 0) sb.Append(FormattableString.Invariant($"h\t{p.name}\t{Gear.HandNames[i]}\t{p.hand[i]:0.#}\n"));
                        for (int i = 0; i < p.armor.Length; i++)
                            if (p.armor[i] > 0) sb.Append(FormattableString.Invariant($"a\t{p.name}\t{Gear.ArmorNames[i]}\t{p.armor[i]:0.#}\n"));
                        for (int i = 0; i < p.diet.Length; i++)
                            if (p.diet[i] > 0) sb.Append(FormattableString.Invariant($"e\t{p.name}\t{Gear.DietNames[i]}\t{p.diet[i]:0.#}\n"));
                        if (p.foodHp + p.foodSt + p.foodEitr > 0) sb.Append(FormattableString.Invariant($"n\t{p.name}\t{p.foodHp:0.#}\t{p.foodSt:0.#}\t{p.foodEitr:0.#}\n"));
                        if (Array.Exists(p.worn, w => w != null))
                            sb.Append("w\t").Append(p.name).Append('\t').Append(string.Join("\t", Array.ConvertAll(p.worn, w => w ?? ""))).Append('\n');
                        if (Array.Exists(p.hits, n => n > 0))
                            sb.Append("x\t").Append(p.name).Append('\t').Append(string.Join("\t", p.hits)).Append('\n');
                        if (p.look != null)                           // its JSON escapes tabs and newlines
                            sb.Append("l\t").Append(p.name).Append('\t').Append(p.yaw).Append('\t').Append(p.look).Append('\n');
                        if (Array.Exists(p.made, n => n > 0))
                            sb.Append("c\t").Append(p.name).Append('\t').Append(string.Join("\t", p.made)).Append('\n');
                        foreach (var kv in p.dishes) sb.Append("f\t").Append(p.name).Append('\t').Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                    }
                    if (Array.Exists(madeAll, n => n > 0)) sb.Append("k\t").Append(string.Join("\t", madeAll)).Append('\n');
                    foreach (var s in kits.Values)
                        sb.Append(FormattableString.Invariant($"s\t{s.kind}\t{s.x:0.#}\t{s.z:0.#}\t")).Append(string.Join("\t", s.made)).Append('\n');
                    foreach (var b in bosses) sb.Append(FormattableString.Invariant($"b\t{b.key}\t{b.t}\n"));
                    foreach (var d in deaths)
                        sb.Append(FormattableString.Invariant($"d\t{d.name}\t{d.x:0.#}\t{d.z:0.#}\t{d.t}\n"));
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
