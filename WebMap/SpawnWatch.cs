using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace WebMap
{
    // Where creatures come from. The server runs no creature, but every new ZDO a player's
    // game creates reaches it, and each ZDOID carries the session that made it. A creature
    // the game's own rules account for -- its spawn tables, a raid, a tame and its young, a
    // summon, a dungeon, a spawner or altar standing where it appeared -- passes; the rest are
    // listed for admins.
    //
    // The game makes a natural spawn through whichever player's game is nearest, so the
    // session alone proves nothing: what is out of place is the signal.
    //
    // Game thread, but for Json and Load, which take the gate.
    internal static class SpawnWatch
    {
        private const int MaxEntries = 200, MaxSessions = 400;
        private const float GroupS = 10f, GroupM = 32f;
        // a raid spawns 40 to 80 m from a player inside its range, a little further in a group
        private const float RaidReachM = 100f;
        private const float SourceM = 8f;           // past a spawner's own reach
        private const float SummonM = 48f;          // a summoning attack lands where it is aimed
        private const float ScanMsPerFrame = 2f;
        private const long FormerUser = 1L;         // ZDOID's own: a ZDO loaded from the save
        private const float DungeonY = 4000f;       // Location.Awake builds an interior 5000 m above its entrance
        private const float AltarM = 40f;           // OfferingBowl.m_spawnBossMaxDistance's default

        // ---------- the rules, in plain values ----------

        // One creature as it appears: what Why decides on.
        internal struct Seen
        {
            public bool tamed;           // its own flag
            public string made;          // "young", "hatched", "summoned": a kind the game makes for a player
            public int here, allowed;    // the biome bits natural spawns go by at the spot; those its spawn tables allow there
            public string raid;          // the raid running, or null
            public bool raidLists;       // that raid spawns this kind
            public float raidM, raidRange;
            public bool raidMark;        // the game's own mark on a raid's creature: the scan's raid test
            public bool inside;          // in a dungeon's interior, which the game fills from its own spawners and altar
            public string source;        // what stands within reach that makes this kind, or null
        }

        // Why the game would have made it, or null when nothing explains it.
        internal static string Why(Seen s)
        {
            if (s.tamed) return "tamed";
            if (s.made != null) return s.made;
            if (s.inside) return "dungeon";
            if ((s.here & s.allowed) != 0) return "biome";
            if (s.raid != null && s.raidLists && s.raidM <= s.raidRange + RaidReachM) return "raid";
            if (s.raidMark) return "raid";
            return s.source;
        }

        // Something in the world that makes a kind: a spawner by its own reach, or a location
        // holding one, by the location's size.
        internal struct Source { public string name; public float x, z, reach; }

        internal static string Reaches(float x, float z, List<Source> near)
        {
            foreach (var s in near)
            {
                float dx = s.x - x, dz = s.z - z, r = s.reach + SourceM;
                if (dx * dx + dz * dz <= r * r) return s.name;
            }
            return null;
        }

        internal sealed class Entry
        {
            public long t, last;                 // unix seconds: the first arrival, the latest
            public string prefab, name;          // the prefab, and the game's name for it
            public int count = 1, level;         // level 0: never set
            public float x, z;
            public string biome, place;
            public long session;                 // the game that created it; 0 for one already here at start
            public string by;                    // that session's player, when known
            public string near; public float nearM = -1f;
            public string raid; public bool raidLists;
            public bool already; public long age = -1;   // the scan's: seconds of world time since it appeared
            public bool logged;
        }

        // A burst -- one kind from one game within a few seconds and metres -- is one entry.
        internal static Entry Add(List<Entry> list, Entry e)
        {
            if (!e.already)
                for (int i = list.Count - 1; i >= 0 && i >= list.Count - 32; i--)
                {
                    var o = list[i];
                    if (o.already || o.prefab != e.prefab || o.session != e.session || e.t - o.last > GroupS) continue;
                    float dx = o.x - e.x, dz = o.z - e.z;
                    if (dx * dx + dz * dz > GroupM * GroupM) continue;
                    o.count += e.count; o.last = Math.Max(o.last, e.t);
                    return o;
                }
            list.Add(e);
            if (list.Count > MaxEntries) list.RemoveAt(0);
            return e;
        }

        // ---------- state ----------

        private static readonly object gate = new object();
        private static readonly List<Entry> entries = new List<Entry>();
        private class Who { public string name; public long t; }
        private static readonly Dictionary<long, Who> sessions = new Dictionary<long, Who>();
        private static string dir;
        private static bool dirty, sessionsDirty, warned;
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        // what the hook has cost: new ZDOs from peers, the creatures among them, Stopwatch ticks
        private static long zdos, creatures, ticks, maxTicks;
        private static int scanSeen = -1, scanListed; private static long scanMs; private static int scanFrames;

        // ZNet.RPC_CharacterID: the session behind a player's character is the session
        // behind everything their game creates.
        public static void Session(long session, string name)
        {
            if (session == 0L || string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                sessions[session] = new Who { name = Clean(name), t = Now() };
                if (sessions.Count > MaxSessions)
                {
                    long oldest = 0L, ot = long.MaxValue;
                    foreach (var kv in sessions) if (kv.Value.t < ot) { ot = kv.Value.t; oldest = kv.Key; }
                    sessions.Remove(oldest);
                }
                sessionsDirty = true;
            }
        }

        private static string NameOf(long session) { lock (gate) return sessions.TryGetValue(session, out var w) ? w.name : null; }

        // ---------- the hook ----------

        // The ZDOs a peer's batch created, kept from CreateNewZDO until the batch is read.
        private struct Fresh { public ZDO zdo; public ZDOID id; }
        private static readonly List<Fresh> fresh = new List<Fresh>();

        internal static void Created(ZDO zdo, ZDOID id) => fresh.Add(new Fresh { zdo = zdo, id = id });

        // RPC_ZDOData's end: each new ZDO's prefab is known now. One lookup for all but creatures.
        internal static void Received()
        {
            if (fresh.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var zm = ZDOMan.instance;
                foreach (var f in fresh)
                {
                    zdos++;
                    if (f.zdo == null || f.zdo.m_uid != f.id) continue;   // released to the pool since
                    Kitchen.Arrived(f.zdo);
                    Kind k = KindOf(f.zdo.GetPrefab());
                    if (k == null) continue;
                    creatures++;
                    if (zm != null && zm.m_deadZDOs.ContainsKey(f.id)) continue;   // a dead one sent again
                    Judge(f.zdo, k, false);
                }
            }
            catch (Exception e) { Warn("judging an arrival", e); }
            finally
            {
                fresh.Clear();
                long dt = Stopwatch.GetTimestamp() - t0;
                ticks += dt; if (dt > maxTicks) maxTicks = dt;
            }
        }

        private static void Warn(string what, Exception e)
        {
            if (warned) return;
            warned = true;
            ZLog.LogWarning("WebMap: spawn watch: " + what + " failed: " + e);
        }

        // ---------- what the game makes, read once ----------

        // A creature: a Character with an AI that attacks, not a player and not a build piece
        // (a training dummy). Fish, birds, deer and the like would only add noise.
        internal sealed class Kind
        {
            public int hash;
            public string prefab, name, made;
            public bool boss;
            public List<Rule> rules;                 // its spawn table entries, null for none
        }
        // A spawn table entry: the biomes it spawns in, the alternate biome it belongs to, and
        // what it waits on that the server can know -- a global key (a boss down sends its
        // biome's creatures into the others at night), a persistent event, a distance from the
        // world's centre, day or night. Weather and terrain are let pass.
        internal sealed class Rule { public int biome; public AltBiome alt; public string key, pevent; public float minC, maxC; public bool day, night; }
        private static readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private static readonly Dictionary<int, List<Rule>> rules = new Dictionary<int, List<Rule>>();
        private static readonly Dictionary<int, string> made = new Dictionary<int, string>();
        private static bool built;

        // Once per prefab, "not a creature" included.
        internal static Kind KindOf(int prefab)
        {
            if (kinds.TryGetValue(prefab, out Kind k)) return k;
            if (ZNetScene.instance == null) return null;
            if (!built) Build();
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            k = null;
            if (go != null && go.GetComponent<MonsterAI>() != null && go.GetComponent<Player>() == null && go.GetComponent<Piece>() == null)
            {
                Character c = go.GetComponent<Character>();
                if (c != null)
                {
                    k = new Kind { hash = prefab, prefab = go.name, name = Display(c.m_name, go.name), boss = c.m_boss };
                    rules.TryGetValue(prefab, out k.rules);
                    made.TryGetValue(prefab, out k.made);
                }
            }
            kinds[prefab] = k;
            return k;
        }

        // The spawn tables, the zone controller's and each alternate biome's; and the kinds the
        // game makes for a player: young (Growup, Procreation), hatched (EggGrow, EggHatch), and
        // summoned (Tameable.m_startsTamed, and what a craftable item's attack brings).
        private static void Build()
        {
            built = true;
            var sw = Stopwatch.StartNew();
            int alts = 0;
            try
            {
                var ss = ZoneSystem.instance != null && ZoneSystem.instance.m_zoneCtrlPrefab != null
                    ? ZoneSystem.instance.m_zoneCtrlPrefab.GetComponentInChildren<SpawnSystem>(true) : null;
                if (ss != null)
                    foreach (var list in ss.m_spawnLists)
                        if (list != null) Allow(list.m_spawners, null);
                foreach (var a in AltBiomeList.m_altBiomes)
                    if (a != null && a.m_enabled && a.m_spawn != null && a.m_spawn.Count > 0) { Allow(a.m_spawn, a); alts++; }
                foreach (GameObject go in ZNetScene.instance.m_namedPrefabs.Values)
                {
                    if (go == null) continue;
                    var g = go.GetComponent<Growup>();
                    if (g != null)
                    {
                        Mark(go, "young");
                        // grown without the young's tame: wild young grow up wild where they stood
                        if (!g.m_inheritTame) { Mark(g.m_grownPrefab, "young"); if (g.m_altGrownPrefabs != null) foreach (var e in g.m_altGrownPrefabs) Mark(e.m_prefab, "young"); }
                    }
                    var p = go.GetComponent<Procreation>();
                    if (p != null) { Mark(p.m_offspring, "young"); Mark(p.m_noPartnerOffspring, "young"); }
                    var eg = go.GetComponent<EggGrow>();
                    if (eg != null) Mark(eg.m_grownPrefab, "hatched");
                    var eh = go.GetComponent<EggHatch>();
                    if (eh != null) Mark(eh.m_spawnPrefab, "hatched");
                    var t = go.GetComponent<Tameable>();
                    if (t != null && t.m_startsTamed) Mark(go, "summoned");
                }
                if (ObjectDB.instance != null)
                    foreach (var r in ObjectDB.instance.m_recipes)
                    {
                        if (r == null || r.m_item == null) continue;
                        var set = new HashSet<int>();
                        Summons(r.m_item.gameObject, set, 4);
                        foreach (int h in set) if (!made.ContainsKey(h)) made[h] = "summoned";
                    }
            }
            catch (Exception e) { Warn("reading the spawn tables", e); }
            ZLog.Log($"WebMap: spawn watch: {rules.Count} creatures in the spawn tables, {alts} alternate biomes with spawns of their own, {made.Count} kinds the game makes for players; read in {sw.ElapsedMilliseconds} ms");
        }

        private static void Allow(List<SpawnSystem.SpawnData> list, AltBiome alt)
        {
            if (list == null) return;
            foreach (var d in list)
            {
                if (d == null || !d.m_enabled || d.m_prefab == null) continue;
                int h = d.m_prefab.name.GetStableHashCode();
                if (!rules.TryGetValue(h, out var mine)) rules[h] = mine = new List<Rule>();
                mine.Add(new Rule { biome = (int)d.m_biome, alt = alt, key = d.m_requiredGlobalKey, pevent = d.m_requiredPersistentEvent,
                                    minC = d.m_minDistanceFromCenter, maxC = d.m_maxDistanceFromCenter, day = d.m_spawnAtDay, night = d.m_spawnAtNight });
            }
        }

        // Whether an entry could have spawned at a spot now: frac is the day's fraction as
        // EnvMan reckons day (a quarter to three quarters), negative for the scan, which cannot
        // know the hour or the persistent event of the past and lets both pass.
        private const float DuskMargin = 0.02f;     // the time a creature takes to reach the server, and more
        private static bool Holds(Rule r, Vector3 at, float frac)
        {
            if (!string.IsNullOrEmpty(r.key) && ZoneSystem.instance != null && !ZoneSystem.instance.GetGlobalKey(r.key)) return false;
            if (frac >= 0f)
            {
                if (!string.IsNullOrEmpty(r.pevent))
                {
                    var pe = PersistentEventSystem.instance != null ? PersistentEventSystem.instance.GetActiveEvent(at) : null;
                    if (pe == null || !string.Equals(pe.internalName, r.pevent, StringComparison.OrdinalIgnoreCase)) return false;
                }
                bool day = frac > 0.25f + DuskMargin && frac < 0.75f - DuskMargin, night = frac < 0.25f - DuskMargin || frac > 0.75f + DuskMargin;
                if ((day && !r.day) || (night && !r.night)) return false;
            }
            float c = Mathf.Sqrt(at.x * at.x + at.z * at.z);
            return (r.minC <= 0f || c >= r.minC) && (r.maxC <= 0f || c <= r.maxC);
        }

        // The day's fraction as EnvMan reckons it from the shared clock, without its smoothing.
        private static float DayFraction()
        {
            if (ZNet.instance == null || EnvMan.instance == null || EnvMan.instance.m_dayLengthSec <= 0) return -1f;
            double t = ZNet.instance.GetTimeSeconds();
            return EnvMan.instance.RescaleDayFraction((float)(t % EnvMan.instance.m_dayLengthSec / EnvMan.instance.m_dayLengthSec));
        }

        private static void Mark(GameObject go, string word) { if (go != null) made[go.name.GetStableHashCode()] = word; }

        // The game's name for a creature, through Localization, which lives in an assembly the
        // build does not reference; the prefab's own name where it cannot be had.
        private static Func<string, string> localize;
        private static string Display(string token, string fallback)
        {
            try
            {
                if (localize == null)
                {
                    var type = AccessTools.TypeByName("Localization");
                    var inst = type != null ? AccessTools.Property(type, "instance")?.GetValue(null) : null;
                    var m = inst != null ? AccessTools.Method(type, "Localize", new[] { typeof(string) }) : null;
                    localize = m != null ? (s => (string)m.Invoke(inst, new object[] { s })) : (Func<string, string>)(s => s);
                }
                string n = string.IsNullOrEmpty(token) ? null : localize(token);
                return string.IsNullOrEmpty(n) || n.StartsWith("$") || n.StartsWith("[") ? fallback : n;
            }
            catch { localize = s => s; return fallback; }
        }

        // What a prefab can bring into the world, read off the components that do it: a
        // spawner's creatures, an altar's boss, what a creature's attacks summon (the Elder's
        // roots, the Queen's brood), followed through projectiles and spawn abilities.
        internal sealed class Maker { public string name; public HashSet<int> makes; public float reach; }
        private static readonly Dictionary<int, Maker> makers = new Dictionary<int, Maker>();

        private static Maker MakerOf(int prefab)
        {
            if (makers.TryGetValue(prefab, out Maker m)) return m;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            m = null;
            if (go != null)
            {
                var into = new HashSet<int>();
                float reach = 0f;
                foreach (var c in go.GetComponentsInChildren<CreatureSpawner>(true)) Add(into, c.m_creaturePrefab);
                foreach (var a in go.GetComponentsInChildren<SpawnArea>(true))
                {
                    if (a.m_prefabs != null) foreach (var d in a.m_prefabs) if (d != null) Add(into, d.m_prefab);
                    reach = Math.Max(reach, a.m_spawnRadius);
                }
                foreach (var b in go.GetComponentsInChildren<OfferingBowl>(true)) { Add(into, b.m_bossPrefab); reach = Math.Max(reach, b.m_spawnBossMaxDistance); }
                foreach (var t in go.GetComponentsInChildren<TriggerSpawner>(true)) if (t.m_creaturePrefabs != null) foreach (var p in t.m_creaturePrefabs) Add(into, p);
                foreach (var s in go.GetComponentsInChildren<SpawnPrefab>(true)) Add(into, s.m_prefab);
                var h = go.GetComponent<Humanoid>();
                if (h != null && go.GetComponent<Player>() == null)
                {
                    int had = into.Count;
                    foreach (var item in Items(h)) Summons(item, into, 4);
                    if (into.Count > had) reach = Math.Max(reach, SummonM);
                }
                into.RemoveWhere(x => x == prefab || KindOf(x) == null);
                if (into.Count > 0) m = new Maker { name = go.name, makes = into, reach = reach };
            }
            makers[prefab] = m;
            return m;
        }

        private static void Add(HashSet<int> into, GameObject go) { if (go != null) into.Add(go.name.GetStableHashCode()); }

        private static IEnumerable<GameObject> Items(Humanoid h)
        {
            foreach (var a in new[] { h.m_defaultItems, h.m_randomWeapon, h.m_randomArmor, h.m_randomShield })
                if (a != null) foreach (var g in a) yield return g;
            if (h.m_randomSets != null) foreach (var set in h.m_randomSets) if (set?.m_items != null) foreach (var g in set.m_items) yield return g;
            if (h.m_randomItems != null) foreach (var r in h.m_randomItems) if (r != null) yield return r.m_prefab;
        }

        // The creatures an item, projectile or spawn ability brings, depth links deep.
        private static void Summons(GameObject g, HashSet<int> into, int depth)
        {
            if (g == null || depth < 0) return;
            if (g.GetComponent<Character>() != null && g.GetComponent<MonsterAI>() != null) { into.Add(g.name.GetStableHashCode()); return; }
            var item = g.GetComponent<ItemDrop>();
            if (item != null && item.m_itemData?.m_shared != null)
            {
                var sh = item.m_itemData.m_shared;
                foreach (var at in new[] { sh.m_attack, sh.m_secondaryAttack })
                    if (at != null) { Summons(at.m_attackProjectile, into, depth - 1); Summons(at.m_spawnOnTrigger, into, depth - 1); Summons(at.m_spawnOnHit, into, depth - 1); }
                Summons(sh.m_spawnOnHit, into, depth - 1);
                Summons(sh.m_spawnOnHitTerrain, into, depth - 1);
            }
            foreach (var sa in g.GetComponentsInChildren<SpawnAbility>(true))
                if (sa.m_spawnPrefab != null) foreach (var p in sa.m_spawnPrefab) Summons(p, into, depth - 1);
            foreach (var pr in g.GetComponentsInChildren<Projectile>(true))
            {
                Summons(pr.m_spawnOnHit, into, depth - 1);
                if (pr.m_randomSpawnOnHit != null) foreach (var p in pr.m_randomSpawnOnHit) Summons(p, into, depth - 1);
            }
            foreach (var ao in g.GetComponentsInChildren<Aoe>(true)) Summons(ao.m_spawnOnHitTerrain, into, depth - 1);
        }

        // ---------- judging one ----------

        private static readonly List<Source> near = new List<Source>();

        private static void Judge(ZDO zdo, Kind k, bool scan)
        {
            // where its AI woke is where it appeared; a creature walks off afterwards
            Vector3 at = zdo.GetVec3(ZDOVars.s_spawnPoint, zdo.GetPosition());
            var s = new Seen { tamed = zdo.GetBool(ZDOVars.s_tamed), made = k.made };
            float frac = scan ? -1f : DayFraction();
            Spot(at, k, frac, out s.here, out s.allowed);
            s.inside = at.y > DungeonY;
            RandomEvent ev = RandEventSystem.instance != null ? RandEventSystem.instance.m_randomEvent : null;
            if (scan) s.raidMark = zdo.GetBool(ZDOVars.s_eventCreature);   // the past's raid is not known, only its mark
            else if (ev != null)
            {
                s.raid = ev.m_name; s.raidRange = ev.m_eventRange;
                s.raidM = XZ(at, ev.m_pos);
                if (ev.m_spawn != null) foreach (var d in ev.m_spawn) if (d?.m_prefab != null && d.m_prefab.name.GetStableHashCode() == k.hash) { s.raidLists = true; break; }
            }
            long session = scan ? 0L : zdo.m_uid.UserID;
            string why = Why(s);
            if (why == null) { s.source = Reaches(at.x, at.z, SourcesNear(k, at)) ?? (scan ? SummonedHere(k, at) : null); why = Why(s); }
            if (why != null)
            {
                if (WebMapConfig.DEBUG && !scan) ZLog.Log(FormattableString.Invariant($"WebMap: spawn watch: {k.prefab} from session {session} at {at.x:0}, {at.z:0}: {why}"));
                return;
            }

            int level = zdo.GetInt(ZDOVars.s_level, 0);
            var e = new Entry
            {
                t = Now(), prefab = k.prefab, name = k.name, level = level, x = at.x, z = at.z,
                biome = BiomeAt(at), place = Features.PlaceAt(at.x, at.z),
                session = session, by = scan ? null : NameOf(session), raid = s.raid, raidLists = s.raidLists, already = scan,
            };
            e.last = e.t;
            if (scan)
            {
                long born = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
                if (born > 0L && ZNet.instance != null) e.age = Math.Max(0L, (ZNet.instance.GetTime().Ticks - born) / TimeSpan.TicksPerSecond);
            }
            else Nearest(at, out e.near, out e.nearM);
            lock (gate) { Add(entries, e); dirty = true; }
        }

        private static float XZ(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        // Natural spawns go by the biome a zone's heightmap gives a spot, weighted between the
        // biomes at its four corners: any of the four is a biome the game could have spawned by.
        private static readonly List<AltBiome> altsHere = new List<AltBiome>();
        private static void Spot(Vector3 at, Kind k, float frac, out int here, out int ok)
        {
            here = ok = 0;
            var wg = WorldGenerator.instance;
            if (wg == null) { here = ok = ~0; return; }    // no world to judge by: everything passes
            Vector3 c = ZoneSystem.GetZonePos(ZoneSystem.GetZone(at));
            altsHere.Clear();
            for (int i = 0; i < 4; i++)
            {
                BiomeSector b = wg.GetBiomeSector(c.x + ((i & 1) == 0 ? -32f : 32f), c.z + ((i & 2) == 0 ? -32f : 32f));
                if (b == null) continue;
                here |= (int)b.Biome;
                if (b.AltBiomes != null) altsHere.AddRange(b.AltBiomes);
            }
            if (k.rules != null)
                foreach (var r in k.rules)
                    if ((r.alt == null || altsHere.Contains(r.alt)) && (r.biome & here) != 0 && Holds(r, at, frac)) ok |= r.biome;
        }

        private static string BiomeAt(Vector3 at)
        {
            var wg = WorldGenerator.instance;
            int b = wg != null ? (int)wg.GetBiomeSector(at.x, at.z).Biome : 0;
            if (b == 0) return null;
            byte c = 1;
            while ((b & 1) == 0) { b >>= 1; c++; }
            return Features.BiomeName(c);
        }

        // The makers of a kind in the zones around a spot. One standing in a location reaches
        // across the location, by the location's size: a draugr village's draugr anywhere in it.
        // A boss's altar is no object of its own -- each game builds it from the location -- so a
        // boss is the game's within its summoning distance of a location.
        private static List<Source> SourcesNear(Kind k, Vector3 at)
        {
            near.Clear();
            var zm = ZDOMan.instance;
            if (zm == null || zm.m_objectsBySector == null) return near;
            Vector2s zone = ZoneSystem.GetZone(at);
            var zs = ZoneSystem.instance;
            if (k.boss && zs != null && zs.m_locationInstances != null)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (zs.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var li) && li.m_location != null)
                            near.Add(new Source { name = li.m_location.m_prefabName, x = li.m_position.x, z = li.m_position.z, reach = li.m_location.m_exteriorRadius + AltarM });
            int hash = k.hash;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = zone.x + dx, sz = zone.y + dz;
                    if (sx < -256 || sx >= 256 || sz < -256 || sz >= 256) continue;
                    var list = zm.m_objectsBySector[ZoneSystem.SectorToIndex(sx, sz).Sector];
                    if (list == null) continue;
                    foreach (ZDO o in list)
                    {
                        Maker m = MakerOf(o.GetPrefab());
                        if (m == null || !m.makes.Contains(hash)) continue;
                        Vector3 p = o.GetPosition();
                        if (LocationOf(p, out var li))
                            near.Add(new Source { name = li.m_location.m_prefabName, x = li.m_position.x, z = li.m_position.z, reach = li.m_location.m_exteriorRadius });
                        near.Add(new Source { name = m.name, x = p.x, z = p.z, reach = m.reach });
                    }
                }
            return near;
        }

        // What a creature's attack brings outlives it -- a Gjall's ticks stay where they fell --
        // so for the scan a kind a creature summons is the game's where that creature's own
        // spawn tables allow it.
        private static Dictionary<int, List<Kind>> summoners;
        private static string SummonedHere(Kind k, Vector3 at)
        {
            if (summoners == null)
            {
                summoners = new Dictionary<int, List<Kind>>();
                if (ZNetScene.instance != null)
                    foreach (GameObject go in new List<GameObject>(ZNetScene.instance.m_namedPrefabs.Values))
                    {
                        if (go == null) continue;
                        int h = go.name.GetStableHashCode();
                        Kind by = KindOf(h);
                        Maker m = by != null ? MakerOf(h) : null;
                        if (m == null) continue;
                        foreach (int p in m.makes) { if (!summoners.TryGetValue(p, out var l)) summoners[p] = l = new List<Kind>(); l.Add(by); }
                    }
            }
            if (!summoners.TryGetValue(k.hash, out var list)) return null;
            foreach (var by in list) { Spot(at, by, -1f, out int here, out int ok); if ((here & ok) != 0) return by.prefab; }
            return null;
        }

        private static bool LocationOf(Vector3 p, out ZoneSystem.LocationInstance found)
        {
            found = default;
            var zs = ZoneSystem.instance;
            if (zs == null || zs.m_locationInstances == null) return false;
            Vector2s zone = ZoneSystem.GetZone(p);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (zs.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var li) && li.m_location != null
                        && XZ(p, li.m_position) <= li.m_location.m_exteriorRadius + SourceM) { found = li; return true; }
            return false;
        }

        private static void Nearest(Vector3 at, out string who, out float m)
        {
            who = null; m = -1f;
            if (ZNet.instance == null || ZDOMan.instance == null) return;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (string.IsNullOrEmpty(peer.m_playerName)) continue;
                ZDO me = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (me == null) continue;
                float d = XZ(me.GetPosition(), at);
                if (m < 0f || d < m) { m = d; who = peer.m_playerName; }
            }
        }

        // ---------- what is already there ----------

        // The first sweep's walk hands over the creatures loaded from the save (the game forgets
        // who made a ZDO as it loads one); they are judged a few a frame once the walk is done.
        private static readonly List<ZDOID> loaded = new List<ZDOID>();
        public static bool Scanning => scanSeen < 0;

        public static void BeginWalk() => loaded.Clear();

        public static void Walked(ZDO zdo, int prefab)
        {
            try { if (KindOf(prefab) != null && zdo.m_uid.UserID == FormerUser) loaded.Add(zdo.m_uid); }
            catch (Exception e) { Warn("reading the sweep", e); }
        }

        public static void EndWalk()
        {
            if (!Scanning) return;
            scanSeen = loaded.Count;
            StaticCoroutine.Start(Scan(new List<ZDOID>(loaded)));
            loaded.Clear();
        }

        private static IEnumerator Scan(List<ZDOID> ids)
        {
            var total = Stopwatch.StartNew();
            var busy = new Stopwatch();
            lock (gate) { entries.RemoveAll(e => e.already); dirty = true; }   // this start's view replaces the last one's
            for (int i = 0; i < ids.Count;)
            {
                busy.Start();
                var frame = Stopwatch.StartNew();
                for (; i < ids.Count && frame.Elapsed.TotalMilliseconds < ScanMsPerFrame; i++)
                {
                    try
                    {
                        ZDO z = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(ids[i]) : null;
                        Kind k = z != null ? KindOf(z.GetPrefab()) : null;
                        if (k != null) Judge(z, k, true);
                    }
                    catch (Exception e) { Warn("judging what was already there", e); }
                }
                busy.Stop(); scanFrames++;
                yield return null;
            }
            scanMs = busy.ElapsedMilliseconds;
            var listed = new List<Entry>();
            lock (gate) foreach (var e in entries) if (e.already && !e.logged) { e.logged = true; listed.Add(e); }
            scanListed = listed.Count;
            foreach (var e in listed) ZLog.Log("WebMap: out of place: " + Line(e));
            ZLog.Log($"WebMap: spawn watch: {ids.Count} creatures already here at start, {listed.Count} entries out of place; judged in {scanMs} ms over {scanFrames} frames, {total.ElapsedMilliseconds} ms wall");
        }

        // ---------- the log, the file, the route ----------

        // The snapshot's second: a burst is logged once it has stopped growing, and the files
        // written when they changed.
        public static void Tick()
        {
            long now = Now();
            List<Entry> closed = null;
            bool save, saveSessions;
            lock (gate)
            {
                foreach (var e in entries)
                    if (!e.logged && !e.already && now - e.last > GroupS) { e.logged = true; (closed ??= new List<Entry>()).Add(e); }
                save = dirty; saveSessions = sessionsDirty;
                dirty = sessionsDirty = false;
            }
            if (closed != null) foreach (var e in closed) ZLog.Log("WebMap: out of place: " + Line(e));
            if (save) Save();
            if (saveSessions) SaveSessions();
        }

        internal static string Line(Entry e)
        {
            var sb = new StringBuilder();
            sb.Append(e.count).Append(' ').Append(e.prefab);
            if (e.name != e.prefab) sb.Append(" (").Append(e.name).Append(')');
            if (e.level > 1) sb.Append(" level ").Append(e.level);
            sb.Append(FormattableString.Invariant($" at {e.x:0}, {e.z:0}"));
            if (e.biome != null) sb.Append(" in the ").Append(e.biome);
            if (e.place != null) sb.Append(" (").Append(e.place).Append(')');
            if (e.already) sb.Append("; already here at start");
            else
            {
                sb.Append("; created by session ").Append(e.session);
                if (e.by != null) sb.Append(" (").Append(e.by).Append(')');
                if (e.near != null) sb.Append(FormattableString.Invariant($"; nearest player {e.near}, {e.nearM:0} m"));
                sb.Append(e.raid == null ? "; no raid running" : "; raid " + e.raid + (e.raidLists ? " (this kind among its spawns, too far)" : " (not among its spawns)"));
            }
            return sb.ToString();
        }

        private static string Esc(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public static string Json()
        {
            var sb = new StringBuilder("{\"spawns\":[");
            lock (gate)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (i < entries.Count - 1) sb.Append(',');
                    sb.Append(FormattableString.Invariant($"{{\"t\":{e.t},\"last\":{e.last},\"prefab\":{Esc(e.prefab)},\"name\":{Esc(e.name)},\"count\":{e.count},\"level\":{e.level}"));
                    // the geography is read a minute after a start: a creature before it is named now
                    sb.Append(FormattableString.Invariant($",\"x\":{e.x:0.#},\"z\":{e.z:0.#},\"biome\":{Esc(e.biome)},\"place\":{Esc(e.place ??= Features.PlaceAt(e.x, e.z))}"));
                    sb.Append(",\"session\":").Append(e.session != 0L ? "\"" + e.session.ToString(CultureInfo.InvariantCulture) + "\"" : "null")
                      .Append(",\"by\":").Append(Esc(e.by)).Append(",\"near\":").Append(Esc(e.near))
                      .Append(",\"near_m\":").Append(e.nearM >= 0f ? Math.Round(e.nearM).ToString(CultureInfo.InvariantCulture) : "null")
                      .Append(",\"event\":").Append(Esc(e.raid)).Append(",\"in_event\":").Append(e.raidLists ? "true" : "false")
                      .Append(",\"already\":").Append(e.already ? "true" : "false")
                      .Append(",\"age_s\":").Append(e.age >= 0 ? e.age.ToString(CultureInfo.InvariantCulture) : "null").Append('}');
                }
                sb.Append("],\"count\":").Append(entries.Count);
            }
            double ms = ticks * 1000.0 / Stopwatch.Frequency, max = maxTicks * 1000.0 / Stopwatch.Frequency;
            sb.Append(FormattableString.Invariant($",\"watch\":{{\"zdos\":{zdos},\"creatures\":{creatures},\"ms\":{ms:0.###},\"max_ms\":{max:0.###}"));
            sb.Append(scanSeen < 0 ? ",\"scan\":null}}" : FormattableString.Invariant($",\"scan\":{{\"creatures\":{scanSeen},\"listed\":{scanListed},\"ms\":{scanMs},\"frames\":{scanFrames}}}}}}}"));
            return sb.ToString();
        }

        // ---------- the files ----------

        public static void Load(string worldDataPath)
        {
            lock (gate)
            {
                dir = worldDataPath;
                entries.Clear(); sessions.Clear(); dirty = sessionsDirty = false;
                try
                {
                    string p = Path.Combine(dir, "sessions.tsv");
                    if (File.Exists(p))
                        foreach (string line in File.ReadAllLines(p))
                        {
                            var f = line.Split('\t');
                            if (f.Length >= 3 && long.TryParse(f[0], out long id) && long.TryParse(f[2], out long t)) sessions[id] = new Who { name = f[1], t = t };
                        }
                    p = Path.Combine(dir, "spawns.tsv");
                    if (File.Exists(p))
                        foreach (string line in File.ReadAllLines(p))
                        {
                            var f = line.Split('\t');
                            if (f.Length < 18) continue;
                            var e = new Entry { prefab = f[2], name = f[3], biome = Opt(f[8]), place = Opt(f[9]), by = Opt(f[11]), near = Opt(f[12]), raid = Opt(f[14]), logged = true };
                            long.TryParse(f[0], out e.t); long.TryParse(f[1], out e.last);
                            int.TryParse(f[4], out e.count); int.TryParse(f[5], out e.level);
                            float.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out e.x);
                            float.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out e.z);
                            long.TryParse(f[10], out e.session);
                            float.TryParse(f[13], NumberStyles.Float, CultureInfo.InvariantCulture, out e.nearM);
                            e.raidLists = f[15] == "1"; e.already = f[16] == "1"; long.TryParse(f[17], out e.age);
                            entries.Add(e);
                        }
                    while (entries.Count > MaxEntries) entries.RemoveAt(0);
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: spawn watch not loaded: " + e.Message); }
            }
        }
        private static string Opt(string s) => s.Length == 0 ? null : s;

        // the server going down: what changed since the last second
        public static void Flush() { Save(); SaveSessions(); }

        private static void Save()
        {
            string text;
            lock (gate)
            {
                if (dir == null) return;
                var sb = new StringBuilder();
                foreach (var e in entries)
                    sb.Append(FormattableString.Invariant($"{e.t}\t{e.last}\t{Clean(e.prefab)}\t{Clean(e.name)}\t{e.count}\t{e.level}\t{e.x:0.#}\t{e.z:0.#}\t{Clean(e.biome)}\t{Clean(e.place)}\t{e.session}\t{Clean(e.by)}\t{Clean(e.near)}\t{e.nearM:0.#}\t{Clean(e.raid)}\t{(e.raidLists ? 1 : 0)}\t{(e.already ? 1 : 0)}\t{e.age}\n"));
                text = sb.ToString();
            }
            Write("spawns.tsv", text);
        }

        private static void SaveSessions()
        {
            string text;
            lock (gate)
            {
                if (dir == null) return;
                var sb = new StringBuilder();
                foreach (var kv in sessions) sb.Append(kv.Key).Append('\t').Append(kv.Value.name).Append('\t').Append(kv.Value.t).Append('\n');
                text = sb.ToString();
            }
            Write("sessions.tsv", text);
        }

        private static void Write(string name, string text)
        {
            try
            {
                string p = Path.Combine(dir, name), tmp = p + ".new";
                File.WriteAllText(tmp, text);
                if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: " + name + " not saved: " + e.Message); }
        }

        // ---------- for the tests (WebMap.Tests) ----------
        internal static void ResetForTests(string worldDataPath) { Load(worldDataPath); scanSeen = -1; }
        internal static void AddForTests(Entry e) { lock (gate) { Add(entries, e); dirty = true; } }
    }

    // Every ZDO the server creates, its own and each a peer tells it of; a peer's are kept for
    // the batch's end, when their prefab is known. The server's own session is the game's doing.
    [HarmonyPatch(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(ZDOID), typeof(Vector3), typeof(int) })]
    internal class SpawnWatchCreatePatch
    {
        private static void Postfix(ZDOID uid, ZDO __result) { if (uid.UserID != ZDOMan.GetSessionID()) SpawnWatch.Created(__result, uid); }
    }

    [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
    internal class SpawnWatchDataPatch
    {
        private static void Postfix() => SpawnWatch.Received();
    }

    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    internal class SpawnWatchCharacterPatch
    {
        private static void Postfix(ZRpc rpc, ZDOID characterID)
        {
            try
            {
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(rpc) : null;
                if (peer != null) SpawnWatch.Session(characterID.UserID, peer.m_playerName);
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: spawn watch: a character's session not kept: " + e.Message); }
        }
    }
}
