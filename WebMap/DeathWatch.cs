using System;
using System.Collections.Generic;
using HarmonyLib;
using static ZRoutedRpc;

namespace WebMap
{
    // Player deaths, reported into the same message feed as joins and leaves.
    //
    // A dedicated server owns no Player object, so nothing local ever runs
    // Player.OnDeath. What the server DOES see is the routed RPC the dying
    // client fires from it: Player.OnDeath calls
    // m_nview.InvokeRPC(ZNetView.Everybody, "OnDeath"), and an Everybody-targeted
    // routed RPC reaches the server as traffic it FORWARDS -- the same reason
    // chat is observed at RouteRPC rather than HandleRoutedRPC. RouteRPC runs
    // even when the dying player is the only peer online, because the server
    // enters it for any target that is not itself.
    //
    // "OnDeath" is registered and invoked in exactly one place in
    // assembly_valheim (Player), so the method hash cannot mean anything else --
    // no creature death or other event shares it.
    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RouteRPC))]
    internal class DeathWatch
    {
        // A death and its respawn are many seconds apart, so anything closer
        // than this is a redelivery of one death, not a second one.
        private const double DUPLICATE_WINDOW = 5.0;

        private static int deathRpcHash;
        private static bool deathRpcHashReady;

        // Peer id -> when we last announced that player's death.
        private static readonly Dictionary<long, DateTime> lastDeath = new Dictionary<long, DateTime>();

        private static void Postfix(RoutedRPCData rpcData)
        {
            try
            {
                if (rpcData == null)
                {
                    return;
                }

                if (!deathRpcHashReady)
                {
                    // The game's own hash, never a reimplementation: clients compute
                    // the wire hash with the vanilla GetStableHashCode.
                    deathRpcHash = "OnDeath".GetStableHashCode();
                    deathRpcHashReady = true;
                }

                if (rpcData.m_methodHash != deathRpcHash)
                {
                    return;
                }

                Announce(rpcData);
            }
            catch (Exception e)
            {
                // Never let a reporting failure escape into the RPC path.
                ZLog.LogWarning($"WebMap: death watch failed: {e.Message}");
            }
        }

        private static void Announce(RoutedRPCData rpcData)
        {
            MapDataServer server = MapDataServer.getInstance();
            if (server == null)
            {
                return;
            }

            ZDOID characterID = rpcData.m_targetZDO;
            long peerID = rpcData.m_senderPeerID;
            string name = null;

            // The dying character's own peer is the authority on the name. Match
            // the character ZDO first; the sender is only a fallback for the case
            // where someone else's client owns the character (ownership moves when
            // a player disconnects mid-fall).
            List<ZNetPeer> peers = server.players;
            if (peers != null)
            {
                foreach (ZNetPeer peer in peers)
                {
                    if (peer == null || peer.m_server)
                    {
                        continue;
                    }
                    if (!characterID.IsNone() && peer.m_characterID == characterID)
                    {
                        name = peer.m_playerName;
                        peerID = peer.m_uid;
                        break;
                    }
                    if (name == null && peer.m_uid == rpcData.m_senderPeerID)
                    {
                        name = peer.m_playerName;
                    }
                }
            }

            if (string.IsNullOrEmpty(name) && !characterID.IsNone())
            {
                ZDO zdo = null;
                try { zdo = ZDOMan.instance.GetZDO(characterID); } catch { }
                if (zdo != null)
                {
                    name = zdo.GetString(ZDOVars.s_playerName, "");
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!ShouldAnnounce(peerID))
            {
                return;
            }

            ZLog.Log($"WebMap: player {name} died");
            server.AddMessage(peerID, (int)Talker.Type.Normal, "Server", $"player _{name}_ died");
            Stats.Death(name);
        }

        private static bool ShouldAnnounce(long key)
        {
            DateTime now = DateTime.UtcNow;

            if (lastDeath.TryGetValue(key, out DateTime previous) &&
                (now - previous).TotalSeconds < DUPLICATE_WINDOW)
            {
                return false;
            }

            // Only ever reached on a death, so sweeping here costs nothing.
            if (lastDeath.Count > 64)
            {
                List<long> stale = new List<long>();
                foreach (KeyValuePair<long, DateTime> entry in lastDeath)
                {
                    if ((now - entry.Value).TotalSeconds >= DUPLICATE_WINDOW)
                    {
                        stale.Add(entry.Key);
                    }
                }
                stale.ForEach(k => lastDeath.Remove(k));
            }

            lastDeath[key] = now;
            return true;
        }
    }
}
