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
    // Observe runs on the game thread during the walk; Finish runs on the sweep's
    // pool thread and touches nothing of Unity's but Mathf.
    internal static class ForestMap
    {
        // Shading curve. Trees per pixel run ~0.5 at a woodland fringe to ~8 in
        // deep Black Forest, so the curve is tuned to spend its range there:
        // 1 tree/px reads 75, 3 reads 135, 8 reads 180. Nothing ever pins.
        private const float Steepness = 225f;
        private const float HalfShade = 2f;     // density at which shading is half of Steepness
        private const float MaxShade  = 200f;   // never pitch black

        private enum Kind { Other, Tree, Stump }

        private struct Cell { public int trees, stumps; }
        private static readonly Dictionary<int, Cell> cells = new Dictionary<int, Cell>();
        private static readonly Dictionary<int, Kind> kindCache = new Dictionary<int, Kind>();

        private static byte[] rgba;
        private static volatile byte[] png;
        private static volatile string statsJson = "{\"trees\":0,\"stumps\":0}";

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
            if (rgba == null)
            {
                int size = WebMapConfig.TEXTURE_SIZE;
                rgba = new byte[size * size * 4];
            }
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
            if (rgba == null) return;
            System.Array.Clear(rgba, 0, rgba.Length);
            int size = WebMapConfig.TEXTURE_SIZE;
            if (cells.Count == 0) { png = ImageConv.EncodeRgbaToPNG(rgba, size, size); return; }

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
                    if (d <= 0.02f) continue;                  // bare ground stays bare
                    // A straight ramp clamped, and every forest interior came out the
                    // same flat slab: felling half a wood changed nothing on the map
                    // because both densities were over the clamp. This saturates
                    // smoothly instead, so there is a gradient at every density and
                    // thinning reads as lightening long before the last tree goes.
                    byte a = (byte)Mathf.Min(MaxShade, Steepness * d / (d + HalfShade));
                    // multiplied, so this darkens and pulls toward green -- which also
                    // greens the biomes that aren't green to begin with
                    int o = ((y + minY) * size + (x + minX)) * 4;
                    rgba[o] = 96; rgba[o + 1] = 130; rgba[o + 2] = 84; rgba[o + 3] = a;
                }

            png = ImageConv.EncodeRgbaToPNG(rgba, size, size);
            statsJson = "{\"trees\":" + LastTrees + ",\"stumps\":" + LastStumps
                      + ",\"cells\":" + cells.Count
                      + ",\"density\":" + Percentiles(blur) + "}";
        }

        // Where the shading curve is actually spending its range. Tuning it by
        // eye means guessing at the tree counts behind the picture; these say so.
        private static string Percentiles(float[] blur)
        {
            var v = new List<float>();
            foreach (float f in blur) if (f > 0.02f) v.Add(f);
            if (v.Count == 0) return "{}";
            v.Sort();
            System.Func<float, float> q = p => v[Mathf.Clamp((int)(p * v.Count), 0, v.Count - 1)];
            return "{\"p50\":" + q(0.50f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                 + ",\"p90\":" + q(0.90f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                 + ",\"p99\":" + q(0.99f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                 + ",\"max\":" + v[v.Count - 1].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        public static string GetStats() => statsJson;

        public static byte[] GetPng() => png ?? new byte[0];
    }
}
