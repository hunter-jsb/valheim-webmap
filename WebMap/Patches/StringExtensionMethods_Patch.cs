using HarmonyLib;
using System.Collections.Generic;

namespace WebMap.Patches
{

    [HarmonyPatch]
    internal class StringExtensionMethods_Patch
    {
        internal static Dictionary<int, string> stablehashNames = new Dictionary<int, string>();
        internal static Dictionary<int, string> stablehashNamesAnim = new Dictionary<int, string>();
        internal static Dictionary<string, int> stablehashLookup = new Dictionary<string, int>();
        internal static Dictionary<string, int> stablehashLookupAnim = new Dictionary<string, int>();

        // Record hash->name for debug output only. This MUST NOT replace the game's
        // own GetStableHashCode: clients compute routed-RPC method hashes with the
        // vanilla implementation, so a reimplementation here desynchronises the
        // server's comparisons (chat and !pin commands silently stop matching).
        [HarmonyPatch(typeof(StringExtensionMethods), "GetStableHashCode")]
        [HarmonyPostfix]
        public static void GetStableHashCode(string str, int __result)
        {
            if (str == null) return;
            if (!stablehashNames.ContainsKey(__result))
            {
                stablehashNames[__result] = str;
            }
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