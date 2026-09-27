using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WebMap
{
    // Portals, with their tag and what they actually connect to.
    //
    // The pairing is not guessed from matching tags: Valheim stores the linked
    // portal's ZDOID on the ZDO itself, so two portals sharing a name are not
    // reported as a pair unless the game has actually connected them -- and a
    // connected pair with no name still reads correctly.
    //
    // Fed from the structures sweep, and still painted into that layer: unlike a
    // boat, a portal is part of a build.
    internal static class Portals
    {
        private struct Entry { public string id, name, to; public float x, z; public bool explored; }

        private static readonly Dictionary<int, bool> isPortal = new Dictionary<int, bool>();
        private static readonly List<Entry> found = new List<Entry>();
        private static volatile string json = "{\"portals\":[],\"count\":0";
        public static volatile float[] Positions = new float[0];   // x, z pairs of the portals in walked ground

        // Where each portal last led: a gate retagged and standing unlinked still
        // belongs, on the atlas, to the hub it was dialled from. Kept across restarts.
        private struct Last { public string to; public float x, z; }
        private static readonly Dictionary<string, Last> last = new Dictionary<string, Last>();
        private const string LastFile = "portals.tsv";
        private static string dir;
        private static bool saving;

        public static void Load(string worldDataPath)
        {
            dir = worldDataPath;
            last.Clear();
            try
            {
                string path = Path.Combine(dir, LastFile);
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                    {
                        var f = line.Split('\t');
                        if (f.Length < 4) continue;
                        if (float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                            && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                            last[f[0]] = new Last { to = f[1], x = x, z = z };
                    }
            }
            catch (Exception e) { ZLog.LogWarning("WebMap: portal history not loaded: " + e.Message); }
        }

        // By component, so a portal added in a later patch counts without this
        // having to learn its prefab name.
        public static bool IsPortal(int prefabHash)
        {
            if (isPortal.TryGetValue(prefabHash, out bool cached)) return cached;
            bool v = false;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                v = go != null && go.GetComponent<TeleportWorld>() != null;
            }
            catch { }
            isPortal[prefabHash] = v;
            return v;
        }

        public static void Begin() => found.Clear();

        public static void Observe(ZDO zdo, Vector3 pos)
        {
            string name = "", to = "";
            try { name = zdo.GetString(ZDOVars.s_tag, "") ?? ""; } catch { }
            try
            {
                var target = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (target != ZDOID.None) to = target.ToString();
            }
            catch { }
            found.Add(new Entry { id = zdo.m_uid.ToString(), name = name, to = to, x = pos.x, z = pos.z,
                                  explored = MapFog.Explored(pos.x, pos.z) });   // fog is a texture: game thread only
        }

        public static void Finish()
        {
            var by = new Dictionary<string, Entry>();
            foreach (var e in found) by[e.id] = e;
            bool changed = false;
            foreach (var e in found)
            {
                if (e.to.Length == 0 || !by.TryGetValue(e.to, out var twin)) continue;
                if (last.TryGetValue(e.id, out var was) && was.to == e.to) continue;
                last[e.id] = new Last { to = e.to, x = twin.x, z = twin.z };
                changed = true;
            }
            var sb = new StringBuilder();
            sb.Append("{\"portals\":[");
            int n = 0;
            foreach (var e in found)
            {
                if (!e.explored) continue;
                if (n > 0) sb.Append(",");
                n++;
                string name = e.name.Replace("\\", "").Replace("\"", "");
                sb.Append(System.FormattableString.Invariant(
                    $"{{\"id\":\"{e.id}\",\"name\":\"{name}\",\"to\":\"{e.to}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#}"));
                // unlinked, but it once led somewhere: the spot its twin stood at
                if ((e.to.Length == 0 || !by.ContainsKey(e.to)) && last.TryGetValue(e.id, out var l))
                    sb.Append(System.FormattableString.Invariant($",\"last\":{{\"x\":{l.x:0.#},\"z\":{l.z:0.#}}}"));
                sb.Append('}');
            }
            sb.Append("],\"count\":").Append(n);
            json = sb.ToString();       // closed at read time, once the hub names are added
            if (changed && !saving && dir != null)
            {
                saving = true;
                var lines = new StringBuilder();
                foreach (var kv in last)
                    lines.Append(kv.Key).Append('\t').Append(kv.Value.to).Append('\t')
                         .Append(kv.Value.x.ToString("0.#", CultureInfo.InvariantCulture)).Append('\t')
                         .Append(kv.Value.z.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
                string text = lines.ToString();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string path = Path.Combine(dir, LastFile), tmp = path + ".new";
                        File.WriteAllText(tmp, text);
                        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                    }
                    catch (Exception ex) { ZLog.LogWarning("WebMap: portal history not saved: " + ex.Message); }
                    finally { saving = false; }
                });
            }
            var pos = new List<float>();
            foreach (var e in found) if (e.explored) { pos.Add(e.x); pos.Add(e.z); }
            Positions = pos.ToArray();
        }

        // the hub names ride along as they are now, not as they were at the last sweep
        public static string GetJson() => json + ",\"named\":" + Features.HubNamesJson() + "}";

        // ---------- for the tests (WebMap.Tests) ----------
        // a save still in flight lands in the last case's directory, not the next one's
        internal static void ResetForTests(string worldDataPath)
        {
            System.Threading.SpinWait.SpinUntil(() => !saving, 5000);
            Load(worldDataPath); found.Clear();
        }
        // a portal as the sweep would see it, without a ZDO or the fog
        internal static void ObserveForTests(string id, string name, string to, float x, float z, bool explored = true) =>
            found.Add(new Entry { id = id, name = name, to = to, x = x, z = z, explored = explored });
    }
}
