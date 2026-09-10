using System.Collections.Generic;
using UnityEngine;

namespace WebMap
{
    // Forest cover and logging, as a map overlay.
    //
    // Chopping a tree leaves a stump ZDO behind ("Beech_Stub" and friends), so
    // stumps are a persistent record of where the woods have been worked --
    // retroactive, no baseline snapshot needed. Naturally-spawned meadow stumps
    // are named "Stubbe" with no underscore, which is how they stay out of it.
    //
    // Standing trees are counted but not drawn: they set how complete a cut was,
    // and a canopy layer turned out to just restate the fog shape.
    //
    // Fed from the structures sweep so the world's ZDOs are only walked once.
    internal static class ForestMap
    {
        private enum Kind { Other, Tree, Stump }

        private struct Cell { public int trees, stumps; }
        private static readonly Dictionary<int, Cell> cells = new Dictionary<int, Cell>();
        private static readonly Dictionary<int, Kind> kindCache = new Dictionary<int, Kind>();

        private static Texture2D texture;
        private static Color32[] buf;
        private static byte[] png;
        private static bool pngStale = true;
        private static string statsJson = "{\"trees\":0,\"stumps\":0}";

        public static int LastTrees { get; private set; }
        public static int LastStumps { get; private set; }

        private static Kind Classify(int prefabHash)
        {
            if (kindCache.TryGetValue(prefabHash, out var cached)) return cached;
            string n = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) n = go.name.ToLowerInvariant();
            } catch { }

            Kind k;
            if (n == null) k = Kind.Other;
            // order matters: "pinetree_01_stub" is a stump, not a tree
            else if (n.Contains("_stub")) k = Kind.Stump;
            else if (n.Contains("_log") || n.Contains("logs")) k = Kind.Stump;   // felled trunk, not yet cut up
            else if (n.Contains("tree") || n.Contains("beech") || n.Contains("birch")
                     || n.Contains("oak") || n.Contains("yggashoot")) k = Kind.Tree;
            else k = Kind.Other;

            kindCache[prefabHash] = k;
            return k;
        }

        public static void Begin()
        {
            cells.Clear();
            LastTrees = 0;
            LastStumps = 0;
            if (texture != null) return;
            int size = WebMapConfig.TEXTURE_SIZE;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            buf = new Color32[size * size];
            texture.SetPixels32(buf);
            texture.Apply();
        }

        public static void Observe(int prefabHash, int idx)
        {
            var k = Classify(prefabHash);
            if (k == Kind.Other) return;
            cells.TryGetValue(idx, out Cell c);
            if (k == Kind.Tree) { c.trees++; LastTrees++; }
            else                { c.stumps++; LastStumps++; }
            cells[idx] = c;
        }

        // Only worked ground is drawn. Canopy was tried and dropped: nearly every
        // explored pixel holds trees, so it just restated the fog shape in green.
        // Standing trees still count -- they set how complete a cut was.
        public static void Finish()
        {
            if (texture == null) return;
            System.Array.Clear(buf, 0, buf.Length);
            int size = WebMapConfig.TEXTURE_SIZE;

            // bone, not the browns the structure layer uses: cut ground should never
            // be mistaken for a building
            foreach (var kv in cells)
            {
                var c = kv.Value;
                if (c.stumps <= 0) continue;
                float cut = c.stumps / (float)(c.stumps + c.trees);
                byte a = (byte)Mathf.Clamp(110 + c.stumps * 22 + cut * 70f, 110, 250);
                buf[kv.Key] = new Color32(228, 219, 196, a);
            }
            // one pixel is 12m of forest floor; a cleared patch needs mass to read
            var spill = new List<KeyValuePair<int, byte>>();
            foreach (var kv in cells)
            {
                if (kv.Value.stumps <= 0) continue;
                byte a = (byte)Mathf.Clamp(45 + kv.Value.stumps * 12, 45, 130);
                int idx = kv.Key;
                spill.Add(new KeyValuePair<int, byte>(idx - 1, a));
                spill.Add(new KeyValuePair<int, byte>(idx + 1, a));
                spill.Add(new KeyValuePair<int, byte>(idx - size, a));
                spill.Add(new KeyValuePair<int, byte>(idx + size, a));
            }
            foreach (var kv in spill)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= buf.Length) continue;
                if (buf[idx].a >= kv.Value) continue;
                buf[idx] = new Color32(228, 219, 196, kv.Value);
            }
            texture.SetPixels32(buf);
            texture.Apply();
            pngStale = true;
            statsJson = "{\"trees\":" + LastTrees + ",\"stumps\":" + LastStumps
                      + ",\"cells\":" + cells.Count + "}";
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
