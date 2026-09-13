using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    // A sweep walks every ZDO, so it is spread across frames and runs only while
    // someone is reading it, at most once a minute: it shares a CPU with the game.
    // Only the walk touches the game. Everything after it -- the rendering, the
    // forest blur, the JSON, the PNG encodes -- runs on a pool thread, so the
    // game thread pays for the walk and nothing else.
    internal static class StructureMap
    {
        private const int ZdosPerFrame = 3000;
        // A read arms the gate for WatchMs; sweeps start at least FloorMs apart.
        private const int WatchMs = 120_000;
        private const int FloorMs = 60_000;

        // Only a few hundred pixels ever hold anything, so a dictionary costs a
        // fraction of a full-map array and needs no clearing pass.
        private struct Cell { public int n, r, g, b; }
        private static readonly Dictionary<int, Cell> cells = new Dictionary<int, Cell>();
        private static readonly Dictionary<int, Color32> paletteCache = new Dictionary<int, Color32>();
        private static byte[] rgba;                       // render target, reused between sweeps
        private static volatile string statsJson = "{\"total\":0,\"prefabs\":[]}";
        private static volatile byte[] png;
        public static volatile int Rev;                    // content revision of the PNG
        private static volatile bool sweeping;            // set on the game thread, cleared by the pool thread

        // Tick of the last read that wants a sweep. Written from HTTP threads (an
        // int store is atomic); only the main thread scans. Un-watched at boot.
        // Deltas compare unsigned: a wrapped tick reads as long ago, never as recent.
        public static volatile int LastRead = unchecked(Environment.TickCount - WatchMs);
        private static int lastSweepStart = unchecked(Environment.TickCount - FloorMs);

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
            if (rgba != null) return;
            int size = WebMapConfig.TEXTURE_SIZE;
            rgba = new byte[size * size * 4];                 // zeroed = fully transparent
            CheckEncoder();
            var empty = new byte[rgba.Length];                // /structures answers from boot, not from the first sweep
            ThreadPool.QueueUserWorkItem(_ => { try { if (png == null) png = ImageConv.EncodeRgbaToPNG(empty, size, size); } catch { } });
        }

        // The overlays used to be Texture2Ds encoded with EncodeToPNG; they are raw
        // buffers encoded with EncodeArrayToPNG now. Both take texture layout, bottom
        // row first, so the bytes should agree -- this says so in the log, once.
        private static void CheckEncoder()
        {
            try
            {
                var t = new Texture2D(1, 2, TextureFormat.RGBA32, false);
                t.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(0, 0, 255, 255) });
                t.Apply();
                byte[] a = ImageConv.EncodeToPNG(t);
                byte[] b = ImageConv.EncodeRgbaToPNG(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, 1, 2);
                UnityEngine.Object.Destroy(t);
                bool same = a.Length == b.Length;
                for (int i = 0; same && i < a.Length; i++) same = a[i] == b[i];
                if (same) ZLog.Log("WebMap: array PNG encoder agrees with the texture encoder");
                else ZLog.LogWarning($"WebMap: array PNG encoder differs from the texture encoder ({a.Length} vs {b.Length} bytes): overlays may be upside down");
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: PNG encoder check failed: " + e.Message); }
        }

        public static IEnumerator Loop()
        {
            Init();
            while (true)
            {
                int now = Environment.TickCount;
                if (unchecked((uint)(now - LastRead)) < WatchMs && unchecked((uint)(now - lastSweepStart)) >= FloorMs)
                {
                    // stepped by hand: a throw inside the walk would otherwise end this
                    // coroutine with `sweeping` stuck true and no sweep ever again
                    var it = Sweep();
                    while (true)
                    {
                        try { if (!it.MoveNext()) break; }
                        catch (Exception e) { ZLog.LogWarning("WebMap: sweep walk failed: " + e); sweeping = false; break; }
                        yield return it.Current;
                    }
                }
                yield return new WaitForSeconds(1f);
            }
        }

        private static IEnumerator Sweep()
        {
            if (sweeping) yield break;                        // the last finish is still on its thread
            sweeping = true;
            Init();
            lastSweepStart = Environment.TickCount;
            int gc2 = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            var walk = Stopwatch.StartNew();                  // paused across yields: our slices only
            int frames = 0;

            int size = WebMapConfig.TEXTURE_SIZE;
            int half = size / 2;
            cells.Clear();                                    // rebuilt each sweep so demolitions vanish
            ForestMap.Begin();                                // shares this one walk of the world
            Vehicles.Begin();
            Portals.Begin();
            Pieces.Begin();
            Graves.Begin();
            Stats.BeginSweep();

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
                                Stats.ObservePiece(creator, false, veh == Vehicles.Kind.Boat);
                            }
                            else
                            {
                                // a portal is part of a build, so it is reported AND painted
                                bool portal = Portals.IsPortal(pref);
                                if (portal) Portals.Observe(zdo, p);
                                Pieces.Observe(pref, zdo, p);      // the same piece, as a footprint
                                Stats.ObservePiece(creator, portal, false);
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
                if (seen % ZdosPerFrame == 0)                     // never stall a frame
                {
                    walk.Stop(); frames++;
                    yield return null;
                    walk.Start();
                }
            }
            walk.Stop();

            // The walk is over and nothing below reads the game: hand the rest to
            // the pool. The per-sweep lists stay quiet until the pool thread clears
            // `sweeping`, which is what lets the next walk begin.
            int started = lastSweepStart, gcBefore = gc2, walkFrames = frames;
            long walkMs = walk.ElapsedMilliseconds;
            ThreadPool.QueueUserWorkItem(_ => Finish(size, byPrefab, found, seen, walkMs, walkFrames, wall, gcBefore, started));
        }

        private static void Finish(int size, Dictionary<int, int> byPrefab, int found, int seen,
                                   long walkMs, int frames, Stopwatch wall, int gcBefore, int started)
        {
            try
            {
                var finish = Stopwatch.StartNew();
                Render(size);
                var bytes = ImageConv.EncodeRgbaToPNG(rgba, size, size);
                png = bytes; Rev = Fnv.Of(bytes);
                ForestMap.Finish();
                Vehicles.Finish();
                Portals.Finish();
                Pieces.Finish();
                Graves.Finish();
                Stats.PublishSweep();
                finish.Stop();

                int gc2 = GC.CollectionCount(2) - gcBefore;
                string sweep = FormattableString.Invariant($"{{\"at\":{started},\"zdos\":{seen},\"walk_ms\":{walkMs},\"finish_ms\":{finish.ElapsedMilliseconds},\"frames\":{frames},\"wall_ms\":{wall.ElapsedMilliseconds},\"gc2\":{gc2}}}");
                statsJson = BuildStats(byPrefab, found, seen, sweep);
                ZLog.Log($"WebMap: structures sweep -> {found} placed pieces from {seen} zdos; "
                       + $"forest {ForestMap.LastTrees} trees / {ForestMap.LastStumps} stumps; "
                       + $"walk {walkMs} ms over {frames} frames + finish {finish.ElapsedMilliseconds} ms off-thread, "
                       + $"{wall.ElapsedMilliseconds} ms wall, {gc2} gen2 gc");
            }
            catch (Exception e)
            {
                ZLog.LogWarning("WebMap: sweep finish failed: " + e);
            }
            finally
            {
                sweeping = false;
            }
        }

        // Each pixel takes the weighted average albedo of the pieces standing on
        // it, so a stone keep reads grey and a thatch longhouse reads straw.
        // Density now only drives opacity -- it used to tint toward yellow, which
        // made a busy base look like it was on fire.
        private static void Render(int size)
        {
            Array.Clear(rgba, 0, rgba.Length);
            foreach (var kv in cells)
            {
                var c = kv.Value;
                if (c.n <= 0) continue;
                int o = kv.Key * 4;
                rgba[o] = (byte)(c.r / c.n); rgba[o + 1] = (byte)(c.g / c.n); rgba[o + 2] = (byte)(c.b / c.n);
                rgba[o + 3] = (byte)Mathf.Clamp(120 + c.n * 20, 120, 255);
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
            int pixels = size * size;
            foreach (var kv in spill)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= pixels) continue;
                if (cells.ContainsKey(idx)) continue;          // never dim a cell with real pieces
                int o = idx * 4;
                if (rgba[o + 3] >= kv.Value.a) continue;
                rgba[o] = kv.Value.r; rgba[o + 1] = kv.Value.g; rgba[o + 2] = kv.Value.b; rgba[o + 3] = kv.Value.a;
            }
        }

        // Pool thread: prefab names come from the cache the walk filled, never
        // from ZNetScene.
        private static string BuildStats(Dictionary<int, int> byPrefab, int found, int seen, string sweep)
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
                if (n > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(Pieces.NameOf(kv.Key).Replace("\"", "")).Append("\",\"count\":").Append(kv.Value).Append("}");
                n++;
            }
            sb.Append("],\"sweep\":").Append(sweep).Append("}");
            return sb.ToString();
        }

        public static string GetStats() => statsJson;

        public static byte[] GetPng() => png ?? new byte[0];
    }
}
