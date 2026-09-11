using HarmonyLib;
using System.Collections.Concurrent;

namespace WebMap.Patches
{

    [HarmonyPatch]
    internal class StringExtensionMethods_Patch
    {
        // Concurrent, not plain dictionaries: these postfixes sit on GetStableHashCode
        // and ZSyncAnimation.GetHash, both of which are called from whatever thread
        // happens to be hashing. A plain Dictionary written from two threads throws or
        // corrupts, and on a hot path that reads as a fast-repeating console error.
        internal static ConcurrentDictionary<int, string> stablehashNames = new ConcurrentDictionary<int, string>();
        internal static ConcurrentDictionary<int, string> stablehashNamesAnim = new ConcurrentDictionary<int, string>();
        internal static ConcurrentDictionary<string, int> stablehashLookup = new ConcurrentDictionary<string, int>();
        internal static ConcurrentDictionary<string, int> stablehashLookupAnim = new ConcurrentDictionary<string, int>();

        // Record hash->name for debug output only. This MUST NOT replace the game's
        // own GetStableHashCode: clients compute routed-RPC method hashes with the
        // vanilla implementation, so a reimplementation here desynchronises the
        // server's comparisons (chat and !pin commands silently stop matching).
        [HarmonyPatch(typeof(StringExtensionMethods), "GetStableHashCode")]
        [HarmonyPostfix]
        public static void GetStableHashCode(string str, int __result)
        {
            if (str == null) return;
            stablehashNames.TryAdd(__result, str);
            stablehashLookup[str] = __result;
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "GetHash")]
        [HarmonyPrefix]
        public static void GetAnimHash(string name, ref int __result, ref bool __runOriginal)
        {
            if (stablehashLookupAnim.TryGetValue(name, out __result))
            {
                __runOriginal = false;
            }
            else
            {
                __runOriginal = true;
            }
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "GetHash")]
        [HarmonyPostfix]
        public static void AddAnimHash(string name, ref int __result, ref bool __runOriginal)
        {
            if (__runOriginal)
            {
                stablehashNamesAnim[__result] = name;
                stablehashLookupAnim[name] = __result;

                if (WebMapConfig.DEBUG)
                {
                    ZLog.Log($"First GetAnimHash: {name} -> {__result}");
                }
            }
        }

        public static string GetStableHashName(int code)
        {
            string str;
            if (stablehashNames.TryGetValue(code, out str))
            {
                return str;
            }

            if (stablehashNamesAnim.TryGetValue(code - 438569, out str))
            {
                return str + $" (A)";
            }

            return code.ToString();
        }
    }
}