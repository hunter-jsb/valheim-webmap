// Ported from f00d4tehg0dz/valheim-webmap (MIT).
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using WebMap.Util;

namespace WebMap.Models
{
    // The model library: one glTF per prefab the world contains, exported on
    // the game thread a few per frame and kept on disk under
    // map_data/models (prefabs are the same for every world). An index
    // records what was exported, with bounds and triangle counts, so a
    // restart exports nothing again and the browser knows which prefabs
    // have a model and which need a box. Served at /models/<file> and
    // /prefabs; the extracted meshes under models/meshes are game data and
    // never leave the server. A world location is no network prefab: its
    // prefab is ZoneSystem's, loaded for the export and let go after.
    internal static class ModelStore
    {
        public const int FORMAT = 6;   // 5: locked meshes come from the mesh cache; 6: a mineable rock names the hit area of each part

        public sealed class Info
        {
            public int hash; public string name; public string cat; public bool ok; public int tris; public bool tex; public float[] bounds = new float[6]; public int renderers, unreadable;
            public int ver;                   // hash of the .glb: the page's ?v=, so a re-export is fetched anew and the rest stay cached
            public int rockKind;              // 5 MineRock5, 1 MineRock, 0 not a rock mined in pieces
            public int[] partAreas;           // per glTF primitive, its hit area (-1 none, -2 MineRock's whole rock)
            public List<string> wants = new List<string>();   // texture names its materials reference
            public List<string> meshWants = new List<string>();     // locked meshes it uses from the mesh cache (MeshCache.Key)
            public List<string> meshMissing = new List<string>();   // those the cache did not have when it was exported
            public float[] canopy;            // foliage bounds in model space (x0,y0,z0,x1,y1,z1), or null
            public string canopyTex;          // leaf texture file, or null
            public float[] canopyColor;       // leaf tint 0..1
            public string ct, lt;             // a rig part's paint over the body's chest and legs, texture files
            public int tp;                    // a rig part's wanted textures that were there when it was exported
        }

        private static string root;
        private static readonly ConcurrentDictionary<int, Info> index = new ConcurrentDictionary<int, Info>();
        private static readonly Queue<int> queue = new Queue<int>();
        private static readonly HashSet<int> queued = new HashSet<int>();
        private static readonly Dictionary<int, string> catOf = new Dictionary<int, string>();
        private static readonly ConcurrentDictionary<int, string> rigNames = new ConcurrentDictionary<int, string>();   // a rig part's hash is its name's
        private static volatile string prefabsJson = "{\"rev\":0,\"prefabs\":{}}";
        private static volatile int rev;
        // one export run: from a prefab queued on an empty queue to the queue running dry
        private static System.Diagnostics.Stopwatch run;
        private static int runStart, quiet;
        private static double runBusy;         // seconds from the run's first export to its last
        // the run's worst game-thread frame, and its slowest single step and whose it was
        private static double runFrameMs, runStepMs; private static int runStepHash;
        private static bool indexDirty;
        private static int idleTicks;
        private static volatile bool extractDone;
        private static volatile bool meshDone;
        public static int Exported { get; private set; }
        public static int Readable { get; private set; }
        public static int Unreadable { get; private set; }

        public static string Root => root;
        public static int Rev => rev;
        public static string PrefabsJson => prefabsJson;
        public static int QueueLength { get { lock (queue) return queue.Count; } }

        public static void Init(string mapDataPath)
        {
            root = Path.Combine(mapDataPath, "models");
            Directory.CreateDirectory(root);
            index.Clear();
            try
            {
                string path = Path.Combine(root, "index.json");
                if (File.Exists(path))
                {
                    var doc = JsonParser.ParseObject(File.ReadAllText(path));
                    if ((int)JsonParser.Num(doc, "format") == FORMAT)
                    {
                        var ps = JsonParser.Obj(doc, "prefabs");
                        if (ps != null)
                            foreach (var kv in ps)
                            {
                                var d = kv.Value as Dictionary<string, object>;
                                if (d == null || !int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) continue;
                                var info = new Info { hash = h, name = JsonParser.Str(d, "n"), cat = JsonParser.Str(d, "c"), ok = JsonParser.Bool(d, "m"), tris = (int)JsonParser.Num(d, "t"), tex = JsonParser.Bool(d, "x") };
                                var b = JsonParser.Arr(d, "b");
                                if (b != null && b.Count == 6) for (int i = 0; i < 6; i++) info.bounds[i] = (float)(b[i] is double x ? x : 0);
                                var w = JsonParser.Arr(d, "w");
                                if (w != null) foreach (var o in w) if (o is string ws) info.wants.Add(ws);
                                var mw = JsonParser.Arr(d, "mw");
                                if (mw != null) foreach (var o in mw) if (o is string ms) info.meshWants.Add(ms);
                                var mm = JsonParser.Arr(d, "mm");
                                if (mm != null) foreach (var o in mm) if (o is string ms) info.meshMissing.Add(ms);
                                info.renderers = (int)JsonParser.Num(d, "r"); info.unreadable = (int)JsonParser.Num(d, "u"); info.ver = (int)JsonParser.Num(d, "v");
                                info.rockKind = (int)JsonParser.Num(d, "rk");
                                var pa = JsonParser.Arr(d, "pa");
                                if (pa != null) { info.partAreas = new int[pa.Count]; for (int i = 0; i < pa.Count; i++) info.partAreas[i] = pa[i] is double x ? (int)x : -1; }
                                var k = JsonParser.Arr(d, "k");
                                if (k != null && k.Count == 6) { info.canopy = new float[6]; for (int i = 0; i < 6; i++) info.canopy[i] = (float)(k[i] is double x ? x : 0); }
                                info.canopyTex = JsonParser.Str(d, "kt", null);
                                var kc = JsonParser.Arr(d, "kc");
                                if (kc != null && kc.Count == 3) { info.canopyColor = new float[3]; for (int i = 0; i < 3; i++) info.canopyColor[i] = (float)(kc[i] is double x ? x : 0); }
                                info.ct = JsonParser.Str(d, "ct", null); info.lt = JsonParser.Str(d, "lt", null); info.tp = (int)JsonParser.Num(d, "tp");
                                if (info.cat == Rig.Cat && info.name != null) rigNames[h] = info.name;
                                // only trust entries whose file still exists
                                if (!info.ok || File.Exists(Path.Combine(root, FileName(h)))) index[h] = info;
                            }
                    }
                }
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: models/index.json not readable: " + e.Message); }
            foreach (var i in index.Values) if (i.ok) Readable++; else if (i.renderers > 0) Unreadable++;
            // textures and meshes extracted since the last run: re-export the models that use them
            int again = RescanTextures() + RescanMeshes();
            WriteTexturesJson();
            Rebuild();
            ZLog.Log($"WebMap: model library at {root}: {index.Count} prefabs known" + (again > 0 ? $", {again} to re-export with newly extracted textures or meshes" : ""));
        }

        // Models with a locked mesh whose cache file now exists (the mesh extractor ran): re-export them.
        public static int RescanMeshes()
        {
            int again = 0;
            foreach (var i in index.Values)
            {
                if (i.meshMissing.Count == 0) continue;
                bool needs = false;
                foreach (var k in i.meshMissing) if (MeshCache.Exists(root, k)) { needs = true; break; }
                if (needs) { Request(i.hash, i.cat, force: true); again++; }
            }
            return again;
        }

        // mesh keys the models need that have no cache file yet
        public static List<string> MissingMeshes()
        {
            var seen = new HashSet<string>(); var list = new List<string>();
            foreach (var i in index.Values)
                foreach (var k in i.meshMissing)
                    if (seen.Add(k) && !MeshCache.Exists(root, k)) list.Add(k);
            return list;
        }
        public static int MeshesWanted { get; private set; }
        public static int MeshesPresent { get; private set; }

        // Models that still lack a texture one of their materials wants, when that file now exists
        // (the extractor ran while the server was up): queue them for re-export. Returns the count.
        // forget: false off the game thread, whose the exporter's texture cache is; the caller forgets then.
        public static int RescanTextures(bool forget = true)
        {
            if (!WebMapConfig.EXTRACT_TEXTURES) return 0;
            int again = 0;
            foreach (var i in index.Values)
            {
                // a rig part names its body paint rather than drawing it: any texture turned up since sends it again
                if (i.cat == Rig.Cat)
                {
                    if (Present(i.wants) > i.tp) { Request(i.hash, i.cat, force: true); again++; }
                    continue;
                }
                // a prefab with no geometry can never become textured: re-exporting it forever helps nobody
                if (i.wants.Count == 0 || !i.ok) continue;
                bool needs = false;
                foreach (var w in i.wants)
                {
                    string file = PrefabExporter.TextureFileName(w);
                    bool present = File.Exists(Path.Combine(root, file));
                    // a model is stale if a texture it wants exists but the model was exported without it
                    if (present && (!i.tex || (i.canopy != null && i.canopyTex == null && IsFoliageWant(w)))) needs = true;
                }
                if (needs) { Request(i.hash, i.cat, force: true); again++; }
            }
            if (again > 0 && forget) PrefabExporter.ForgetMissingTextures();
            return again;
        }
        // texture names the models reference that have no file yet
        public static List<string> MissingTextures()
        {
            var seen = new HashSet<string>(); var list = new List<string>();
            foreach (var i in index.Values)
                foreach (var w in i.wants)
                    if (seen.Add(w) && !File.Exists(Path.Combine(root, PrefabExporter.TextureFileName(w)))) list.Add(w);
            return list;
        }

        private static int Present(List<string> wants)
        {
            int n = 0;
            foreach (var w in wants) if (File.Exists(Path.Combine(root, PrefabExporter.TextureFileName(w)))) n++;
            return n;
        }

        private static bool IsFoliageWant(string texName)
        {
            string n = texName.ToLowerInvariant();
            return n.Contains("leaf") || n.Contains("leaves") || n.Contains("branch") || n.Contains("needle") || n.Contains("foliage") || n.Contains("bush");
        }

        // Every texture the exported models reference, and whether a file for it exists.
        // The extractor (and the manual tool) read this to know what to pull out of the game files.
        public static void WriteTexturesJson()
        {
            lock (saveGate)
            try
            {
                var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var i in index.Values) foreach (var w in i.wants) names[w] = names.TryGetValue(w, out int n) ? n + 1 : 1;
                var j = new JsonWriter(names.Count * 80 + 64);
                int present = 0;
                j.BeginObject().Key("textures").BeginArray();
                foreach (var kv in names)
                {
                    string file = PrefabExporter.TextureFileName(kv.Key);
                    bool p = File.Exists(Path.Combine(root, file));
                    if (p) present++;
                    j.BeginObject().Prop("name", kv.Key).Prop("file", file).Prop("present", p).Prop("models", kv.Value).End();
                }
                j.End().Prop("count", names.Count).Prop("present", present).Prop("enabled", WebMapConfig.EXTRACT_TEXTURES).End();
                File.WriteAllText(Path.Combine(root, "textures.json"), j.ToString());
                TexturesWanted = names.Count; TexturesPresent = present;
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: could not write models/textures.json: " + e.Message); }
        }
        public static int TexturesWanted { get; private set; }
        public static int TexturesPresent { get; private set; }

        public static string FileName(int hash) => unchecked((uint)hash).ToString("x8") + ".glb";

        // Game thread (the player snapshot): a part of a live player's rig, exported once like any prefab.
        public static void RequestRig(string name)
        {
            int h = name.GetStableHashCode();
            if (index.ContainsKey(h)) return;
            rigNames[h] = name;
            Request(h, Rig.Cat);
        }

        // Main thread (from the sweep). Queues an export if this prefab is new.
        public static void Request(int hash, string cat, bool force = false)
        {
            if (!force && index.ContainsKey(hash)) return;
            lock (queue)
            {
                if (queued.Contains(hash)) return;
                queued.Add(hash);
                queue.Enqueue(hash);
                catOf[hash] = cat;
            }
        }

        // Main thread: a few prefabs per frame, within a time budget.
        public static IEnumerator Pump()
        {
            var sw = new System.Diagnostics.Stopwatch();
            IEnumerator job = null; int jobHash = 0;
            while (true)
            {
                if (job == null && QueueLength == 0)
                {
                    if (indexDirty && Saved()) { indexDirty = false; SaveSoon(textures: true); }
                    // a sweep queues prefabs as it meets them, so a run ends after a quiet spell, not at the first empty queue
                    if (run != null && ++quiet >= 15)
                    {
                        string slowest = index.TryGetValue(runStepHash, out var si) && si.name != null ? si.name : "#" + runStepHash;
                        ZLog.Log($"WebMap: model export done: {Exported - runStart} prefabs in {runBusy:0}s; "
                               + $"{Readable} with a model, {Unreadable} waiting on locked meshes, library {libraryMb:0.0} MB; "
                               + $"longest frame {runFrameMs:0.0} ms, slowest step {runStepMs:0.0} ms ({slowest})");
                        run = null;
                    }
                    // once a minute (and a few seconds after the first export): fetch missing textures from the
                    // game files on a background thread, then re-export the models that use them
                    int tick = idleTicks++;
                    if (extractDone) { extractDone = false; int n = RescanTextures(); WriteTexturesJson(); Rebuild(); ZLog.Log($"WebMap: {n} models to re-export with newly extracted textures"); }
                    else if (meshDone) { meshDone = false; int n = RescanMeshes(); Rebuild(); ZLog.Log($"WebMap: {n} models to re-export with newly extracted meshes"); }
                    else if (tick == 5 || tick % 60 == 59)
                    {
                        // the library's files looked at on the pool: a File.Exists for every texture every
                        // model wants was 12-19 ms on the game thread each minute
                        int textures = 0, again = 0; List<string> texMissing = null, meshMissing = null;
                        var look = Task.Run(() =>
                        {
                            textures = RescanTextures(forget: false); again = textures + RescanMeshes();
                            if (again == 0 && WebMapConfig.EXTRACT_TEXTURES) texMissing = MissingTextures();
                            if (WebMapConfig.EXTRACT_MESHES) meshMissing = MissingMeshes();
                        });
                        while (!look.IsCompleted) yield return null;
                        if (look.Exception != null) ZLog.LogWarning("WebMap: model library not checked: " + look.Exception.GetBaseException().Message);
                        else
                        {
                            if (textures > 0) PrefabExporter.ForgetMissingTextures();
                            if (again > 0) ZLog.Log($"WebMap: {again} models to re-export with newly extracted textures or meshes");
                            else if (WebMapConfig.EXTRACT_TEXTURES && !TextureExtractor.Running && !MeshExtractor.Running && texMissing.Count > 0)
                                TextureExtractor.Start(texMissing, root, Math.Max(64, WebMapConfig.TEXTURE_MAX_SIZE), () => extractDone = true);
                            // locked meshes next, once textures are settled
                            if (WebMapConfig.EXTRACT_MESHES && !TextureExtractor.Running && !MeshExtractor.Running && meshMissing.Count > 0)
                                MeshExtractor.Start(meshMissing, root, () => meshDone = true);
                        }
                    }
                    yield return new WaitForSeconds(1f);
                    continue;
                }
                quiet = 0;
                if (run == null)
                {
                    run = System.Diagnostics.Stopwatch.StartNew(); runStart = Exported; runFrameMs = runStepMs = 0;
                    ZLog.Log($"WebMap: exporting models into {root} as the sweep finds them, {Math.Max(2, WebMapConfig.EXPORT_MS_PER_FRAME)} ms a frame");
                }
                sw.Restart();
                int budgetMs = Math.Max(2, WebMapConfig.EXPORT_MS_PER_FRAME), before = Exported;
                // one export at a time, stepped until the budget is spent; a big one carries on next frame
                while (sw.ElapsedMilliseconds < budgetMs)
                {
                    if (job == null)
                    {
                        string cat;
                        lock (queue)
                        {
                            if (queue.Count == 0) break;
                            jobHash = queue.Dequeue();
                            catOf.TryGetValue(jobHash, out cat); catOf.Remove(jobHash);
                        }
                        job = cat == Rig.Cat ? ExportRig(jobHash) : ExportOne(jobHash, cat ?? "other");
                    }
                    bool more;
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    try { more = job.MoveNext(); }
                    catch (Exception e) { ZLog.LogWarning($"WebMap: model export of #{jobHash} failed: {e.Message}"); more = false; }
                    double step = sw.Elapsed.TotalMilliseconds - t0;
                    if (step > runStepMs) { runStepMs = step; runStepHash = jobHash; }
                    if (more) { if (job.Current == PrefabExporter.Wait) break; continue; }
                    job = null;
                    runBusy = run.Elapsed.TotalSeconds;
                    lock (queue) queued.Remove(jobHash);
                }
                if (Exported != before && Exported % 50 == 0 && indexDirty && Saved()) SaveSoon(textures: false);
                runFrameMs = Math.Max(runFrameMs, sw.Elapsed.TotalMilliseconds);
                yield return null;
            }
        }

        // The index, textures.json and /prefabs, written on the pool: 17-32 ms of JSON and files
        // on the game thread at the end of every export run.
        private static Task saving;
        private static readonly object saveGate = new object();
        private static double libraryMb;
        private static void SaveSoon(bool textures) =>
            saving = Task.Run(() => { SaveIndex(); if (textures) WriteTexturesJson(); Rebuild(); libraryMb = LibraryMB(); });
        // game thread: whether the last save is done, saying so once if it failed
        private static bool Saved()
        {
            if (saving == null) return true;
            if (!saving.IsCompleted) return false;
            if (saving.Exception != null) ZLog.LogWarning("WebMap: model library index not saved: " + saving.Exception.GetBaseException().Message);
            saving = null;
            return true;
        }

        // A glTF bigger than this is written on the pool: a 3 MB rock took 43 ms on the game thread,
        // where one of 250 KB takes under 2.
        private const long WriteOnPoolBytes = 256 * 1024;

        // Steps like PrefabExporter.Export, yielding Wait while a location's prefab loads and while a
        // location's or a big model's glTF is written on the pool: a camp is too big for the game thread's budget.
        private static IEnumerator ExportOne(int hash, string cat)
        {
            var info = new Info { hash = hash, cat = cat };
            GameObject go = null;
            try { go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null; } catch { }
            ZoneSystem.ZoneLocation loc = null;
            bool held = false;
            if (go == null && WebMapConfig.EXPORT_MODELS)
            {
                try { loc = ZoneSystem.instance != null ? ZoneSystem.instance.GetLocation(hash) : null; if (loc != null && loc.m_prefab.IsValid) { loc.m_prefab.LoadAsync(); held = true; } }
                catch (Exception e) { ZLog.LogWarning($"WebMap: location #{hash} not loaded: {e.Message}"); }
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (held && !Loaded(loc) && waited.ElapsedMilliseconds < 60000) yield return PrefabExporter.Wait;
                try { if (held && Loaded(loc)) go = loc.m_prefab.Asset; } catch { }
            }
            info.name = go != null ? go.name : loc != null && !string.IsNullOrEmpty(loc.m_prefabName) ? loc.m_prefabName : ("#" + hash);
            try
            {
                if (go != null && WebMapConfig.EXPORT_MODELS)
                {
                    var r = new PrefabExporter.Result { category = cat };
                    bool failed = false;
                    var steps = PrefabExporter.Export(go, root, FallbackColor(hash, go.name, cat), r);
                    while (true)
                    {
                        bool more;
                        try { more = steps.MoveNext(); }
                        catch (Exception e) { ZLog.LogWarning($"WebMap: model export of {info.name} failed: {e.Message}"); failed = true; break; }
                        if (!more) break;
                        yield return steps.Current;
                    }
                    byte[] glb = null; int ver = 0;
                    string file = Path.Combine(root, FileName(hash));
                    Action write = () => { glb = r.Write(); if (glb != null) { File.WriteAllBytes(file, glb); ver = Fnv.Of(glb); } };
                    Exception wrote = null;
                    if (!failed && (r.location || r.Bytes > WriteOnPoolBytes))
                    {
                        var task = Task.Run(write);
                        while (!task.IsCompleted) yield return PrefabExporter.Wait;
                        wrote = task.Exception?.GetBaseException();
                    }
                    else if (!failed) try { write(); } catch (Exception e) { wrote = e; }
                    if (wrote != null) { ZLog.LogWarning($"WebMap: model export of {info.name} failed: {wrote.Message}"); failed = true; }
                    if (!failed)
                    {
                        info.renderers = r.renderers; info.unreadable = r.unreadable; info.wants = r.wants; info.meshWants = r.meshWants; info.meshMissing = r.meshMissing;
                        if (r.rockKind > 0) { info.rockKind = r.rockKind; info.partAreas = r.partAreas.ToArray(); }
                        if (r.hasCanopy) { info.canopy = r.canopy; info.canopyTex = r.canopyTexture; info.canopyColor = r.canopyColor; }
                        if (glb != null)
                        {
                            info.ok = true; info.tris = r.triangles; info.bounds = r.bounds; info.tex = r.textured; info.ver = ver;
                            Readable++;
                        }
                        else if (r.renderers > 0) Unreadable++;
                        if (!info.ok) info.bounds = r.shape ?? RendererBounds(go);
                    }
                }
            }
            finally { if (held) try { loc.m_prefab.Release(); } catch { } }
            Record(hash, info);
        }

        // A live player's rig part: RigExporter's glTF, its body paint named beside it. The standing pose,
        // a clone of the Player's rig played through once a start, takes a frame of its own; the glTF and
        // its file are written on the pool: together they were a 30 ms frame.
        private static IEnumerator ExportRig(int hash)
        {
            rigNames.TryGetValue(hash, out string name);
            var info = new Info { hash = hash, cat = Rig.Cat, name = name ?? ("#" + hash) };
            if (name != null && WebMapConfig.EXPORT_MODELS)
            {
                if (!RigExporter.Ready) { RigExporter.Pose(); yield return PrefabExporter.Wait; }
                var r = new PrefabExporter.Result { category = Rig.Cat };
                bool made = false;
                try { made = RigExporter.Export(name, root, r); }
                catch (Exception e) { ZLog.LogWarning($"WebMap: model export of {name} failed: {e.Message}"); }
                if (made)
                {
                    byte[] glb = null; string file = Path.Combine(root, FileName(hash));
                    var task = Task.Run(() => { glb = r.Write(); if (glb != null) File.WriteAllBytes(file, glb); });
                    while (!task.IsCompleted) yield return PrefabExporter.Wait;
                    if (task.Exception != null) ZLog.LogWarning($"WebMap: model export of {name} failed: {task.Exception.GetBaseException().Message}");
                    else
                    {
                        if (glb != null)
                        {
                            info.ok = true; info.tris = r.triangles; info.bounds = r.bounds; info.tex = r.textured; info.ver = Fnv.Of(glb);
                            Readable++;
                        }
                        else if (r.renderers > 0) Unreadable++;
                        info.renderers = r.renderers; info.unreadable = r.unreadable; info.wants = r.wants; info.meshWants = r.meshWants; info.meshMissing = r.meshMissing;
                        info.ct = r.overlayChest; info.lt = r.overlayLegs; info.tp = Present(r.wants);
                    }
                }
            }
            Record(hash, info);
        }

        private static void Record(int hash, Info info)
        {
            // a re-export was counted once already
            if (index.TryGetValue(hash, out var old)) { if (old.ok) Readable--; else if (old.renderers > 0) Unreadable--; }
            index[hash] = info;
            Exported++;
            indexDirty = true;
            if (Exported == 1 || Exported % 100 == 0)
                ZLog.Log($"WebMap: models exported {Exported} (readable meshes {Readable}, unreadable {Unreadable}, queued {QueueLength})");
        }

        private static bool Loaded(ZoneSystem.ZoneLocation loc) => loc.m_prefab.IsLoaded;

        // Prefab-space bounds from the renderers (works even when meshes are unreadable), in the viewer's frame (z negated).
        private static float[] RendererBounds(GameObject go)
        {
            var b = new float[6];
            bool any = false;
            Bounds acc = new Bounds();
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                Bounds rb;
                try { rb = r.bounds; } catch { continue; }
                if (!any) { acc = rb; any = true; } else acc.Encapsulate(rb);
            }
            if (!any) { b[0] = b[1] = b[2] = -0.5f; b[3] = b[4] = b[5] = 0.5f; return b; }
            // renderer bounds are in world space; a prefab asset sits at the origin, so this is prefab space
            Vector3 mn = acc.min, mx = acc.max;
            b[0] = mn.x; b[1] = mn.y; b[2] = -mx.z; b[3] = mx.x; b[4] = mx.y; b[5] = -mn.z;
            return b;
        }

        // A colour for a material with neither a texture nor a tint: the structures
        // layer's material guess for a piece, a plausible green or grey for the rest.
        private static Color32 FallbackColor(int hash, string name, string cat)
        {
            string n = (name ?? "").ToLowerInvariant();
            switch (cat)
            {
                case "piece": return StructureMap.MaterialOf(hash);
                case "tree": return n.Contains("pine") || n.Contains("fir") ? new Color32(44, 82, 52, 255) : new Color32(86, 138, 58, 255);
                case "bush": return new Color32(70, 110, 50, 255);
                case "rock": return n.Contains("copper") || n.Contains("tin") || n.Contains("silver") ? new Color32(134, 104, 74, 255) : new Color32(118, 118, 112, 255);
                default: return new Color32(150, 140, 130, 255);
            }
        }

        // what the library holds on disk, models, textures and extracted meshes together
        public static double LibraryMB()
        {
            long n = 0;
            try { foreach (var f in new DirectoryInfo(root).GetFiles("*", SearchOption.AllDirectories)) n += f.Length; } catch { }
            return n / 1048576.0;
        }

        private static void SaveIndex()
        {
            lock (saveGate)
            try
            {
                var j = new JsonWriter(index.Count * 120 + 64);
                j.BeginObject().Prop("format", FORMAT).Key("prefabs").BeginObject();
                foreach (var kv in index) WriteInfo(j, kv.Value, full: true);
                j.End().End();
                string path = Path.Combine(root, "index.json"), tmp = path + ".tmp";
                File.WriteAllText(tmp, j.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: could not save models/index.json: " + e.Message); }
        }

        private static void WriteInfo(JsonWriter j, Info i, bool full)
        {
            j.Key(i.hash.ToString(CultureInfo.InvariantCulture)).BeginObject();
            j.Prop("n", i.name).Prop("c", i.cat).Prop("m", i.ok).Prop("t", i.tris).Prop("x", i.tex);
            if (i.ok) j.Prop("v", i.ver);
            if (i.rockKind > 0 && i.partAreas != null)
            {
                j.Prop("rk", i.rockKind);
                j.Key("pa").BeginArray(); foreach (int a in i.partAreas) j.Value(a); j.End();
            }
            if (full)
            {
                j.Prop("r", i.renderers).Prop("u", i.unreadable);
                j.Key("w").BeginArray(); foreach (var w in i.wants) j.Value(w); j.End();
                if (i.meshWants.Count > 0) { j.Key("mw").BeginArray(); foreach (var w in i.meshWants) j.Value(w); j.End(); }
                if (i.meshMissing.Count > 0) { j.Key("mm").BeginArray(); foreach (var w in i.meshMissing) j.Value(w); j.End(); }
            }
            else if (i.unreadable > 0) j.Prop("u", i.unreadable);   // the page can say "part of this model is missing"
            j.Key("b").BeginArray(); foreach (float v in i.bounds) j.Value(v, 3); j.End();
            if (i.canopy != null)
            {
                j.Key("k").BeginArray(); foreach (float v in i.canopy) j.Value(v, 2); j.End();
                if (i.canopyTex != null) j.Prop("kt", i.canopyTex);
                if (i.canopyColor != null) { j.Key("kc").BeginArray(); foreach (float v in i.canopyColor) j.Value(v, 3); j.End(); }
            }
            if (i.ct != null) j.Prop("ct", i.ct);
            if (i.lt != null) j.Prop("lt", i.lt);
            if (full && i.cat == Rig.Cat) j.Prop("tp", i.tp);
            j.End();
        }

        private static void Rebuild()
        {
            lock (saveGate) RebuildLocked();
        }
        private static void RebuildLocked()
        {
            int next = rev + 1;
            try
            {
                var seen = new HashSet<string>(); int present = 0;
                foreach (var i in index.Values) foreach (var k in i.meshWants) if (seen.Add(k) && MeshCache.Exists(root, k)) present++;
                MeshesWanted = seen.Count; MeshesPresent = present;
            }
            catch { }
            var j = new JsonWriter(index.Count * 100 + 64);
            j.BeginObject().Prop("rev", next).Prop("format", FORMAT).Prop("exported", Exported).Prop("readable", Readable).Prop("unreadable", Unreadable).Prop("queued", QueueLength)
             .Prop("texturesWanted", TexturesWanted).Prop("texturesPresent", TexturesPresent).Prop("extracting", TextureExtractor.Running).Prop("extractReport", TextureExtractor.LastReport)
             .Prop("meshesWanted", MeshesWanted).Prop("meshesPresent", MeshesPresent).Prop("extractingMeshes", MeshExtractor.Running).Prop("meshReport", MeshExtractor.LastReport);
            j.Key("prefabs").BeginObject();
            foreach (var kv in index) WriteInfo(j, kv.Value, full: false);
            j.End().End();
            prefabsJson = j.ToString();
            rev = next;                        // after the JSON, so a reader never pairs a new rev with the old list
        }
    }
}
