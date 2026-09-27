using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebMap
{
    // Cooking, brewing, honey and smelting, per player. Nobody's inventory reaches the
    // server, but the stations are world objects whose ZDOs sync: a cooking slot's item
    // and state, a fermenter's content, a hive's honey, a smelter's queue. The sweep hands
    // over the stations it walks past, a poll every few seconds diffs them, and each change
    // is credited to whoever put the thing there.
    //
    // A cooking station names its placer: its owner broadcasts every slot it fills, and a
    // player who is not the owner asks it first, through the server. The rest go to the
    // nearest player standing beside the station, as a rock goes to the owner beside it.
    //
    // Game thread only: the sweep's walk, RouteRPC and the snapshot all run there.
    internal static class Kitchen
    {
        public enum Type { Cook, Brew, Hive, Smelt }
        public enum Made { Cooked, Burnt, Brewed, Honey, Smelted }
        public static readonly string[] MadeNames = { "cooked", "burnt", "brewed", "honey", "smelted" };

        // What a station's prefab turns things into, read once off its component.
        internal sealed class Kind
        {
            public Type type; public string name; public int slots;
            public Dictionary<string, string> to;                       // a cooking station's or a smelter's: item -> product
            public Dictionary<int, KeyValuePair<string, int>> brews;    // a fermenter's: base hash -> mead, how many a tap drops
        }

        private sealed class Station
        {
            public ZDOID id; public Kind kind; public float x, z; public bool seen;
            // a cooking slot: its item (null until first read), cooked time, status (0 raw, 1 done, 2 burnt),
            // who placed it, who placed the next one, whether its outcome is still to count, the dish counted
            public string[] item, placer, pending, dish; public float[] time; public int[] status; public bool[] open;
            public string asker; public float askedAt;          // a cross-owner RPC_AddItem, awaiting the owner's slot
            public int value; public string filler;             // a fermenter's content or a hive's level
            public List<string> loaders, ores;                  // a smelter's queue, oldest first
        }

        private const int MaxStations = 1024, MaxSlots = 8, MaxQueue = 64;
        private const float PollS = 5f, AskS = 2f, BesideM = 8f;
        private static readonly int[] SlotKey = Keys("slot", MaxSlots), StatusKey = Keys("slotstatus", MaxSlots), ItemKey = Keys("item", MaxQueue);
        private static int[] Keys(string stem, int n) { var k = new int[n]; for (int i = 0; i < n; i++) k[i] = (stem + i).GetStableHashCode(); return k; }

        private static readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private static readonly Dictionary<ZDOID, Station> stations = new Dictionary<ZDOID, Station>();
        private static readonly List<ZDOID> gone = new List<ZDOID>();
        private static readonly string[] queue = new string[MaxQueue];
        private static float polled = float.NegativeInfinity;
        private static bool warned;

        // the nearest player within reach of a spot, or null
        internal static Func<float, float, string> Beside = Nearest;

        // The sweep's walk, for each placed piece; a throw here must not end the walk.
        public static void Found(ZDO zdo, int prefab, Vector3 at)
        {
            try
            {
                Kind k = KindOf(prefab);
                if (k != null && !stations.ContainsKey(zdo.m_uid)) Watch(zdo.m_uid, k, at.x, at.z);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; ZLog.LogWarning("WebMap: kitchen: reading a station's prefab failed: " + e); }
            }
        }

        internal static void Watch(ZDOID id, Kind kind, float x, float z)
        {
            if (stations.Count >= MaxStations) return;
            var st = new Station { id = id, kind = kind, x = x, z = z };
            if (kind.type == Type.Cook)
            {
                int n = kind.slots;
                st.item = new string[n]; st.placer = new string[n]; st.pending = new string[n]; st.dish = new string[n];
                st.time = new float[n]; st.status = new int[n]; st.open = new bool[n];
            }
            else if (kind.type == Type.Smelt) { st.loaders = new List<string>(MaxQueue); st.ores = new List<string>(MaxQueue); }
            stations[id] = st;
        }

        // Once per prefab, "none" included.
        internal static Kind KindOf(int prefab)
        {
            if (kinds.TryGetValue(prefab, out Kind k)) return k;
            if (ZNetScene.instance == null) return null;
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            k = go != null ? Read(go) : null;
            kinds[prefab] = k;
            return k;
        }

        private static Kind Read(GameObject go)
        {
            CookingStation cs = go.GetComponentInChildren<CookingStation>(true);
            if (cs != null)
            {
                var k = new Kind { type = Type.Cook, name = go.name, slots = Math.Min(MaxSlots, cs.m_slots != null ? cs.m_slots.Length : 0), to = new Dictionary<string, string>() };
                foreach (var c in cs.m_conversion) if (c.m_from != null && c.m_to != null) k.to[c.m_from.gameObject.name] = c.m_to.gameObject.name;
                return k.slots > 0 ? k : null;
            }
            Fermenter f = go.GetComponentInChildren<Fermenter>(true);
            if (f != null)
            {
                var k = new Kind { type = Type.Brew, name = go.name, brews = new Dictionary<int, KeyValuePair<string, int>>() };
                foreach (var c in f.m_conversion)
                    if (c.m_from != null && c.m_to != null) k.brews[c.m_from.gameObject.name.GetStableHashCode()] = new KeyValuePair<string, int>(c.m_to.gameObject.name, c.m_producedItems);
                return k;
            }
            if (go.GetComponentInChildren<Beehive>(true) != null) return new Kind { type = Type.Hive, name = go.name };
            Smelter s = go.GetComponentInChildren<Smelter>(true);
            if (s != null)
            {
                var k = new Kind { type = Type.Smelt, name = go.name, to = new Dictionary<string, string>() };
                foreach (var c in s.m_conversion) if (c.m_from != null && c.m_to != null) k.to[c.m_from.gameObject.name] = c.m_to.gameObject.name;
                return k;
            }
            return null;
        }

        // The snapshot's second; a poll every few. Only what the ZDO hands back is new each time.
        public static void Tick(float now)
        {
            if (now - polled < PollS) return;
            polled = now;
            ZDOMan zm = ZDOMan.instance;
            if (zm == null) return;
            foreach (Station st in stations.Values)
            {
                try
                {
                    ZDO z = zm.GetZDO(st.id);
                    if (z == null) { gone.Add(st.id); continue; }
                    switch (st.kind.type)
                    {
                        case Type.Cook:
                            for (int i = 0; i < st.kind.slots; i++) Slot(st, i, z.GetString(SlotKey[i]), z.GetFloat(SlotKey[i]), z.GetInt(StatusKey[i]));
                            break;
                        case Type.Brew: Brew(st, z.GetInt(ZDOVars.s_content)); break;
                        case Type.Hive: Hive(st, z.GetInt(ZDOVars.s_level)); break;
                        case Type.Smelt:
                            int q = Math.Min(MaxQueue, Math.Max(0, z.GetInt(ZDOVars.s_queued)));
                            if (st.seen && q == st.ores.Count) break;
                            for (int i = 0; i < q; i++) queue[i] = z.GetString(ItemKey[i]);
                            Smelt(st, q, queue);
                            break;
                    }
                }
                catch (Exception e)
                {
                    if (!warned) { warned = true; ZLog.LogWarning("WebMap: kitchen: reading a station failed: " + e); }
                }
            }
            foreach (var id in gone) stations.Remove(id);
            gone.Clear();
        }

        // RouteRPC: a player who is not the owner asks the owner to cook something.
        public static void Asked(ZDOID id, string who, float now)
        {
            Station st = Find(id);
            if (st == null || st.kind.type != Type.Cook || string.IsNullOrEmpty(who)) return;
            st.asker = who; st.askedAt = now;
        }

        // RouteRPC: the owner shows a slot filled -- for whoever asked just now, else for itself.
        public static void Shown(ZDOID id, string owner, int slot, string item, float now)
        {
            if (string.IsNullOrEmpty(item)) return;           // emptied: the poll sees what came out
            Station st = Find(id);
            if (st == null || st.kind.type != Type.Cook || slot < 0 || slot >= st.kind.slots) return;
            st.pending[slot] = st.asker != null && now - st.askedAt <= AskS ? st.asker : owner;
            st.asker = null;
        }

        // A station first used between two sweeps is watched from its first RPC.
        private static Station Find(ZDOID id)
        {
            if (stations.TryGetValue(id, out Station st)) return st;
            ZDO z = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            Kind k = z != null ? KindOf(z.GetPrefab()) : null;
            if (k == null) return null;
            Vector3 p = z.GetPosition();
            Watch(id, k, p.x, p.z);
            return stations.TryGetValue(id, out st) ? st : null;
        }

        // The poll's reading, fed plain: a cooking slot; a fermenter's content or a hive's level; a smelter's queue.
        internal static void Observe(ZDOID id, int slot, string item, float time, int status) { if (stations.TryGetValue(id, out Station st)) Slot(st, slot, item, time, status); }
        internal static void Observe(ZDOID id, int value)
        {
            if (!stations.TryGetValue(id, out Station st)) return;
            if (st.kind.type == Type.Brew) Brew(st, value); else Hive(st, value);
        }
        internal static void Observe(ZDOID id, string[] items) { if (stations.TryGetValue(id, out Station st)) Smelt(st, items.Length, items); }

        // The same item cooks on with its time and status rising; anything else in the slot
        // means the last one was taken out and another put in.
        private static void Slot(Station st, int i, string item, float time, int status)
        {
            item = item ?? "";
            string was = st.item[i];
            if (was == null)                                  // first read: what cooks already counts for the world alone
            {
                if (item.Length > 0) { st.placer[i] = st.pending[i]; st.pending[i] = null; st.open[i] = status == 0; }
                Set(st, i, item, time, status);
                return;
            }
            bool same = was.Length > 0 && item.Length > 0 && time >= st.time[i] && (status > st.status[i] || (status == st.status[i] && item == was));
            if (was.Length > 0 && !same) Taken(st, i);
            if (item.Length > 0)
            {
                if (!same) { st.placer[i] = st.pending[i]; st.pending[i] = null; st.open[i] = true; st.dish[i] = null; }
                if (status == 1 && st.open[i]) { st.open[i] = false; st.dish[i] = item; Credit(st, st.placer[i], Made.Cooked, item, 1); }
                else if (status == 2 && (st.open[i] || st.dish[i] != null))
                {
                    if (st.dish[i] != null) Credit(st, st.placer[i], Made.Cooked, st.dish[i], -1);   // done, then left on the fire
                    st.open[i] = false; st.dish[i] = null;
                    Credit(st, st.placer[i], Made.Burnt, null, 1);
                }
            }
            Set(st, i, item, time, status);
        }

        // Out of the slot between two polls. Only a done or burnt item comes out, so one last seen raw cooked.
        private static void Taken(Station st, int i)
        {
            if (st.open[i] && st.status[i] == 0)
            {
                st.kind.to.TryGetValue(st.item[i], out string dish);
                Credit(st, st.placer[i], Made.Cooked, dish, 1);
            }
            st.open[i] = false; st.dish[i] = null; st.placer[i] = null;
        }

        private static void Set(Station st, int i, string item, float time, int status) { st.item[i] = item; st.time[i] = time; st.status[i] = status; }

        // Filled: content from nothing to a base. Tapped: back to nothing, the meads dropped.
        private static void Brew(Station st, int content)
        {
            if (st.seen && content != st.value)
            {
                if (st.value != 0 && st.kind.brews.TryGetValue(st.value, out var mead))
                    Credit(st, st.filler ?? Beside(st.x, st.z), Made.Brewed, mead.Key, mead.Value);
                st.filler = content != 0 ? Beside(st.x, st.z) : null;
            }
            st.value = content; st.seen = true;
        }

        private static void Hive(Station st, int level)
        {
            if (st.seen && level < st.value) Credit(st, Beside(st.x, st.z), Made.Honey, null, st.value - level);
            st.value = level; st.seen = true;
        }

        // A rise is ore loaded by whoever stands there; a fall is the oldest processed.
        private static void Smelt(Station st, int queued, string[] items)
        {
            int had = st.ores.Count;
            if (!st.seen || queued > had)
            {
                string who = st.seen ? Beside(st.x, st.z) : null;
                for (int i = had; i < queued; i++) { st.loaders.Add(who); st.ores.Add(items[i] ?? ""); }
                st.seen = true;
                return;
            }
            string near = null; bool looked = false;
            for (int i = queued; i < had; i++)
            {
                string who = st.loaders[0], ore = st.ores[0];
                st.loaders.RemoveAt(0); st.ores.RemoveAt(0);
                if (who == null) { if (!looked) { near = Beside(st.x, st.z); looked = true; } who = near; }
                st.kind.to.TryGetValue(ore, out string bar);
                Credit(st, who, Made.Smelted, bar, 1);
            }
        }

        private static void Credit(Station st, string who, Made what, string item, int n) => Stats.Made(who, what, item, n, st.kind.name, st.x, st.z);

        private static string Nearest(float x, float z)
        {
            if (ZNet.instance == null || ZDOMan.instance == null) return null;
            string best = null;
            float bd = BesideM * BesideM;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (string.IsNullOrEmpty(peer.m_playerName)) continue;
                ZDO me = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (me == null) continue;
                Vector3 p = me.GetPosition();
                float dx = p.x - x, dz = p.z - z, d = dx * dx + dz * dz;
                if (d <= bd) { bd = d; best = peer.m_playerName; }
            }
            return best;
        }

        internal static void ResetForTests() { stations.Clear(); kinds.Clear(); gone.Clear(); polled = float.NegativeInfinity; Beside = Nearest; }
    }
}
