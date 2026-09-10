using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Splatform;
using Steamworks;
using UnityEngine;

// Authored by Jere Kuusela <https://github.com/JereKuusela>
// https://github.com/JereKuusela/valheim-expand_world_prefabs/blob/main/ExpandWorldPrefabs/service/ServerClient.cs (public domain)

namespace WebMap
{
    public class ServerClient
    {
        // Built once. On a crossplay/PlayFab server the Steam identity is unavailable,
        // and this used to throw on every SendPlayerList tick; now a failure is recorded
        // once and the injection is simply skipped.
        public static ZNet.PlayerInfo? Client
        {
            get
            {
                if (client != null || clientFailed) return client;
                try { client = CreatePlayerInfo(); }
                catch (Exception e)
                {
                    clientFailed = true;
                    ZLog.LogWarning("WebMap: server chat client unavailable, !pin disabled: " + e.Message);
                }
                return client;
            }
        }
        private static ZNet.PlayerInfo? client;
        private static bool clientFailed;

        // Server client is only sent to clients, so this is needed for the server to recognize it.
        // This is what makes chat reach the server at all: without a server entry in the
        // player list, clients never route Say to us and !pin can never fire.
        // TEMPORARILY DISABLED: suspected of blocking joins on 1.0 crossplay (2026-09-09).
        // [HarmonyPatch(typeof(ZNet), nameof(ZNet.TryGetPlayerByPlatformUserID))]
        public class RecognizeServerClient
        {
            static bool Postfix(bool result, PlatformUserID platformUserID, ref ZNet.PlayerInfo playerInfo)
            {
                if (result) return result;
                var c = Client;
                if (c == null || platformUserID != c.Value.m_userInfo.m_id) return result;

                playerInfo = c.Value;
                return true;
            }
        }

        // [HarmonyPatch(typeof(ZNet), nameof(ZNet.SendPlayerList))]
        public class AddExtraPlayer
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return new CodeMatcher(instructions).End().MatchStartBackwards(new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(ZNet), nameof(ZNet.m_players))))
                  .Advance(-1)
                  .InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0))
                  .InsertAndAdvance(new CodeInstruction(OpCodes.Ldloc_0))
                  .InsertAndAdvance(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AddExtraPlayer), nameof(AddServer))))
                  .InstructionEnumeration();
            }

            static void AddServer(ZNet net, ZPackage pkg)
            {
                if (Client == null) return;          // nothing to inject; leave the list alone
                var prev = pkg.GetPos();
                try
                {
                    // This is needed in case multiple mods are adding extra players.
                    pkg.SetPos(0);
                    if (IsExtraPlayerAdded(net, pkg.ReadInt()))
                    {
                        pkg.SetPos(prev);
                    }
                    else
                    {
                        pkg.SetPos(0);
                        pkg.Write(net.m_players.Count + 1);
                        Write(pkg);
                    }
                }
                catch (Exception e)
                {
                    // Never let this break SendPlayerList: restore and stop trying.
                    try { pkg.SetPos(prev); } catch { }
                    clientFailed = true; client = null;
                    ZLog.LogWarning("WebMap: disabling server chat client after error: " + e.Message);
                }
            }

            static bool IsExtraPlayerAdded(ZNet net, int count) => count >= net.m_players.Count + 1;
        }

        private static ZNet.PlayerInfo CreatePlayerInfo() => new()
        {
            m_name = "Server",
            // Receiving chat messages requires a valid character ID.
            m_characterID = new ZDOID(ZDOMan.GetSessionID(), uint.MaxValue),
            m_userInfo = new() { m_id = new(ZNet.instance.m_steamPlatform, GetId()), m_displayName = "Server" },
            m_publicPosition = false,
            m_position = Vector3.zero,
        };

        private static string GetId()
        {
            try
            {
                return SteamGameServer.GetSteamID().ToString();
            }
            catch (Exception)
            {
                // Crossplay/PlayFab servers have no Steam game server identity at all.
                return "0";
            }
        }

        public static void Write(ZPackage pkg)
        {
            var c = Client;
            if (c == null) return;
            var info = c.Value;
            pkg.Write(info.m_name);
            pkg.Write(info.m_characterID);
            pkg.Write(info.m_userInfo.m_id.ToString());
            pkg.Write(info.m_userInfo.m_displayName);
            // Server position is never public.
            pkg.Write(false);
        }
    }
}
