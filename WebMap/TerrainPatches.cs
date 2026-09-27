// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace WebMap
{
    // Player terraforming, decoded from the world.
    //
    // Every zone a player has dug, raised, levelled or paved in owns a
    // "_TerrainCompiler" object whose ZDO carries the modifications as a
    // gzipped blob (ZDOVars.s_TCData): for each of the 65x65 heightmap vertices
    // a flag and two height deltas (level + smooth), then the paint. The game's
    // Heightmap adds the deltas to the generator's height, held within 8 m of
    // it (TerrainComp.ApplyToHeightmap), and so does Heights: roads, moats and
    // flattened bases stand in the 3D view as they are in the world.
    //
    // Observe is the sweep's, on the game thread, and decodes a blob only when
    // its data revision has moved. Patches are immutable arrays in a concurrent
    // map, so the height endpoint reads them from any thread.
    internal static class TerrainPatches
    {
        public const int W = 65;                  // vertices per zone edge
        public const int N = W * W;
        public const int ZoneSize = 64;
        public static readonly int CompilerHash = "_TerrainCompiler".GetStableHashCode();

        public sealed class Patch
        {
            public float[] delta;                 // N entries, level + smooth clamped to +-8 m; row z, column x
            public int hash;                      // content hash: which chunks' heights moved
        }

        private static readonly ConcurrentDictionary<long, Patch> patches = new ConcurrentDictionary<long, Patch>();
        // zone -> data revision last decoded, so an unchanged zone costs one lookup a sweep;
        // the walk and its finish never overlap, so no lock
        private static readonly Dictionary<long, uint> seenRev = new Dictionary<long, uint>();
        private static HashSet<long> seen;
        private static int changed;

        public static volatile int Rev;           // bumped when any zone's heights changed
        public static int Count => patches.Count;

        // Zone (zx, zz) covers x in [zx*64-32, zx*64+32); its vertex (i, j) stands at
        // (zx*64-32+i, zz*64-32+j).
        public static int ZoneOf(float w) => (int)Math.Floor((w + ZoneSize / 2f) / ZoneSize);
        public static long ZoneKey(int zx, int zz) => ((long)(zx + 32768) << 20) | (uint)(zz + 32768);

        public static Patch Get(int zx, int zz) { patches.TryGetValue(ZoneKey(zx, zz), out var p); return p; }

        // Game thread, before the walk.
        public static void Begin() { seen = new HashSet<long>(); changed = 0; }

        // Game thread, for each terrain compiler the walk meets.
        public static void Observe(ZDO zdo, UnityEngine.Vector3 pos)
        {
            if (seen == null) return;
            int zx = ZoneOf(pos.x), zz = ZoneOf(pos.z);
            long key = ZoneKey(zx, zz);
            seen.Add(key);
            uint rev = 0;
            try { rev = unchecked((uint)zdo.DataRevision); } catch { }
            if (seenRev.TryGetValue(key, out uint prev) && prev == rev) return;
            seenRev[key] = rev;

            byte[] blob = null;
            try { blob = zdo.GetByteArray(ZDOVars.s_TCData); } catch { }
            Patch p = null;
            if (blob != null && blob.Length > 0)
            {
                try { p = Decode(blob); }
                catch (Exception e) { if (WebMapConfig.DEBUG) ZLog.LogWarning($"WebMap: terrain blob for zone {zx},{zz} not understood: {e.Message}"); return; }
            }
            bool had = patches.TryGetValue(key, out var old);
            if (p == null) { if (had) { patches.TryRemove(key, out _); changed++; } return; }
            if (had && old.hash == p.hash) return;
            patches[key] = p;
            changed++;
        }

        // The sweep's pool thread: a zone whose compiler is gone is flat again.
        public static void Finish()
        {
            var s = seen;
            seen = null;
            if (s == null) return;
            foreach (long key in new List<long>(patches.Keys))
                if (!s.Contains(key)) { patches.TryRemove(key, out _); changed++; }
            foreach (long key in new List<long>(seenRev.Keys)) if (!s.Contains(key)) seenRev.Remove(key);
            if (changed > 0 || Rev == 0) Rev++;
        }

        // TerrainComp's own layout; a blob with no height change is null. The paint
        // grid after it is not read: the 3D view colours ground by biome.
        public static Patch Decode(byte[] blob)
        {
            byte[] raw = Gunzip(blob);
            var p = new Patch { delta = new float[N] };
            int hash = 17;
            bool any = false;
            using (var br = new BinaryReader(new MemoryStream(raw)))
            {
                br.ReadInt32();                     // version
                br.ReadInt32();                     // operations
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle();   // last op point
                br.ReadSingle();                    // last op radius
                int n = br.ReadInt32();
                if (n != N) throw new FormatException("height grid is " + n + " vertices, expected " + N);
                for (int i = 0; i < n; i++)
                {
                    if (!br.ReadBoolean()) continue;
                    float d = br.ReadSingle() + br.ReadSingle();       // level + smooth
                    d = d < -8f ? -8f : d > 8f ? 8f : d;
                    p.delta[i] = d;
                    if (d != 0f) { any = true; hash = unchecked(hash * 31 + i); hash = unchecked(hash * 31 + (int)(d * 100f)); }
                }
            }
            p.hash = hash;
            return any ? p : null;
        }

        private static byte[] Gunzip(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                gz.CopyTo(outp);
                return outp.ToArray();
            }
        }
    }
}
