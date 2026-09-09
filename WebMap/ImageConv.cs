using System;
using System.Reflection;
using UnityEngine;

namespace WebMap
{
    // Calls UnityEngine.ImageConversion via reflection so the mod need not reference
    // UnityEngine.ImageConversionModule at compile time. That module pulls in netstandard
    // 2.1 / ReadOnlySpan overloads which don't resolve when building against net48.
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

        public static byte[] EncodeToPNG(Texture2D tex) => (byte[])_enc.Invoke(null, new object[] { tex });
    }
}
