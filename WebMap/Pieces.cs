using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace WebMap
{
    // Every placed piece as a footprint: position, yaw, size and material.
    //
    // A raster cannot show a building at any sane size -- a longhouse is one
    // pixel at 12 m and two at 6 m -- but pieces are sparse, a few thousand in
    // a handful of clusters, so they go out as data and the page draws them as
    // oriented rectangles that stay crisp at every zoom. Sizes come from the
    // prefab names, which encode them ("wood_wall_log_4x0.5", "stone_floor_2x2");
    // everything else sits on Valheim's 2 m build grid.
    internal static class Pieces
    {
        private class Kind { public string name; public float w, d; public string colour; public int idx; }
        private struct Entry { public int kind; public float x, z, yaw; }

        private static readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private static readonly List<Kind> order = new List<Kind>();            // idx -> kind
        private static readonly List<Entry> found = new List<Entry>();
        private static readonly Regex Dims = new Regex(@"(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static volatile string json = "{\"prefabs\":[],\"pieces\":[],\"count\":0}";

        private static string Inv(System.FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

        private static Kind KindOf(int prefabHash)
        {
            if (kinds.TryGetValue(prefabHash, out var k)) return k;
            string n = null;
            try { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null; if (go != null) n = go.name; } catch { }
            n = n ?? prefabHash.ToString();
            string l = n.ToLowerInvariant();
            float w = 1f, d = 1f;
            var m = Dims.Match(l);
            if (m.Success)
            {
                w = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                d = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (l.Contains("roof_top"))                                        { w = 2f; d = 0.5f; }
            else if (l.Contains("floor") || l.Contains("roof"))                     { w = 2f; d = 2f; }
            else if (l.Contains("wall") || l.Contains("fence") || l.Contains("gate")
                  || l.Contains("beam"))                                            { w = 2f; d = 0.3f; }
            else if (l.Contains("pole"))                                            { w = 0.4f; d = 0.4f; }
            else if (l.Contains("chest"))                                           { w = 1f; d = 0.5f; }
            else if (l.Contains("sign"))                                            { w = 1f; d = 0.2f; }
            else if (l.Contains("sapling"))                                         { w = 0.5f; d = 0.5f; }
            var c = StructureMap.MaterialOf(prefabHash);
            k = new Kind { name = n.Replace("\\", "").Replace("\"", ""), w = w, d = d,
                           colour = c.r.ToString("x2") + c.g.ToString("x2") + c.b.ToString("x2"), idx = order.Count };
            kinds[prefabHash] = k; order.Add(k);
            return k;
        }

        public static void Begin() => found.Clear();

        public static void Observe(int prefabHash, ZDO zdo, Vector3 pos)
        {
            var k = KindOf(prefabHash);
            float yaw = 0f;
            try { yaw = zdo.GetRotation().eulerAngles.y; } catch { }
            found.Add(new Entry { kind = k.idx, x = pos.x, z = pos.z, yaw = yaw });
        }

        // Not fog-gated here: the page draws these under the fog mask, like the
        // raster, so unexplored builds are hidden the same way that layer is.
        public static void Finish()
        {
            var sb = new StringBuilder(found.Count * 24 + 4096);
            sb.Append("{\"prefabs\":[");
            for (int i = 0; i < order.Count; i++)
            {
                var k = order[i];
                if (i > 0) sb.Append(',');
                sb.Append(Inv($"{{\"n\":\"{k.name}\",\"w\":{k.w:0.##},\"d\":{k.d:0.##},\"c\":\"{k.colour}\"}}"));
            }
            sb.Append("],\"pieces\":[");
            for (int i = 0; i < found.Count; i++)
            {
                var e = found[i];
                if (i > 0) sb.Append(',');
                sb.Append(Inv($"[{e.kind},{e.x:0.#},{e.z:0.#},{Mathf.RoundToInt(e.yaw)}]"));
            }
            sb.Append("],\"count\":").Append(found.Count).Append('}');
            string s = sb.ToString();
            json = s; Rev = Fnv.Of(s);
        }

        public static string GetJson() => json;
        public static volatile int Rev;                    // content revision of the JSON

        // Name from the walk's cache: safe off the game thread, unlike ZNetScene.
        internal static string NameOf(int prefabHash) => kinds.TryGetValue(prefabHash, out var k) ? k.name : prefabHash.ToString();
    }
}
