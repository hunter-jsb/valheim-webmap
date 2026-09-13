using UnityEngine;

namespace WebMap
{
    // Whether anyone has been somewhere yet.
    //
    // The structures layer is drawn under the fog mask by the page, so builds in
    // unexplored land are hidden for free. Markers cannot be masked that way, so
    // anything drawn as a marker is filtered through this instead -- not reported
    // at all, rather than merely not drawn.
    internal static class MapFog
    {
        public static bool Explored(float x, float z)
        {
            try
            {
                var fog = WebMap.mapDataServer != null ? WebMap.mapDataServer.fogRgba : null;
                if (fog == null) return true;          // before the fog loads, hide nothing
                int size = WebMapConfig.TEXTURE_SIZE, half = size / 2;
                int px = Mathf.RoundToInt(x / WebMapConfig.PIXEL_SIZE + half);
                int py = Mathf.RoundToInt(z / WebMapConfig.PIXEL_SIZE + half);
                if (px < 0 || py < 0 || px >= size || py >= size) return false;
                return fog[(py * size + px) * 4] > 127;   // white is explored
            }
            catch { return true; }
        }
    }
}
