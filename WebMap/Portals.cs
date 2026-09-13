using System.Collections.Generic;
using System.Text;
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
        private static volatile string json = "{\"portals\":[],\"count\":0}";

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
                    $"{{\"id\":\"{e.id}\",\"name\":\"{name}\",\"to\":\"{e.to}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#}}}"));
            }
            sb.Append("],\"count\":").Append(n).Append("}");
            json = sb.ToString();
        }

        public static string GetJson() => json;
    }
}
