using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WebMap
{
    // Player-built structures as a map overlay.
    //
    // Anything a player placed carries a creator on its ZDO; terrain and spawned
    // props don't. The site draws this layer UNDERNEATH the fog, so builds in
    // unexplored territory are hidden by the fog mask for free -- no separate
    // spoiler logic.
    //
    // A sweep walks every ZDO, so it is deliberately spread across frames and run
    // infrequently: buildings change slowly and this shares a CPU with the game.
    internal static class StructureMap
    {
        private const float SweepInterval = 120f;
        private const int ZdosPerFrame = 3000;

        private static Texture2D texture;
        // Only a few hundred pixels ever hold anything, so a dictionary costs a
        // fraction of a full-map array and needs no clearing pass.
        private struct Cell { public int n, r, g, b; }
        private static readonly Dictionary<int, Cell> cells = new Dictionary<int, Cell>();
        private static readonly Dictionary<int, Color32> paletteCache = new Dictionary<int, Color32>();
        private static Color32[] buf;        // render target, reused between sweeps
        private static string statsJson = "{\"total\":0,\"prefabs\":[]}";
        private static byte[] png;
        private static bool pngStale = true;
        private static bool sweeping;

        // Set from the HTTP thread; consumed on the main thread. Unity objects
        // must not be touched off-thread, so a request can only ask, never scan.
        public static volatile bool RefreshRequested;

        public static int LastCount { get; private set; }
        public static int LastScanned { get; private set; }

        // Rough albedo per build material, keyed off the prefab name. Valheim's
        // piece names are descriptive enough that this needs no asset lookups.
        internal static Color32 MaterialOf(int prefabHash)
        {
            if (paletteCache.TryGetValue(prefabHash, out var cached)) return cached;
            string n = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) n = go.name.ToLowerInvariant();
            } catch { }

            Color32 c;
            if (n == null)                                   c = new Color32(150, 120,  90, 255);
            else if (n.Contains("portal"))                   c = new Color32( 90, 200, 210, 255);
            else if (n.Contains("blackmarble"))              c = new Color32( 70,  70,  85, 255);
            else if (n.Contains("stone") || n.Contains("grausten"))
                                                             c = new Color32(150, 150, 145, 255);
            else if (n.Contains("iron") || n.Contains("metal"))
                                                             c = new Color32(120, 130, 145, 255);
            else if (n.Contains("darkwood"))                 c = new Color32( 90,  66,  46, 255);
            else if (n.Contains("roof") || n.Contains("straw") || n.Contains("thatch"))
                                                             c = new Color32(196, 160,  86, 255);
            else if (n.Contains("fire") || n.Contains("hearth") || n.Contains("forge"))
                                                             c = new Color32(214, 122,  58, 255);
            else                                             c = new Color32(150, 108,  66, 255); // wood
            paletteCache[prefabHash] = c;
            return c;
        }

        private static void Init()
        {
            if (texture != null) return;
            int size = WebMapConfig.TEXTURE_SIZE;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            buf = new Color32[size * size];
            texture.SetPixels32(buf);                        // zeroed = fully transparent
            texture.Apply();
        }

        public static IEnumerator Loop()
        {
            Init();
            yield return new WaitForSeconds(30f);            // let the world finish loading
            while (true)
            {
                yield return Sweep();
                float waited = 0f;
                while (waited < SweepInterval && !RefreshRequested)
                {
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                RefreshRequested = false;
            }
        }

        private static IEnumerator Sweep()
        {
            if (sweeping) yield break;
            sweeping = true;
            Init();

            int size = WebMapConfig.TEXTURE_SIZE;
            int half = size / 2;
            cells.Clear();                                    // rebuilt each sweep so demolitions vanish
            System.Array.Clear(buf, 0, buf.Length);
            ForestMap.Begin();                                // shares this one walk of the world
            Vehicles.Begin();
            Portals.Begin();
            Pieces.Begin();
            Graves.Begin();

            var byPrefab = new Dictionary<int, int>();

            List<ZDO> all = null;
            try { all = new List<ZDO>(ZDOMan.instance.m_objectsByID.Values); }
            catch { }
            if (all == null) { sweeping = false; yield break; }

            int found = 0, seen = 0;
            foreach (var zdo in all)
            {
                seen++;
                if (zdo != null)
                {
                    long creator = 0L;
                    try { creator = zdo.GetLong(ZDOVars.s_creator, 0L); } catch { }
                    Vector3 p = zdo.GetPosition();
                    int x = Mathf.RoundToInt(p.x / WebMapConfig.PIXEL_SIZE + half);
                    int y = Mathf.RoundToInt(p.z / WebMapConfig.PIXEL_SIZE + half);
                    if (x >= 0 && y >= 0 && x < size && y < size)
                    {
                        int idx = y * size + x;
                        int pref = 0;
                        try { pref = zdo.GetPrefab(); } catch { }
                        if (Graves.IsGrave(pref))
                        {
                            Graves.Observe(zdo, p);   // spawned by the game, so no creator
                        }
                        else if (creator != 0L)
                        {
                            var veh = Vehicles.Classify(pref);
                            if (veh != Vehicles.Kind.None)
                            {
                                Vehicles.Observe(pref, veh, p);   // a boat is not a building
                            }
                            else
                            {
                                // a portal is part of a build, so it is reported AND painted
                                if (Portals.IsPortal(pref)) Portals.Observe(zdo, p);
                                Pieces.Observe(pref, zdo, p);      // the same piece, as a footprint
                                var mat = MaterialOf(pref);
                                cells.TryGetValue(idx, out Cell cell);
                                cell.n++; cell.r += mat.r; cell.g += mat.g; cell.b += mat.b;
                                cells[idx] = cell;
                                found++;
                                byPrefab.TryGetValue(pref, out int n);
                                byPrefab[pref] = n + 1;
                            }
                        }
                        else
                        {
                            ForestMap.Observe(pref, idx);     // trees and stumps aren't placed by anyone
                        }
                    }
                }
                if (seen % ZdosPerFrame == 0) yield return null;   // never stall a frame
            }

            yield return Render(size);
            ForestMap.Finish();
            Vehicles.Finish();
            Portals.Finish();
            Pieces.Finish();
            Graves.Finish();

            LastCount = found;
            LastScanned = seen;
            statsJson = BuildStats(byPrefab, found, seen);
            pngStale = true;
            sweeping = false;
            ZLog.Log($"WebMap: structures sweep -> {found} placed pieces from {seen} zdos; "
                   + $"forest {ForestMap.LastTrees} trees / {ForestMap.LastStumps} stumps");
        }

        // Each pixel takes the weighted average albedo of the pieces standing on
        // it, so a stone keep reads grey and a thatch longhouse reads straw.
        // Density now only drives opacity -- it used to tint toward yellow, which
        // made a busy base look like it was on fire.
        private static IEnumerator Render(int size)
        {
            int done = 0;
            foreach (var kv in cells)
            {
                var c = kv.Value;
                if (c.n <= 0) continue;
                byte a = (byte)Mathf.Clamp(120 + c.n * 20, 120, 255);
                buf[kv.Key] = new Color32((byte)(c.r / c.n), (byte)(c.g / c.n), (byte)(c.b / c.n), a);
                if ((++done & 0x3FF) == 0) yield return null;
            }
            // Spill dense cells into their neighbours: at 12m per pixel a longhouse
            // is only a few pixels, so bases need mass to read as shapes.
            var spill = new List<KeyValuePair<int, Color32>>();
            foreach (var kv in cells)
            {
                var c = kv.Value;
                var col = new Color32((byte)(c.r / c.n), (byte)(c.g / c.n), (byte)(c.b / c.n),
                                      (byte)Mathf.Clamp(55 + c.n * 10, 55, 140));
                int idx = kv.Key;
                spill.Add(new KeyValuePair<int, Color32>(idx - 1, col));
                spill.Add(new KeyValuePair<int, Color32>(idx + 1, col));
                spill.Add(new KeyValuePair<int, Color32>(idx - size, col));
                spill.Add(new KeyValuePair<int, Color32>(idx + size, col));
            }
            foreach (var kv in spill)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= buf.Length) continue;
                if (cells.ContainsKey(idx)) continue;          // never dim a cell with real pieces
                if (buf[idx].a >= kv.Value.a) continue;
                buf[idx] = kv.Value;
            }
            yield return null;
            texture.SetPixels32(buf);
            texture.Apply();
        }

        private static string BuildStats(Dictionary<int, int> byPrefab, int found, int seen)
        {
            var top = new List<KeyValuePair<int, int>>(byPrefab);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"total\":").Append(found).Append(",\"scanned\":").Append(seen)
              .Append(",\"distinct\":").Append(byPrefab.Count).Append(",\"prefabs\":[");
            int n = 0;
            foreach (var kv in top)
            {
                if (n >= 25) break;
                string name = kv.Key.ToString();
                try
                {
                    var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kv.Key) : null;
                    if (go != null) name = go.name;
                } catch { }
                if (n > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(name.Replace("\"", "")).Append("\",\"count\":").Append(kv.Value).Append("}");
                n++;
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string GetStats() => statsJson;

        public static byte[] GetPng()
        {
            if (texture == null) return new byte[0];
            if (pngStale || png == null)
            {
                png = ImageConv.EncodeToPNG(texture);
                pngStale = false;
            }
            return png;
        }
    }
}
