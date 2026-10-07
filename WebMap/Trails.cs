using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using static WebMap.WebMapConfig;

namespace WebMap
{
    // Where people go: a count per map pixel of the times a player walked into
    // it, drawn as a faint trail over the map. Fed once a second per player from
    // the snapshot on the game thread, rendered on the sweep's pool thread, and
    // kept on disk beside the stats so a restart forgets nothing.
    internal static class Trails
    {
        private const int SaveEveryMs = 10 * 60_000;

        private static readonly object gate = new object();
        private static byte[] visits;                                          // one saturating byte per texture pixel
        private static readonly Dictionary<long, int> lastCell = new Dictionary<long, int>();
        private static string path;
        private static bool dirty, unsaved, saving;
        private static int lastSave = Environment.TickCount;
        private static volatile byte[] png;
        public static volatile int Rev;                                        // content revision of the PNG
        // the sweep's copy of the counts and the picture drawn from it, 24 MB together: kept between sweeps
        private static byte[] copy, alpha, rgba;

        public static void Load(string worldDataPath)
        {
            lock (gate)
            {
                int n = TEXTURE_SIZE * TEXTURE_SIZE;
                visits = new byte[n];
                lastCell.Clear();
                path = Path.Combine(worldDataPath, "trails.bin");
                try
                {
                    string file = File.Exists(path) ? path : File.Exists(path + ".new") ? path + ".new" : null;
                    if (file != null)
                    {
                        var b = File.ReadAllBytes(file);
                        if (b.Length == n) visits = b; else ZLog.LogWarning("WebMap: trails.bin is for another texture size; starting over");
                    }
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: trails not loaded: " + e.Message); }
                dirty = true;
            }
        }

        // Game thread, once a second per player. Standing still is not a trail:
        // a pixel counts once each time someone comes into it.
        public static void Mark(long who, Vector3 pos)
        {
            if (visits == null) return;
            int size = TEXTURE_SIZE, half = size / 2;
            int x = Mathf.RoundToInt(pos.x / PIXEL_SIZE + half), y = Mathf.RoundToInt(pos.z / PIXEL_SIZE + half);
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            int idx = y * size + x;
            lock (gate)
            {
                if (lastCell.TryGetValue(who, out int was) && was == idx) return;
                lastCell[who] = idx;
                if (visits[idx] < 255) visits[idx]++;
                dirty = true; unsaved = true;
            }
        }

        // Pool thread, at the end of a sweep. Each visited pixel is a soft 3x3
        // splat so a walk reads as a line even when the map is fitted to the
        // screen, and the alpha climbs by the log of the count: one pass shows,
        // a road is solid, and nothing pins early.
        public static void Finish()
        {
            int size = TEXTURE_SIZE;
            byte[] v;
            lock (gate)
            {
                if (!dirty || visits == null) return;
                if (copy == null || copy.Length != visits.Length) { copy = new byte[visits.Length]; alpha = new byte[size * size]; rgba = new byte[size * size * 4]; }
                Buffer.BlockCopy(visits, 0, copy, 0, visits.Length);
                v = copy;
                dirty = false;
            }
            Array.Clear(alpha, 0, alpha.Length);
            Array.Clear(rgba, 0, rgba.Length);
            for (int i = 0; i < v.Length; i++)
            {
                int c = v[i]; if (c == 0) continue;
                int a = Math.Min(255, 60 + (int)(55.0 * Math.Log(1 + c, 2.0)));
                int x = i % size, y = i / size;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                        int j = yy * size + xx, aj = (dx == 0 && dy == 0) ? a : a * 45 / 100;
                        if (alpha[j] < aj) alpha[j] = (byte)aj;
                    }
            }
            for (int i = 0; i < alpha.Length; i++)
            {
                if (alpha[i] == 0) continue;
                int o = i * 4;
                rgba[o] = 150; rgba[o + 1] = 200; rgba[o + 2] = 255; rgba[o + 3] = alpha[i];
            }
            var bytes = ImageConv.EncodeRgbaToPNG(rgba, size, size);
            png = bytes; Rev = Fnv.Of(bytes);
        }

        public static byte[] GetPng() => png ?? new byte[0];

        // Game thread, once a second: the 4 MB write goes to the pool, ten minutes apart.
        public static void MaybeSave()
        {
            if (!unsaved || saving || unchecked((uint)(Environment.TickCount - lastSave)) < SaveEveryMs) return;
            Save();
        }

        public static void Save()
        {
            byte[] copy; string p;
            lock (gate)
            {
                if (visits == null || path == null || !unsaved) return;
                copy = (byte[])visits.Clone(); p = path;
                unsaved = false; saving = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    File.WriteAllBytes(p + ".new", copy);
                    if (File.Exists(p)) File.Replace(p + ".new", p, null); else File.Move(p + ".new", p);
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: trails not saved: " + e.Message); lock (gate) unsaved = true; }
                finally { lastSave = Environment.TickCount; saving = false; }
            });
        }
    }
}
