using System;
using System.Collections.Generic;
using System.Text;

namespace WebMap
{
    // The traders: world-generated, never moving, and pinned on a player's own map
    // once they have been near. Reported only in explored ground, which is the
    // same bar -- somebody has walked there.
    internal static class Traders
    {
        private struct Entry { public string name; public float x, z; }
        // Written once by the scan on the game thread, read by HTTP: published whole,
        // never filled in place, so a read cannot walk a list mid-write.
        private static volatile Entry[] found = new Entry[0];
        private static bool scanned;

        // The camp each one keeps shop in, by name. Hildir's three quest sites
        // (Hildir_cave, Hildir_crypt, Hildir_plainsfortress) carry her name too and
        // are nowhere near her, so a prefix match puts her in three wrong places.
        private static string Label(string prefab)
        {
            switch ((prefab ?? "").ToLowerInvariant())
            {
                case "vendor_blackforest": return "Haldor";
                case "hildir_camp":        return "Hildir";
                case "bogwitch_camp":      return "Bog Witch";
                default:                   return null;
            }
        }

        // Game thread, from the sweep: the location list exists once the world has
        // generated, and it is walked by the game, so it is read where the game is.
        public static void ScanIfNeeded()
        {
            if (scanned) return;
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || zs.m_locationInstances == null || zs.m_locationInstances.Count == 0) return;
                var list = new List<Entry>();
                foreach (var li in zs.m_locationInstances.Values)
                {
                    string label = Label(li.m_location != null ? li.m_location.m_prefabName : null);
                    if (label == null) continue;
                    list.Add(new Entry { name = label, x = li.m_position.x, z = li.m_position.z });
                }
                found = list.ToArray();
                scanned = true;
                ZLog.Log($"WebMap: {list.Count} trader locations in this world: "
                         + string.Join(", ", list.ConvertAll(e => e.name).ToArray()));
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: trader scan failed: " + e.Message); }
        }

        // Fog-gated when read: the fog moves, the traders do not.
        public static string Json()
        {
            var sb = new StringBuilder("{\"traders\":[");
            int n = 0;
            foreach (var e in found)
            {
                if (!MapFog.Explored(e.x, e.z)) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append(FormattableString.Invariant($"{{\"name\":\"{e.name}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#}}}"));
            }
            return sb.Append("],\"count\":").Append(n).Append('}').ToString();
        }
    }
}
