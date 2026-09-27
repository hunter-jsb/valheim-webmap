using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UnityEngine;
using WebMap;
using Xunit;

// The mod keeps its state in static classes, so the cases run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WebMap.Tests
{
    internal static class Setup
    {
        // Unity's logger ends in an engine call that exists only inside the game, and
        // the mod logs as it goes: switched off before any case runs.
        [ModuleInitializer]
        internal static void Init() => UnityEngine.Debug.unityLogger.logEnabled = false;
    }

    // A directory of its own per case, gone afterwards.
    public abstract class WithDir : IDisposable
    {
        protected readonly string Dir = Directory.CreateTempSubdirectory("webmap-tests-").FullName;
        public virtual void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    // A world for Features to read: one byte per cell, 0 for water, else a biome
    // class (Heightmap.Biome's bit index + 1), laid out the way the mod samples it.
    internal sealed class Grid
    {
        public const int N = Features.Cells;
        public const byte Meadows = 1, Mountain = 3, Mistlands = 10;
        public readonly byte[] Cls = new byte[N * N];
        public readonly List<List<Vector2>> Rivers = new List<List<Vector2>>();
        public static float Cell => WebMapConfig.TEXTURE_SIZE * WebMapConfig.PIXEL_SIZE / (float)N;
        // the world position of a cell's centre, and back
        public static float W(int c) => (c - N / 2f) * Cell + Cell / 2f;
        public static int C(float w) => (int)Math.Floor(w / Cell + N / 2f);

        // cells [x0, x1) x [y0, y1)
        public Grid Box(int x0, int y0, int x1, int y1, byte c = Meadows)
        {
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) Cls[y * N + x] = c;
            return this;
        }
        public Grid River(params (int x, int y)[] cells)
        {
            var path = new List<Vector2>();
            for (int i = 0; i + 1 < cells.Length; i++)
                for (int k = 0; k < 8; k++)      // a point every eighth of the way, as the generator samples
                {
                    float t = k / 8f;
                    path.Add(new Vector2(W(cells[i].x) + (W(cells[i + 1].x) - W(cells[i].x)) * t,
                                         W(cells[i].y) + (W(cells[i + 1].y) - W(cells[i].y)) * t));
                }
            path.Add(new Vector2(W(cells[cells.Length - 1].x), W(cells[cells.Length - 1].y)));
            Rivers.Add(path);
            return this;
        }
        // read by the mod; the features as the site gets them
        public List<JsonElement> Read(string seed = "test")
        {
            Features.AnalyseForTests(Cls, new float[N * N], Rivers, seed);
            var list = new List<JsonElement>();
            foreach (var f in JsonDocument.Parse(Features.Json()).RootElement.GetProperty("features").EnumerateArray()) list.Add(f.Clone());
            return list;
        }
    }

    internal static class J
    {
        public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
        public static string Str(this JsonElement e, string key) => e.GetProperty(key).GetString();
        public static double Num(this JsonElement e, string key) => e.GetProperty(key).GetDouble();
        public static int Int(this JsonElement e, string key) => e.GetProperty(key).GetInt32();
    }
}
