using System;
using System.Collections.Generic;
using System.Text;

namespace WebMap
{
    // The world's locations -- altars, crypts, caves, camps, ruins, wrecks, runestones -- where
    // the generator put them, for the tour to visit. Told only in walked ground, the site's
    // honour rule: what a player's own map would show.
    internal static class Locations
    {
        private struct Entry { public string kind; public float x, z; public bool explored; }
        // filled on the game thread as a sweep begins, read by its finish on the pool thread
        private static readonly List<Entry> found = new List<Entry>();
        private static volatile string json = "{\"locations\":[],\"count\":0}";
        private static ulong written;     // what the JSON was built from: the same walked ones keep it, a MB a sweep

        // Game thread, once a sweep: the instance list is the game's, and the fog moves.
        public static void Scan()
        {
            found.Clear();
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || zs.m_locationInstances == null) return;
                foreach (var li in zs.m_locationInstances.Values)
                {
                    string kind = li.m_location != null ? li.m_location.m_prefabName : null;
                    if (string.IsNullOrEmpty(kind)) continue;
                    float x = li.m_position.x, z = li.m_position.z;
                    found.Add(new Entry { kind = kind, x = x, z = z, explored = MapFog.Explored(x, z) });
                }
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: location scan failed: " + e.Message); }
        }

        public static void Finish()
        {
            ulong h = Fnv.Seed;
            foreach (var e in found) if (e.explored) h = Fnv.Mix(Fnv.Mix(Fnv.Mix(h, Fnv.Of(e.kind)), e.x), e.z);
            if (h == written) return;
            written = h;
            var sb = new StringBuilder("{\"locations\":[");
            int n = 0;
            foreach (var e in found)
            {
                if (!e.explored) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append(FormattableString.Invariant($"{{\"kind\":\"{e.kind.Replace("\\", "").Replace("\"", "")}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#}}}"));
            }
            json = sb.Append("],\"count\":").Append(n).Append('}').ToString();
        }

        public static string GetJson() => json;

        // ---------- for the tests (WebMap.Tests) ----------
        internal static void ResetForTests() => found.Clear();
        internal static void ObserveForTests(string kind, float x, float z, bool explored) =>
            found.Add(new Entry { kind = kind, x = x, z = z, explored = explored });
    }
}
