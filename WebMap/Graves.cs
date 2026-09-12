using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WebMap
{
    // Graves that still exist, and whose they are.
    //
    // This is the current state of the world rather than a log of deaths: a
    // tombstone appears when someone dies and disappears when it is emptied, so
    // what the map shows is where somebody's gear is still lying.
    //
    // Spawned by the game rather than placed by a player, so unlike a portal a
    // grave carries no creator and has to be recognised before that test.
    internal static class Graves
    {
        private struct Entry { public string name; public float x, z; public long age; }

        private static readonly Dictionary<int, bool> isGrave = new Dictionary<int, bool>();
        private static readonly List<Entry> found = new List<Entry>();
        private static string json = "{\"graves\":[],\"count\":0}";

        public static bool IsGrave(int prefabHash)
        {
            if (isGrave.TryGetValue(prefabHash, out bool cached)) return cached;
            bool v = false;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                v = go != null && go.GetComponent<TombStone>() != null;
            }
            catch { }
            isGrave[prefabHash] = v;
            return v;
        }

        public static void Begin() => found.Clear();

        public static void Observe(ZDO zdo, Vector3 pos)
        {
            string name = "";
            long age = -1;
            try { name = zdo.GetString(ZDOVars.s_ownerName, "") ?? ""; } catch { }
            try
            {
                // Ticks of the world's own clock, not wall time, so it is compared
                // against the same clock rather than against DateTime.Now.
                long died = zdo.GetLong(ZDOVars.s_timeOfDeath, 0L);
                if (died > 0L && ZNet.instance != null)
                    age = (ZNet.instance.GetTime().Ticks - died) / System.TimeSpan.TicksPerSecond;
            }
            catch { }
            found.Add(new Entry { name = name, x = pos.x, z = pos.z, age = age });
        }

        public static void Finish()
        {
            var sb = new StringBuilder();
            sb.Append("{\"graves\":[");
            int n = 0;
            foreach (var e in found)
            {
                if (!MapFog.Explored(e.x, e.z)) continue;
                if (n > 0) sb.Append(",");
                n++;
                string name = e.name.Replace("\\", "").Replace("\"", "");
                sb.Append(System.FormattableString.Invariant(
                    $"{{\"name\":\"{name}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#},\"age\":{e.age}}}"));
            }
            sb.Append("],\"count\":").Append(n).Append("}");
            json = sb.ToString();
        }

        public static string GetJson() => json;
    }
}
