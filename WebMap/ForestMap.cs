using System.Collections.Generic;
using UnityEngine;

namespace WebMap
{
    // Forest cover and logging, as a map overlay.
    //
    // Standing trees shade the terrain; where they have been felled the shading
    // stops and the bare render shows through, which is what deforestation looks
    // like on the map. Stumps ("Beech_Stub" and friends) are counted alongside as
    // the positive record of felling -- naturally-spawned meadow stumps are named
    // "Stubbe" with no underscore, which is how they stay out of the count.
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

        // Forest darkens the terrain instead of being painted on it: the layer is
        // multiplied over the base render, so dense woods go dark and green and a
        // clearing is simply a hole where the real terrain shows through. Cut
        // ground needs no colour of its own -- the missing trees are the signal.
        //
        // One pixel is 12m, so raw counts are one or two trees and read as noise.
        // A box blur over the populated bounds turns them into canopy.
        public static void Finish()
        {
            if (texture == null) return;
            System.Array.Clear(buf, 0, buf.Length);
            int size = WebMapConfig.TEXTURE_SIZE;
            if (cells.Count == 0) { texture.SetPixels32(buf); texture.Apply(); pngStale = true; return; }

            int minX = size, minY = size, maxX = 0, maxY = 0;
            foreach (var kv in cells)
            {
                int x = kv.Key % size, y = kv.Key / size;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            const int R = 2;                                  // 5x5 box
            minX = Mathf.Max(0, minX - R); minY = Mathf.Max(0, minY - R);
            maxX = Mathf.Min(size - 1, maxX + R); maxY = Mathf.Min(size - 1, maxY + R);
            int w = maxX - minX + 1, h = maxY - minY + 1;

            var dens = new float[w * h];
            foreach (var kv in cells)
            {
                int x = kv.Key % size - minX, y = kv.Key / size - minY;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                dens[y * w + x] = kv.Value.trees;
            }

            var blur = new float[w * h];
            float norm = (2 * R + 1) * (2 * R + 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int dy = -R; dy <= R; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -R; dx <= R; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= w) continue;
                            sum += dens[yy * w + xx];
                        }
                    }
                    blur[y * w + x] = sum / norm;
                }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = blur[y * w + x];
                    if (d <= 0.05f) continue;                  // bare ground stays bare
                    byte a = (byte)Mathf.Clamp(d * 90f, 12f, 170f);
                    // multiplied, so this darkens and pulls toward green -- which also
                    // greens the biomes that aren't green to begin with
                    buf[(y + minY) * size + (x + minX)] = new Color32(96, 130, 84, a);
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
