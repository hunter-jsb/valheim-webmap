using System;
using System.Reflection;
using UnityEngine;

namespace WebMap
{
    // Calls UnityEngine.ImageConversion via reflection so the mod need not reference
    // UnityEngine.ImageConversionModule at compile time. That module pulls in netstandard
    // 2.1 / ReadOnlySpan overloads which don't resolve when building against net48.
    // Content revision of a layer: FNV-1a over the bytes. Two sweeps that paint the
    // same picture get the same number, so a viewer that already has it skips it.
    internal static class Fnv
    {
        public static int Of(byte[] b)
        {
            uint h = 2166136261;
            if (b != null) for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            return unchecked((int)(h & 0x7fffffff));
        }
        public static int Of(string s)
        {
            uint h = 2166136261;
            if (s != null) for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h ^= (byte)(s[i] >> 8); h *= 16777619; }
            return unchecked((int)(h & 0x7fffffff));
        }
    }

    internal static class ImageConv
    {
        private static readonly Type T =
            Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");

        private static readonly MethodInfo _load =
            T?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) })
            ?? T?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });

        private static readonly MethodInfo _enc =
            T?.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });

        public static bool LoadImage(Texture2D tex, byte[] data)
        {
            var args = _load.GetParameters().Length == 3
                ? new object[] { tex, data, false }
                : new object[] { tex, data };
            return (bool)_load.Invoke(null, args);
        }

        private static readonly MethodInfo _jpg =
            T?.GetMethod("EncodeToJPG", new[] { typeof(Texture2D), typeof(int) });

        public static byte[] EncodeToPNG(Texture2D tex) => (byte[])_enc.Invoke(null, new object[] { tex });

        private static readonly MethodInfo _encArr =
            T?.GetMethod("EncodeArrayToPNG", new[] { typeof(Array), typeof(UnityEngine.Experimental.Rendering.GraphicsFormat),
                                                    typeof(uint), typeof(uint), typeof(uint) });

        // Thread-safe in Unity's binding (FreeFunction ..., true), which EncodeToPNG
        // on a Texture2D is not: a sweep encodes its overlays on a pool thread with
        // this. RGBA bytes laid out like a texture, bottom row first.
        public static byte[] EncodeRgbaToPNG(byte[] rgba, int width, int height) =>
            (byte[])_encArr.Invoke(null, new object[] { rgba, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                                                        (uint)width, (uint)height, 0u });

        public static byte[] EncodeToJPG(Texture2D tex, int quality) =>
            (byte[])_jpg.Invoke(null, new object[] { tex, quality });
    }
}
