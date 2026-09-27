using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace WebMap
{
    // The ground under a 256 m chunk, for the 3D view: /height?cx=&cz=[&step=].
    //
    // (256/step + 1) samples a side, step metres apart -- 257 at step 1 down to 17
    // at step 16, for the view's far rings -- both edges included so neighbouring
    // chunks share their seam. Row 0 is the south edge (z = cz*256), column 0 the
    // west (x = cx*256); each sample is a little-endian int16 of decimetres of
    // world height, the water standing at ZoneSystem.m_waterLevel (30 m).
    //
    // Built the way the game builds its heightmap (HeightmapBuilder.Build): per
    // 64 m zone, the generator's height for the biome at each of the zone's four
    // corners, blended across the zone by smoothstep where they differ. Sampling
    // the biome per metre instead cuts a cliff along every biome border, and a
    // building on it floats or sinks. The zone's terraforming (TerrainPatches)
    // goes on top; every sample is a heightmap vertex, so a coarse grid is the fine
    // one's every step-th sample, terraforming and all.
    //
    // The generator's part never changes and is kept, within a budget, for the
    // chunks and steps recently asked for; the terraforming is laid over it and the
    // bytes kept until a patch under the chunk moves. The generator is sampled on
    // the asking HTTP thread, as the game's own builder thread does; should the
    // engine ever refuse, sampling moves to the game thread a few rows a frame.
    internal static class Heights
    {
        public const int N = 257, Size = 256;
        private const long KeepSamples = 48L * N * N;           // generator heights held: 48 fine chunks, ~12 MB
        private const int FrameMs = 4;                          // the game-thread fallback's budget a frame

        private sealed class Served { public int hash; public byte[] raw, gz; }
        private sealed class Job { public int cx, cz, step, n, row; public float[] grid; public Exception error; public readonly ManualResetEventSlim done = new ManualResetEventSlim(false); }

        private static readonly ConcurrentDictionary<int, float[]> generated = new ConcurrentDictionary<int, float[]>();
        private static readonly ConcurrentQueue<int> order = new ConcurrentQueue<int>();
        private static long held;
        private static readonly ConcurrentDictionary<int, Served> served = new ConcurrentDictionary<int, Served>();
        private static readonly ConcurrentDictionary<int, object> gates = new ConcurrentDictionary<int, object>();
        private static readonly ConcurrentQueue<Job> mainQueue = new ConcurrentQueue<Job>();
        private static volatile bool mainThreadOnly;
        private static int logged;

        public static bool ValidStep(int step) => step == 1 || step == 2 || step == 4 || step == 8 || step == 16;
        // a chunk at a step: each step its own cache entry, all of them keyed by the same revision
        private static int Key(int cx, int cz, int step) => ((cx + 64) * 128 + (cz + 64)) * 8 + (step == 1 ? 0 : step == 2 ? 1 : step == 4 ? 2 : step == 8 ? 3 : 4);

        // Any thread. Null when the generator cannot be sampled yet; rev is the
        // chunk's terraforming hash, which changes when its heights do.
        public static byte[] Chunk(int cx, int cz, int step, bool gzip, out int rev)
        {
            if (!ValidStep(step)) step = 1;
            int key = Key(cx, cz, step);
            rev = TerraformHash(cx, cz);
            if (served.TryGetValue(key, out var s) && s.hash == rev) return gzip ? s.gz : s.raw;
            lock (gates.GetOrAdd(key, _ => new object()))
            {
                if (served.TryGetValue(key, out s) && s.hash == rev) return gzip ? s.gz : s.raw;
                float[] g = Generated(cx, cz, step, key);
                if (g == null) return null;
                byte[] raw = Encode(g, cx, cz, step);
                s = new Served { hash = rev, raw = raw, gz = Gzip(raw) };
                served[key] = s;
                return gzip ? s.gz : s.raw;
            }
        }

        private static int TerraformHash(int cx, int cz)
        {
            int h = 17;
            int zx0 = TerrainPatches.ZoneOf(cx * Size), zx1 = TerrainPatches.ZoneOf(cx * Size + Size);
            int zz0 = TerrainPatches.ZoneOf(cz * Size), zz1 = TerrainPatches.ZoneOf(cz * Size + Size);
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    var p = TerrainPatches.Get(zx, zz);
                    if (p != null) h = unchecked(h * 31 + p.hash + zx * 7919 + zz);
                }
            return h & 0x7fffffff;
        }

        private static byte[] Encode(float[] g, int cx, int cz, int step)
        {
            int n = Size / step + 1;
            var b = new byte[n * n * 2];
            int x0 = cx * Size, z0 = cz * Size;
            TerrainPatches.Patch p = null; int pzx = int.MinValue, pzz = int.MinValue;
            for (int j = 0; j < n; j++)
            {
                int z = z0 + j * step, zz = TerrainPatches.ZoneOf(z), lj = z - (zz * TerrainPatches.ZoneSize - 32);
                for (int i = 0; i < n; i++)
                {
                    int x = x0 + i * step, zx = TerrainPatches.ZoneOf(x), li = x - (zx * TerrainPatches.ZoneSize - 32);
                    if (zx != pzx || zz != pzz) { p = TerrainPatches.Get(zx, zz); pzx = zx; pzz = zz; }
                    float h = g[j * n + i] + (p != null ? p.delta[lj * TerrainPatches.W + li] : 0f);
                    int v = Mathf.RoundToInt(h * 10f);
                    v = v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : v;
                    int o = (j * n + i) * 2;
                    b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
                }
            }
            return b;
        }

        private static byte[] Gzip(byte[] raw)
        {
            using (var ms = new MemoryStream(raw.Length / 3 + 64))
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static float[] Generated(int cx, int cz, int step, int key)
        {
            if (generated.TryGetValue(key, out var g)) return g;
            if (WorldGenerator.instance == null) return null;
            var sw = Stopwatch.StartNew();
            int n = Size / step + 1;
            var job = new Job { cx = cx, cz = cz, step = step, n = n, grid = new float[n * n] };
            if (!mainThreadOnly)
            {
                try { Sample(job, n); }
                catch (Exception e) when (ThreadRule(e))
                {
                    ZLog.LogWarning("WebMap: the engine refused height sampling off the game thread; sampling there instead (" + e.Message + ")");
                    mainThreadOnly = true;
                    job.row = 0;
                }
            }
            if (mainThreadOnly)
            {
                mainQueue.Enqueue(job);
                if (!job.done.Wait(120_000)) return null;
                if (job.error != null) throw job.error;
            }
            if (Interlocked.Increment(ref logged) <= 3 || WebMapConfig.DEBUG)
                ZLog.Log($"WebMap: heights for chunk {cx},{cz} at {step} m sampled in {sw.ElapsedMilliseconds} ms" + (mainThreadOnly ? " on the game thread" : ""));
            generated[key] = job.grid;
            order.Enqueue(key);
            // the oldest go first, the served bytes with them, until what is held fits
            if (Interlocked.Add(ref held, job.grid.Length) > KeepSamples)
                while (Interlocked.Read(ref held) > KeepSamples && order.TryDequeue(out int old))
                {
                    if (generated.TryRemove(old, out var gone)) Interlocked.Add(ref held, -gone.Length);
                    served.TryRemove(old, out _);
                }
            return job.grid;
        }

        private static bool ThreadRule(Exception e)
        {
            string m = (e.Message ?? "") + " " + e.GetType().Name;
            return m.IndexOf("main thread", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("UnityException", StringComparison.Ordinal) >= 0;
        }

        // Rows [job.row, to) of the job's grid, as HeightmapBuilder blends them.
        private static void Sample(Job job, int to)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) throw new InvalidOperationException("WorldGenerator not ready");
            int x0 = job.cx * Size, z0 = job.cz * Size, W = TerrainPatches.ZoneSize, n = job.n, step = job.step;
            var corners = new Dictionary<long, Heightmap.Biome[]>();
            var hs = new float[4];
            for (int j = job.row; j < to; j++)
            {
                int z = z0 + j * step, zz = TerrainPatches.ZoneOf(z), zc = zz * W - 32;
                float tz = DUtils.SmoothStep(0f, 1f, (z - zc) / (float)W);
                for (int i = 0; i < n; i++)
                {
                    int x = x0 + i * step, zx = TerrainPatches.ZoneOf(x), xc = zx * W - 32;
                    long zk = TerrainPatches.ZoneKey(zx, zz);
                    if (!corners.TryGetValue(zk, out var c))
                        corners[zk] = c = new[] { wg.GetBiome(xc, zc), wg.GetBiome(xc + W, zc), wg.GetBiome(xc, zc + W), wg.GetBiome(xc + W, zc + W) };
                    float h;
                    if (c[1] == c[0] && c[2] == c[0] && c[3] == c[0]) h = wg.GetBiomeHeight(c[0], x, z, out Color _);
                    else
                    {
                        for (int k = 0; k < 4; k++) hs[k] = wg.GetBiomeHeight(c[k], x, z, out Color _);
                        float tx = DUtils.SmoothStep(0f, 1f, (x - xc) / (float)W);
                        h = DUtils.Lerp(DUtils.Lerp(hs[0], hs[1], tx), DUtils.Lerp(hs[2], hs[3], tx), tz);
                    }
                    job.grid[j * n + i] = h;
                }
            }
            job.row = to;
        }

        // Game thread: only ever busy if the engine refused sampling elsewhere.
        public static IEnumerator Pump()
        {
            var sw = new Stopwatch();
            while (true)
            {
                if (!mainQueue.TryPeek(out var job)) { yield return new WaitForSeconds(0.25f); continue; }
                sw.Restart();
                try
                {
                    while (job.row < job.n && sw.ElapsedMilliseconds < FrameMs) Sample(job, Math.Min(job.n, job.row + 8));
                }
                catch (Exception e) { job.error = e; job.row = job.n; }
                if (job.row >= job.n) { mainQueue.TryDequeue(out _); job.done.Set(); }
                yield return null;
            }
        }
    }
}
