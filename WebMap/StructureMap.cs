using System.Collections;
using System.Collections.Generic;
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
    // A sweep walks every ZDO, so it is deliberately spread across frames and run
    // infrequently: buildings change slowly and this shares a CPU with the game.
    internal static class StructureMap
    {
        private const float SweepInterval = 120f;
        private const int ZdosPerFrame = 3000;

        private static Texture2D texture;
        private static byte[] png;
        private static bool pngStale = true;
        private static bool sweeping;

        // Set from the HTTP thread; consumed on the main thread. Unity objects
        // must not be touched off-thread, so a request can only ask, never scan.
        public static volatile bool RefreshRequested;

        public static int LastCount { get; private set; }
        public static int LastScanned { get; private set; }

        private static void Init()
        {
            if (texture != null) return;
            int size = WebMapConfig.TEXTURE_SIZE;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(new Color32[size * size]);   // zeroed = fully transparent
            texture.Apply();
        }

        public static IEnumerator Loop()
        {
            Init();
            yield return new WaitForSeconds(30f);            // let the world finish loading
            while (true)
            {
                yield return Sweep();
                float waited = 0f;
                while (waited < SweepInterval && !RefreshRequested)
                {
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                RefreshRequested = false;
            }
        }

        private static IEnumerator Sweep()
        {
            if (sweeping) yield break;
            sweeping = true;
            Init();

            int size = WebMapConfig.TEXTURE_SIZE;
            int half = size / 2;
            // Rebuilt from scratch each sweep so demolished builds actually vanish.
            var buf = new Color32[size * size];
            // Warm orange: reads against both meadow green and mountain grey,
            // and is not a colour the terrain render itself produces.
            var mark = new Color32(255, 146, 48, 255);

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
                    if (creator != 0L)
                    {
                        Vector3 p = zdo.GetPosition();
                        int x = Mathf.RoundToInt(p.x / WebMapConfig.PIXEL_SIZE + half);
                        int y = Mathf.RoundToInt(p.z / WebMapConfig.PIXEL_SIZE + half);
                        if (x >= 0 && y >= 0 && x < size && y < size)
                        {
                            buf[y * size + x] = mark;
                            found++;
                        }
                    }
                }
                if (seen % ZdosPerFrame == 0) yield return null;   // never stall a frame
            }

            texture.SetPixels32(buf);
            texture.Apply();
            LastCount = found;
            LastScanned = seen;
            pngStale = true;
            sweeping = false;
            ZLog.Log($"WebMap: structures sweep -> {found} placed pieces from {seen} zdos");
        }

        public static byte[] GetPng()
        {
            if (texture == null) return new byte[0];
            if (pngStale || png == null)
            {
                png = ImageConv.EncodeToPNG(texture);
                pngStale = false;
            }
            return png;
        }
    }
}
