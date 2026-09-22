using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using static WebMap.WebMapConfig;

namespace WebMap
{
    // The world's geography, found once per world from the generator itself:
    // landmasses, lakes, bays and mountain ranges from a sampled grid of biome
    // and height, rivers from the generator's own river list. Each feature gets
    // a Norse name from the seed, and a name given on the site replaces it.
    // Names are public; the viewer decides what the fog allows it to show.
    internal static class Features
    {
        private const string NamesFile = "names.tsv";
        private const int N = 1024;                 // cells across the world: a cell is two texture px, 24 m
        private const float Edge = 10500f;          // the generator's water edge: beyond it, only sea

        public static volatile int Rev;             // content revision, 0 until the world has been read
        private static volatile string json = "{\"features\":[],\"count\":0,\"rev\":0}";
        private static volatile bool building;
        private static string dir;
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Named> names = new Dictionary<string, Named>();
        private static Feature[] found = new Feature[0];
        private static readonly Dictionary<string, string> generated = new Dictionary<string, string>();
        // what is where: per cell, 1 + the index into found of the landmass, the biome
        // region, the lake or bay, and the range there; the biome and height too
        private static volatile Grids grids;
        private class Grids { public ushort[] land, region, water, range; public byte[] cls; public float[] hgt; public float cell; }

        private class Named { public string name, by; public long t; }
        private class Feature
        {
            public string id, kind;
            public float x, z;            // where its name sits: the point deepest inside it, or a river's middle
            public float w, h, area;      // extent in metres, area in km²
            public bool hasPeak; public float px, pz, py;
            public bool hasElev; public float hmin, hmax, hmean;   // land, ranges and biome regions
            public List<float[]> line;    // rivers only: the path, [x, z] every ~50 m
        }

        private static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

        public static void Load(string worldDataPath)
        {
            dir = worldDataPath;
            lock (gate)
            {
                names.Clear();
                try
                {
                    string p = Path.Combine(dir, NamesFile);
                    if (File.Exists(p))
                        foreach (string line in File.ReadAllLines(p))
                        {
                            var f = line.Split('\t');
                            if (f.Length < 2 || f[0].Length == 0) continue;
                            long t = 0; if (f.Length > 3) long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out t);
                            names[f[0]] = new Named { name = f[1], by = f.Length > 2 ? f[2] : "", t = t };
                        }
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: names not loaded: " + e.Message); }
            }
            StaticCoroutine.Start(Build());
        }

        // ---------- reading the world (game thread, one row per frame) ----------
        private static IEnumerator Build()
        {
            if (building) yield break;
            building = true;
            float cell = TEXTURE_SIZE * PIXEL_SIZE / (float)N, half = N / 2f;
            var cls = new byte[N * N];
            var hgt = new float[N * N];
            float water = ZoneSystem.instance.m_waterLevel;
            var wg = WorldGenerator.instance;
            for (int y = 0; y < N; y++)
            {
                yield return null;
                float wz = (y - half) * cell + cell / 2f;
                for (int x = 0; x < N; x++)
                {
                    float wx = (x - half) * cell + cell / 2f;
                    int i = y * N + x;
                    if (wx * wx + wz * wz > Edge * Edge) { cls[i] = 0; hgt[i] = 0f; continue; }
                    var biome = wg.GetBiome(wx, wz);
                    float h = wg.GetBiomeHeight(biome, wx, wz, out Color _);
                    hgt[i] = h;
                    cls[i] = h < water ? (byte)0 : (byte)(Index(biome) + 1);
                }
            }
            // the rivers are the generator's: its path rule, sampled the way it samples
            var rivers = new List<List<Vector2>>();
            try { foreach (var r in wg.GetRivers()) rivers.Add(RiverPath(r)); }
            catch (Exception e) { ZLog.LogWarning("WebMap: rivers not read: " + e.Message); }
            string seed = "";
            try { seed = wg.m_world != null ? (wg.m_world.m_seedName ?? "") : ""; } catch { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Analyse(cls, hgt, rivers, cell, seed); }
                catch (Exception e) { ZLog.LogWarning("WebMap: geography not read: " + e.Message + "\n" + e.StackTrace); }
                finally { building = false; }
            });
        }

        private static List<Vector2> RiverPath(WorldGenerator.River r)
        {
            var pts = new List<Vector2>();
            float step = r.widthMin / 8f;
            Vector2 n = (r.p1 - r.p0).normalized, a = new Vector2(-n.y, n.x);
            float len = Vector2.Distance(r.p0, r.p1);
            for (float t = 0f; t <= len; t += step)
            {
                float u = t / r.curveWavelength;
                float d = (float)(Math.Sin(u) * Math.Sin(u * 0.634119987487793) * Math.Sin(u * 0.3341200053691864) * r.curveWidth);
                pts.Add(r.p0 + n * t + a * d);
            }
            return pts;
        }

        private static int Index(Heightmap.Biome b)
        {
            int v = (int)b, i = 0;
            while (v > 1 && i < 31) { v >>= 1; i++; }
            return i;
        }
        private static byte Class(Heightmap.Biome b) => (byte)(Index(b) + 1);

        // ---------- the analysis (pool thread) ----------
        private static void Analyse(byte[] cls, float[] hgt, List<List<Vector2>> rivers, float cell, string seed)
        {
            var feats = new List<Feature>();
            int n = N * N;
            float cellKm2 = cell * cell / 1e6f;
            Func<float, float> toWorld = c => (c - N / 2f) * cell + cell / 2f;

            // the rivers, and the cells they run through: a lake with a river to the
            // sea is still a lake, so those cells count as neither land nor water
            var riverCell = new bool[n];
            foreach (var path in rivers)
            {
                float r = 4f;   // in cells: half of a river's widest, 100 m, plus a little
                foreach (var p in path)
                {
                    int cx = (int)Math.Floor(p.x / cell + N / 2f), cy = (int)Math.Floor(p.y / cell + N / 2f);
                    for (int dy = -(int)r; dy <= r; dy++)
                        for (int dx = -(int)r; dx <= r; dx++)
                        {
                            int x = cx + dx, y = cy + dy;
                            if (x < 0 || y < 0 || x >= N || y >= N || dx * dx + dy * dy > r * r) continue;
                            riverCell[y * N + x] = true;
                        }
                }
            }

            var g = new Grids { land = new ushort[n], region = new ushort[n], water = new ushort[n], range = new ushort[n], cls = cls, hgt = hgt, cell = cell };

            // landmasses
            var land = new bool[n];
            for (int i = 0; i < n; i++) land[i] = cls[i] != 0;
            Regions(land, cell, 20, area => area * cellKm2 >= 4f ? "continent" : area * cellKm2 >= 0.1f ? "island" : "holm", feats, hgt, false, g.land);

            // still water: the outer sea is whatever touches the edge; the rest are lakes
            var water = new bool[n];
            for (int i = 0; i < n; i++) water[i] = !land[i] && !riverCell[i];
            var wl = Label(water, out int wc, out int[] warea);
            var atEdge = new bool[wc + 1];
            for (int x = 0; x < N; x++) { atEdge[wl[x]] = true; atEdge[wl[(N - 1) * N + x]] = true; }
            for (int y = 0; y < N; y++) { atEdge[wl[y * N]] = true; atEdge[wl[y * N + N - 1]] = true; }
            var lakes = new bool[n];
            for (int i = 0; i < n; i++) lakes[i] = water[i] && !atEdge[wl[i]];
            Regions(lakes, cell, 12, _ => "lake", feats, null, false, g.water);

            // bays: what closing the coast by ~200 m fills in, on the open sea
            var sea = new bool[n];
            for (int i = 0; i < n; i++) sea[i] = !land[i] && atEdge[wl[i]];
            var closed = Erode(Dilate(land, 8), 8);
            var bays = new bool[n];
            for (int i = 0; i < n; i++) bays[i] = closed[i] && sea[i];
            Regions(bays, cell, 40, _ => "bay", feats, null, false, g.water);

            // mountain ranges, each with its highest point
            var mtn = new bool[n];
            byte m = Class(Heightmap.Biome.Mountain);
            for (int i = 0; i < n; i++) mtn[i] = cls[i] == m;
            Regions(mtn, cell, 40, _ => "range", feats, hgt, true, g.range);

            // the other biomes as regions, only where they are big enough to be a place
            var biomes = new[] {
                (Heightmap.Biome.BlackForest, "forest"), (Heightmap.Biome.Swamp, "swamp"), (Heightmap.Biome.Plains, "plains"),
                (Heightmap.Biome.Mistlands, "mistlands"), (Heightmap.Biome.Meadows, "meadows"),
                (Heightmap.Biome.AshLands, "ashlands"), (Heightmap.Biome.DeepNorth, "north") };
            foreach (var (b, kind) in biomes)
            {
                byte c = Class(b);
                var mask = new bool[n];
                for (int i = 0; i < n; i++) mask[i] = cls[i] == c;
                Regions(mask, cell, (int)(0.5f / cellKm2), _ => kind, feats, hgt, false, g.region);
            }

            // rivers: the path thinned to every ~50 m, named at its middle
            foreach (var path in rivers)
            {
                if (path.Count < 2) continue;
                float len = 0f;
                for (int i = 1; i < path.Count; i++) len += Vector2.Distance(path[i - 1], path[i]);
                if (len < 300f) continue;
                var f = new Feature { kind = "river", line = new List<float[]>(), area = 0f };
                float acc = 0f, next = 0f, minx = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxz = float.MinValue;
                Vector2 mid = path[path.Count / 2];
                for (int i = 0; i < path.Count; i++)
                {
                    if (i > 0) acc += Vector2.Distance(path[i - 1], path[i]);
                    if (i == 0 || i == path.Count - 1 || acc >= next)
                    {
                        f.line.Add(new[] { R1(path[i].x), R1(path[i].y) });
                        next = acc + 50f;
                    }
                    if (acc >= len / 2f && mid == path[path.Count / 2]) mid = path[i];
                    minx = Math.Min(minx, path[i].x); maxx = Math.Max(maxx, path[i].x);
                    minz = Math.Min(minz, path[i].y); maxz = Math.Max(maxz, path[i].y);
                }
                f.x = R1(mid.x); f.z = R1(mid.y); f.w = R1(maxx - minx); f.h = R1(maxz - minz); f.area = R1(len);   // a river's "area" is its length in m
                f.id = Id(f.kind, f.x, f.z);
                feats.Add(f);
            }

            var gen = new Dictionary<string, string>();
            var used = new HashSet<string>();
            foreach (var f in feats) gen[f.id] = Generate(seed, f, used);
            lock (gate)
            {
                found = feats.ToArray();
                grids = g;
                generated.Clear();
                foreach (var kv in gen) generated[kv.Key] = kv.Value;
                Rebuild();
            }
            var counts = new Dictionary<string, int>();
            foreach (var f in feats) counts[f.kind] = counts.TryGetValue(f.kind, out int k) ? k + 1 : 1;
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add(kv.Value + " " + kv.Key);
            ZLog.Log("WebMap: geography read: " + string.Join(", ", parts.ToArray()));
        }

        private static float R1(float v) => (float)Math.Round(v, 1);
        // stable across restarts: the kind and the rounded spot its name sits at
        private static string Id(string kind, float x, float z) =>
            kind + "@" + ((int)Math.Round(x / 48f) * 48).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(z / 48f) * 48).ToString(CultureInfo.InvariantCulture);

        // Every connected blob of the mask big enough becomes a feature of the kind
        // the area says, named at the cell farthest from its edge; with heights, the
        // highest cell is its peak.
        // hgt gives the elevation span; withPeak names its highest point too; into, when
        // given, is filled with 1 + each kept feature's index in feats, cell by cell
        private static void Regions(bool[] mask, float cell, int minCells, Func<int, string> kindOf, List<Feature> feats, float[] hgt, bool withPeak, ushort[] into)
        {
            int n = N * N;
            var lab = Label(mask, out int count, out int[] area);
            if (count == 0) return;
            var depth = Depth(mask);
            var best = new int[count + 1]; var bestD = new int[count + 1];
            var minx = new int[count + 1]; var miny = new int[count + 1]; var maxx = new int[count + 1]; var maxy = new int[count + 1];
            var peak = new int[count + 1]; var peakH = new float[count + 1]; var low = new float[count + 1]; var sum = new double[count + 1];
            for (int k = 1; k <= count; k++) { minx[k] = miny[k] = N; maxx[k] = maxy[k] = -1; bestD[k] = -1; peakH[k] = float.MinValue; low[k] = float.MaxValue; }
            for (int i = 0; i < n; i++)
            {
                int k = lab[i]; if (k == 0) continue;
                int x = i % N, y = i / N;
                if (depth[i] > bestD[k]) { bestD[k] = depth[i]; best[k] = i; }
                if (x < minx[k]) minx[k] = x; if (x > maxx[k]) maxx[k] = x;
                if (y < miny[k]) miny[k] = y; if (y > maxy[k]) maxy[k] = y;
                if (hgt != null)
                {
                    if (hgt[i] > peakH[k]) { peakH[k] = hgt[i]; peak[k] = i; }
                    if (hgt[i] < low[k]) low[k] = hgt[i];
                    sum[k] += hgt[i];
                }
            }
            float cellKm2 = cell * cell / 1e6f;
            var index = new ushort[count + 1];
            for (int k = 1; k <= count; k++)
            {
                if (area[k] < minCells) continue;
                var f = new Feature { kind = kindOf(area[k]), area = R1(area[k] * cellKm2 * 100f) / 100f };
                f.x = R1(World(best[k] % N, cell)); f.z = R1(World(best[k] / N, cell));
                f.w = R1((maxx[k] - minx[k] + 1) * cell); f.h = R1((maxy[k] - miny[k] + 1) * cell);
                if (hgt != null)
                {
                    f.hasElev = true; f.hmin = R1(low[k]); f.hmax = R1(peakH[k]); f.hmean = R1((float)(sum[k] / area[k]));
                    if (withPeak) { f.hasPeak = true; f.px = R1(World(peak[k] % N, cell)); f.pz = R1(World(peak[k] / N, cell)); f.py = R1(peakH[k]); }
                }
                f.id = Id(f.kind, f.x, f.z);
                if (feats.Count < ushort.MaxValue) index[k] = (ushort)(feats.Count + 1);
                feats.Add(f);
            }
            if (into != null) for (int i = 0; i < n; i++) if (lab[i] != 0 && index[lab[i]] != 0) into[i] = index[lab[i]];
        }
        private static float World(int c, float cell) => (c - N / 2f) * cell + cell / 2f;

        // 4-connected component labels, 1-based; 0 outside the mask
        private static int[] Label(bool[] mask, out int count, out int[] area)
        {
            int n = N * N;
            var lab = new int[n];
            var areas = new List<int> { 0 };
            var queue = new int[n];
            count = 0;
            for (int s = 0; s < n; s++)
            {
                if (!mask[s] || lab[s] != 0) continue;
                int k = ++count, head = 0, tail = 0, size = 0;
                queue[tail++] = s; lab[s] = k;
                while (head < tail)
                {
                    int i = queue[head++]; size++;
                    int x = i % N, y = i / N;
                    if (x > 0 && mask[i - 1] && lab[i - 1] == 0) { lab[i - 1] = k; queue[tail++] = i - 1; }
                    if (x < N - 1 && mask[i + 1] && lab[i + 1] == 0) { lab[i + 1] = k; queue[tail++] = i + 1; }
                    if (y > 0 && mask[i - N] && lab[i - N] == 0) { lab[i - N] = k; queue[tail++] = i - N; }
                    if (y < N - 1 && mask[i + N] && lab[i + N] == 0) { lab[i + N] = k; queue[tail++] = i + N; }
                }
                areas.Add(size);
            }
            area = areas.ToArray();
            return lab;
        }

        // how far each mask cell is from the mask's edge, in cells: a flood from every edge cell
        private static int[] Depth(bool[] mask)
        {
            int n = N * N;
            var d = new int[n];
            var queue = new int[n];
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++)
            {
                d[i] = -1;
                if (!mask[i]) continue;
                int x = i % N, y = i / N;
                bool edge = x == 0 || y == 0 || x == N - 1 || y == N - 1
                    || !mask[i - 1] || !mask[i + 1] || !mask[i - N] || !mask[i + N];
                if (edge) { d[i] = 0; queue[tail++] = i; }
            }
            while (head < tail)
            {
                int i = queue[head++], x = i % N, y = i / N, v = d[i] + 1;
                if (x > 0 && mask[i - 1] && d[i - 1] < 0) { d[i - 1] = v; queue[tail++] = i - 1; }
                if (x < N - 1 && mask[i + 1] && d[i + 1] < 0) { d[i + 1] = v; queue[tail++] = i + 1; }
                if (y > 0 && mask[i - N] && d[i - N] < 0) { d[i - N] = v; queue[tail++] = i - N; }
                if (y < N - 1 && mask[i + N] && d[i + N] < 0) { d[i + N] = v; queue[tail++] = i + N; }
            }
            return d;
        }

        // square dilation and erosion by r cells, as two one-dimensional passes
        private static bool[] Dilate(bool[] a, int r) => Pass(Pass(a, r, true, true), r, false, true);
        private static bool[] Erode(bool[] a, int r) => Pass(Pass(a, r, true, false), r, false, false);
        private static bool[] Pass(bool[] a, int r, bool rows, bool any)
        {
            var o = new bool[a.Length];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    bool v = !any;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = rows ? x + k : x, yy = rows ? y : y + k;
                        bool s = xx >= 0 && yy >= 0 && xx < N && yy < N && a[yy * N + xx];
                        if (any ? s : !s) { v = any; break; }
                    }
                    o[y * N + x] = v;
                }
            return o;
        }

        // ---------- names ----------
        // Old Norse enough to belong: a stem, sometimes a genitive, and the ending
        // the kind of place takes. Deterministic from the seed and the feature, so
        // every viewer sees the same map and a restart changes nothing.
        private static readonly string[] Stems = {
            "Ask", "Bjarn", "Brim", "Dag", "Eld", "Fenn", "Frost", "Gard", "Grim", "Gull", "Hav", "Hjal", "Isa", "Jarn",
            "Kald", "Lang", "Nord", "Orm", "Ragn", "Sten", "Torn", "Ulf", "Var", "Vind", "Ygg", "Aud", "Ein", "Skal",
            "Hrim", "Sval", "Ravn", "Bjork", "Eik", "Lind", "Hol", "Sol", "Mor", "Raud", "Gra", "Svart", "Hvit", "Blak",
            "Kol", "Sig", "Thor", "Odd", "Rune", "Hild", "Alf", "Ger", "Gunn", "Ing", "Ketil", "Odin", "Tyr", "Vale",
            "Agn", "Arn", "Bal", "Berg", "Bod", "Dyr", "Egg", "Eir", "Fal", "Fjor", "Galt", "Geir", "Hakon", "Hall", "Har",
            "Heid", "Helg", "Hjort", "Hrafn", "Ivar", "Jor", "Kar", "Knut", "Leif", "Loft", "Magn", "Mjol", "Njal", "Ol",
            "Ott", "Rag", "Rand", "Rolf", "Saem", "Sand", "Sindr", "Skeg", "Snor", "Stein", "Styr", "Sun", "Svein", "Tind",
            "Tofa", "Trygg", "Ulfr", "Una", "Vig", "Vott", "Yng", "Yr", "Aesir", "Bragi", "Freyr", "Idun", "Loki", "Skadi" };
        // between stem and ending: nothing, a genitive, or a joining vowel
        private static readonly string[] Links = { "", "", "s", "s", "ar", "a", "e", "i", "u", "en" };
        private static string Suffix(string kind)
        {
            switch (kind)
            {
                case "continent": return "land";
                case "island":    return "ey";
                case "holm":      return "holm";
                case "range":     return "fjell";
                case "lake":      return "vatn";
                case "river":     return "á";
                case "bay":       return "vik";
                case "forest":    return "mork";
                case "swamp":     return "myr";
                case "plains":    return "vellir";
                case "meadows":   return "eng";
                case "mistlands": return "dimma";
                case "ashlands":  return "brand";
                case "north":     return "frost";
                default:          return "stad";
            }
        }
        private static string Generate(string seed, Feature f, HashSet<string> used)
        {
            string name = null;
            for (int salt = 0; salt < 24; salt++)
            {
                uint h = (uint)Fnv.Of(seed + "|" + f.id + "|" + salt);
                string stem = Stems[h % (uint)Stems.Length], end = Suffix(f.kind);
                bool vowel = "aeiouy".IndexOf(char.ToLowerInvariant(stem[stem.Length - 1])) >= 0;
                // two vowels meeting read badly (Isaá, Ravnuá): before a vowel ending the
                // stem gives up its last vowel and the link is a consonant or nothing
                bool vowelEnd = "aeiouyá".IndexOf(end[0]) >= 0;
                if (vowel && vowelEnd) { stem = stem.Substring(0, stem.Length - 1); vowel = false; }
                string link = vowel ? "" : Links[(h >> 8) % (uint)Links.Length];
                if (vowelEnd && link.Length > 0 && "aeiou".IndexOf(link[link.Length - 1]) >= 0) link = "";
                name = stem + link + end;
                if (used.Add(name)) return name;
            }
            return name;
        }

        // ---------- the document ----------
        // one feature as JSON, without its closing brace so a caller can add to it
        private static void Open(StringBuilder sb, Feature f)      // under gate
        {
            bool given = names.TryGetValue(f.id, out var nm) && !string.IsNullOrEmpty(nm.name);
            string name = given ? nm.name : (generated.TryGetValue(f.id, out var g) ? g : f.kind);
            sb.Append(Inv($"{{\"id\":\"{Esc(f.id)}\",\"kind\":\"{f.kind}\",\"name\":\"{Esc(name)}\",\"x\":{f.x:0.#},\"z\":{f.z:0.#},\"w\":{f.w:0.#},\"h\":{f.h:0.#},\"area\":{f.area:0.##}"));
            if (given) sb.Append(Inv($",\"by\":\"{Esc(nm.by)}\",\"t\":{nm.t}"));
            if (f.hasPeak) sb.Append(Inv($",\"peak\":{{\"x\":{f.px:0.#},\"z\":{f.pz:0.#},\"y\":{f.py:0.#}}}"));
            if (f.hasElev) sb.Append(Inv($",\"elev\":{{\"min\":{f.hmin:0.#},\"max\":{f.hmax:0.#},\"mean\":{f.hmean:0.#}}}"));
            if (f.line != null)
            {
                sb.Append(",\"line\":[");
                for (int i = 0; i < f.line.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Inv($"[{f.line[i][0]:0.#},{f.line[i][1]:0.#}]")); }
                sb.Append(']');
            }
        }
        private static void Rebuild()      // under gate
        {
            var sb = new StringBuilder("{\"features\":[");
            int n = 0;
            foreach (var f in found)
            {
                if (n++ > 0) sb.Append(',');
                Open(sb, f);
                sb.Append('}');
            }
            sb.Append("],\"count\":").Append(n);
            int rev = Fnv.Of(sb.ToString());
            sb.Append(",\"rev\":").Append(rev).Append('}');
            json = sb.ToString();
            Rev = rev;
        }
        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Json() => json;

        // What is at a spot: the biome and height there, and the places it lies in,
        // most particular first, each with how much of it has been walked and what
        // stands on it. Null for ground nobody has walked: that stays a mystery.
        public static string At(float x, float z)
        {
            var g = grids; if (g == null) return null;
            int cx = (int)Math.Floor(x / g.cell + N / 2f), cz = (int)Math.Floor(z / g.cell + N / 2f);
            if (cx < 0 || cz < 0 || cx >= N || cz >= N) return null;
            if (!MapFog.Explored(x, z)) return null;
            int i = cz * N + cx;
            var sb = new StringBuilder();
            sb.Append(Inv($"{{\"x\":{x:0.#},\"z\":{z:0.#},\"biome\":\"{BiomeName(g.cls[i])}\",\"height\":{g.hgt[i]:0.#},\"here\":["));
            int n = 0;
            lock (gate)
            {
                foreach (var layer in new[] { g.water, g.range, g.region, g.land })
                {
                    int k = layer[i]; if (k == 0 || k > found.Length) continue;
                    var f = found[k - 1];
                    if (n++ > 0) sb.Append(',');
                    Open(sb, f);
                    Measure(g, layer, (ushort)k, f, out float walked, out int builds, out int portals);
                    sb.Append(Inv($",\"explored\":{walked:0.###},\"builds\":{builds},\"portals\":{portals}}}"));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }
        // the share of a place's cells someone has walked, and the pieces and portals in it
        private static void Measure(Grids g, ushort[] layer, ushort k, Feature f, out float walked, out int builds, out int portals)
        {
            int cells = 0, seen = 0;
            var fog = WebMap.mapDataServer != null ? WebMap.mapDataServer.fogRgba : null;
            int size = TEXTURE_SIZE, per = size / N;
            for (int z = 0; z < N; z++)
            {
                int row = z * N;
                for (int x = 0; x < N; x++)
                {
                    if (layer[row + x] != k) continue;
                    cells++;
                    if (fog == null) { seen++; continue; }
                    int px = x * per + per / 2, py = z * per + per / 2;
                    if (fog[(py * size + px) * 4] > 127) seen++;
                }
            }
            walked = cells == 0 ? 0f : (float)seen / cells;
            builds = Count(g, layer, k, Pieces.Positions);
            portals = Count(g, layer, k, Portals.Positions);
        }
        private static int Count(Grids g, ushort[] layer, ushort k, float[] pos)
        {
            int n = 0;
            if (pos == null) return 0;
            for (int i = 0; i + 1 < pos.Length; i += 2)
            {
                int cx = (int)Math.Floor(pos[i] / g.cell + N / 2f), cz = (int)Math.Floor(pos[i + 1] / g.cell + N / 2f);
                if (cx < 0 || cz < 0 || cx >= N || cz >= N) continue;
                if (layer[cz * N + cx] == k) n++;
            }
            return n;
        }
        private static string BiomeName(byte c)
        {
            if (c == 0) return "Ocean";
            switch ((Heightmap.Biome)(1 << (c - 1)))
            {
                case Heightmap.Biome.Meadows: return "Meadows";
                case Heightmap.Biome.BlackForest: return "Black Forest";
                case Heightmap.Biome.Swamp: return "Swamp";
                case Heightmap.Biome.Mountain: return "Mountain";
                case Heightmap.Biome.Plains: return "Plains";
                case Heightmap.Biome.Mistlands: return "Mistlands";
                case Heightmap.Biome.AshLands: return "Ashlands";
                case Heightmap.Biome.DeepNorth: return "Deep North";
                default: return "Ocean";
            }
        }

        // A name given on the site (or cleared, so the generated one returns).
        // Returns null when it took, else what was wrong.
        public static string SetName(string id, string name, string by)
        {
            id = (id ?? "").Trim(); by = (by ?? "").Trim();
            name = Regex.Replace(name ?? "", @"[\p{C}]", "").Trim();
            if (name.Length > 40) return "a name is at most 40 characters";
            lock (gate)
            {
                Feature f = null;
                foreach (var g in found) if (g.id == id) { f = g; break; }
                if (f == null) return "no such place";
                string was = names.TryGetValue(id, out var old) && !string.IsNullOrEmpty(old.name) ? old.name : (generated.TryGetValue(id, out var gn) ? gn : "");
                if (name.Length == 0) names.Remove(id);
                else names[id] = new Named { name = name, by = by, t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                Rebuild();
                Save();
                ZLog.Log($"WebMap: {(by.Length > 0 ? by : "someone")} named the {f.kind} '{was}' " + (name.Length == 0 ? "back to its own name" : $"'{name}'"));
            }
            return null;
        }

        private static void Save()        // under gate
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in names)
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.name.Replace('\t', ' ')).Append('\t')
                      .Append((kv.Value.by ?? "").Replace('\t', ' ')).Append('\t').Append(kv.Value.t.ToString(CultureInfo.InvariantCulture)).Append('\n');
                string p = Path.Combine(dir, NamesFile), tmp = p + ".new";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: names not saved: " + e.Message); }
        }

        // A body of {"id":"...","name":"..."}: the two strings, escapes honoured.
        public static bool ParseBody(string body, out string id, out string name)
        {
            id = Body.Str(body, "id"); name = Body.Str(body, "name");
            return id != null && name != null;
        }
    }
}
