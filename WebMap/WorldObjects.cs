// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WebMap.Models;

namespace WebMap
{
    // Every visible object in the world, per 256 m chunk, for the 3D view:
    // player pieces, the world's ruins, trees, rocks, boats, carts, furniture --
    // anything with a mesh, whoever placed it. Creatures, items and effects are
    // left out. Each record is prefab + position + rotation + scale, which with
    // the prefab's exported model reproduces the object as the game draws it.
    //
    // Fed by the structures sweep on the game thread; only chunks somebody has
    // walked are recorded at all, so nothing about unwalked ground is ever held,
    // let alone served. Finish runs on the sweep's pool thread and swaps the
    // chunks in whole; the bytes are built lazily on the HTTP thread.
    //
    // Chunk (cx, cz) covers x in [cx*256, cx*256+256), z likewise.
    internal static class WorldObjects
    {
        public const int ChunkSize = 256;
        public enum Cat : byte { Skip = 0, Piece = 1, Tree = 2, Bush = 3, Rock = 4, Other = 5 }

        public struct Obj
        {
            public int prefab; public float x, y, z; public float qx, qy, qz, qw; public float sx, sy, sz; public bool creator;
            public byte[] gone;   // a mined rock: bit i set when hit area i is broken off
        }

        // Rocks mined in pieces: MineRock5 keeps every area's health in one packed value
        // (MineRock5.LoadHealth), MineRock one float per area under "Health<i>".
        private struct Rock { public int kind, areas; }
        private static readonly Dictionary<int, Rock> rocks = new Dictionary<int, Rock>();
        private static readonly List<int> healthKeys = new List<int>();

        private sealed class Chunk { public int rev; public Obj[] objs; public byte[] bytes; }

        private static readonly Dictionary<int, Cat> catCache = new Dictionary<int, Cat>();
        private static HashSet<string> enabledCats;
        private static readonly ConcurrentDictionary<int, Chunk> chunks = new ConcurrentDictionary<int, Chunk>();
        private static readonly ConcurrentDictionary<int, int> chunkHash = new ConcurrentDictionary<int, int>();
        // Per-chunk lists kept from sweep to sweep and refilled: a world holds most of a
        // million of these, and fresh lists every minute were a heap of garbage a sweep.
        private static readonly Dictionary<int, List<Obj>> lists = new Dictionary<int, List<Obj>>();
        private static bool building;
        private static readonly int hashScale = "scale".GetStableHashCode();
        private static readonly int hashScaleScalar = "scaleScalar".GetStableHashCode();

        public static volatile int Rev;            // bumped when any chunk changed; 0 until the first sweep
        public static int Total { get; private set; }

        public static int ChunkOf(float w) => (int)Math.Floor(w / ChunkSize);
        private static int Key(int cx, int cz) => (cx + 64) * 128 + (cz + 64);

        public static string CatName(Cat c)
        {
            switch (c) { case Cat.Piece: return "piece"; case Cat.Tree: return "tree"; case Cat.Bush: return "bush"; case Cat.Rock: return "rock"; case Cat.Other: return "other"; default: return "skip"; }
        }

        // Game thread, at the start of the walk.
        public static void Begin()
        {
            foreach (var l in lists.Values) l.Clear();
            building = true;
        }

        private static bool Enabled(Cat c)
        {
            if (enabledCats == null)
            {
                enabledCats = new HashSet<string>();
                foreach (var part in (WebMapConfig.OBJECT_CATEGORIES ?? "").Split(',')) { string t = part.Trim().ToLowerInvariant(); if (t.Length > 0) enabledCats.Add(t); }
                if (enabledCats.Count == 0) { enabledCats.Add("piece"); enabledCats.Add("other"); enabledCats.Add("rock"); }
            }
            return enabledCats.Contains(CatName(c));
        }

        // Game thread, from the walk. The fog test comes first: an unwalked chunk
        // costs one array read and no ZDO lookups.
        public static void Observe(ZDO zdo, int prefabHash, Vector3 pos, long creator)
        {
            if (!building) return;
            int cx = ChunkOf(pos.x), cz = ChunkOf(pos.z);
            if (cx < -64 || cz < -64 || cx > 63 || cz > 63 || !MapFog.ChunkExplored(cx, cz)) return;
            Cat cat = Classify(prefabHash);
            if (cat == Cat.Skip) return;
            var o = new Obj { prefab = prefabHash, x = pos.x, y = pos.y, z = pos.z, qw = 1f, sx = 1f, sy = 1f, sz = 1f, creator = creator != 0L };
            try { Quaternion q = zdo.GetRotation(); o.qx = q.x; o.qy = q.y; o.qz = q.z; o.qw = q.w; } catch { }
            try
            {
                Vector3 s = zdo.GetVec3(hashScale, Vector3.one);
                if (s.x > 0 && s.y > 0 && s.z > 0) { o.sx = s.x; o.sy = s.y; o.sz = s.z; }
                float ss = zdo.GetFloat(hashScaleScalar, 1f);
                if (ss > 0 && Math.Abs(ss - 1f) > 0.001f) { o.sx *= ss; o.sy *= ss; o.sz *= ss; }
            }
            catch { }
            if (rocks.TryGetValue(prefabHash, out var rock))
                try { o.gone = rock.kind == 5 ? GoneFromHealth(Packed(zdo)) : GoneFromFloats(zdo, rock.areas); } catch { }
            int key = Key(cx, cz);
            if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<Obj>(256);
            list.Add(o);
        }

        // MineRock5's value: a ZPackage of an int count and a float per area, once a base64
        // string, read as bytes too in case a later game stores it so
        private static byte[] Packed(ZDO zdo)
        {
            byte[] raw = zdo.GetByteArray(ZDOVars.s_health);
            if (raw != null) return raw;
            string s = zdo.GetString(ZDOVars.s_health, "");
            return s.Length > 0 ? Convert.FromBase64String(s) : null;
        }

        // bit i for each area at or below zero health; null when none is
        internal static byte[] GoneFromHealth(byte[] raw)
        {
            if (raw == null || raw.Length < 4) return null;
            int n = Math.Min(BitConverter.ToInt32(raw, 0), Math.Min((raw.Length - 4) / 4, 4096));
            byte[] gone = null;
            for (int i = 0; i < n; i++)
                if (BitConverter.ToSingle(raw, 4 + i * 4) <= 0f)
                {
                    if (gone == null) gone = new byte[(n + 7) / 8];
                    gone[i >> 3] |= (byte)(1 << (i & 7));
                }
            return gone;
        }

        private static byte[] GoneFromFloats(ZDO zdo, int areas)
        {
            byte[] gone = null;
            while (healthKeys.Count < areas) healthKeys.Add(("Health" + healthKeys.Count).GetStableHashCode());
            for (int i = 0; i < areas; i++)
                if (zdo.GetFloat(healthKeys[i], 1f) <= 0f)
                {
                    if (gone == null) gone = new byte[(areas + 7) / 8];
                    gone[i >> 3] |= (byte)(1 << (i & 7));
                }
            return gone;
        }

        // Game thread (ZNetScene), once per prefab.
        private static Cat Classify(int prefabHash)
        {
            if (catCache.TryGetValue(prefabHash, out var c)) return c;
            c = Cat.Skip;
            GameObject go = null;
            try { go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null; } catch { }
            if (go != null && PrefabExporter.IsVisibleThing(go))
            {
                // counted as the game counts its hit areas (PrefabExporter names each part's)
                var r5 = go.GetComponent<MineRock5>();
                var r1 = r5 == null ? go.GetComponent<MineRock>() : null;
                if (r5 != null || r1 != null) rocks[prefabHash] = new Rock { kind = r5 != null ? 5 : 1, areas = PrefabExporter.HitAreas(go).Length };
                string n = go.name.ToLowerInvariant();
                if (go.GetComponent("Piece") != null || go.GetComponent("WearNTear") != null) c = Cat.Piece;
                else if (n.Contains("_log") || n.EndsWith("logs") || n.Contains("_trunk") || n.Contains("_stub") || n.Contains("stubbe")
                         || go.GetComponent("TreeBase") != null || go.GetComponent("TreeLog") != null) c = Cat.Tree;
                else c = ClassifyName(n);
                if (c == Cat.Other && (n.Contains("_ragdoll") || n.Contains("smoke") || n.Contains("cloud") || n.Contains("_proxy") || n == "locationproxy")) c = Cat.Skip;
                if (c != Cat.Skip && !Enabled(c)) c = Cat.Skip;
                if (c != Cat.Skip && WebMapConfig.EXPORT_MODELS) ModelStore.Request(prefabHash, CatName(c));
            }
            catCache[prefabHash] = c;
            return c;
        }

        // the world's own vegetation, by the names Valheim gives it
        private static Cat ClassifyName(string n)
        {
            if (n.Contains("raspberry") || n.Contains("blueberry") || n.Contains("cloudberry") || n.StartsWith("bush") || n.Contains("shrub")) return Cat.Bush;
            if (n.Contains("silvervein") || n.Contains("mudpile") || n.Contains("_copper") || n.Contains("minerock") || n.Contains("_tin")
                || n.Contains("meteorite") || n.StartsWith("cliff") || n.StartsWith("giant_") || n.StartsWith("rock") || n.StartsWith("highrock")) return Cat.Rock;
            if (n.StartsWith("beech") || n.StartsWith("oak") || n.StartsWith("birch") || n.StartsWith("fir") || n.StartsWith("pine")
                || n.StartsWith("swamptree") || n.StartsWith("yggashoot") || n.Contains("tree")) return Cat.Tree;
            return Cat.Other;
        }

        // The sweep's pool thread, after the walk. Returns the number of chunks that changed.
        public static int Finish()
        {
            if (!building) return 0;
            building = false;
            int changed = 0, total = 0;
            var seen = new HashSet<int>();
            foreach (var kv in lists)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;
                total += list.Count;
                list.Sort((a, b) => a.prefab != b.prefab ? a.prefab.CompareTo(b.prefab) : a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
                int h = 17;
                foreach (var o in list)
                {
                    h = unchecked(h * 31 + o.prefab + (int)(o.x * 10) * 7 + (int)(o.z * 10) * 13 + (int)(o.y * 10) * 3 + (int)(o.qy * 1000) * 101 + (int)(o.sx * 100));
                    if (o.gone != null) foreach (byte b in o.gone) h = unchecked(h * 31 + b + 1);   // a piece mined off moves the chunk
                }
                seen.Add(kv.Key);
                if (chunkHash.TryGetValue(kv.Key, out int old) && old == h) continue;
                chunkHash[kv.Key] = h;
                int rev = (chunks.TryGetValue(kv.Key, out var prev) ? prev.rev : 0) + 1;
                chunks[kv.Key] = new Chunk { rev = rev, objs = list.ToArray() };
                changed++;
            }
            foreach (int key in new List<int>(chunks.Keys))
            {
                if (seen.Contains(key)) continue;
                chunks.TryRemove(key, out _);
                chunkHash.TryRemove(key, out _);
                changed++;
            }
            Total = total;
            if (changed > 0 || Rev == 0) Rev++;
            return changed;
        }

        // Any thread. A walked chunk with nothing in it is an empty OBJ2; an unwalked
        // one is null, and the caller answers 404. rev is the chunk's own revision.
        //
        // Little-endian: 'OBJ2', u32 count, u32 prefabCount, i32[prefabCount] prefab
        // hashes, then per object: u16 prefab index, u8 flags (1 = player-built, 2 = pieces
        // mined off), u8 pad, f32 x y z, f32 qx qy qz qw, f32 sx sy sz (44 bytes; Unity's
        // frame, y up, z north). Then the mined rocks: u32 count, and per rock u32 object
        // index, u16 bits, bits/8 bytes, bit i (byte i>>3, bit i&7) set when hit area i is
        // gone. OBJ1 was the same without the flag and the table.
        public static byte[] ChunkBytes(int cx, int cz, out int rev)
        {
            rev = 0;
            if (!MapFog.ChunkExplored(cx, cz)) return null;
            if (!chunks.TryGetValue(Key(cx, cz), out var c)) return Empty;
            rev = c.rev;
            byte[] cached = c.bytes;
            if (cached != null) return cached;
            return c.bytes = Encode(c.objs);
        }

        private static byte[] Encode(Obj[] objs)
        {
            var table = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var o in objs) if (!table.ContainsKey(o.prefab)) { table[o.prefab] = order.Count; order.Add(o.prefab); }
            using (var ms = new MemoryStream(16 + order.Count * 4 + objs.Length * 44))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)'O'); bw.Write((byte)'B'); bw.Write((byte)'J'); bw.Write((byte)'2');
                bw.Write(objs.Length); bw.Write(order.Count);
                foreach (int p in order) bw.Write(p);
                int mined = 0;
                foreach (var o in objs)
                {
                    if (o.gone != null) mined++;
                    bw.Write((ushort)table[o.prefab]); bw.Write((byte)((o.creator ? 1 : 0) | (o.gone != null ? 2 : 0))); bw.Write((byte)0);
                    bw.Write(o.x); bw.Write(o.y); bw.Write(o.z);
                    bw.Write(o.qx); bw.Write(o.qy); bw.Write(o.qz); bw.Write(o.qw);
                    bw.Write(o.sx); bw.Write(o.sy); bw.Write(o.sz);
                }
                bw.Write(mined);
                for (int i = 0; i < objs.Length; i++)
                {
                    if (objs[i].gone == null) continue;
                    bw.Write(i); bw.Write((ushort)(objs[i].gone.Length * 8)); bw.Write(objs[i].gone);
                }
                bw.Flush();
                return ms.ToArray();
            }
        }

        // for the tests: a chunk's bytes from objects as the walk would find them
        internal static byte[] EncodeForTests(Obj[] objs) => Encode(objs);

        private static readonly byte[] Empty = { (byte)'O', (byte)'B', (byte)'J', (byte)'2', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }
}
