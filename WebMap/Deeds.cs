using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using static ZRoutedRpc;

namespace WebMap
{
    // Kills, trees felled and rock areas broken, per player. A dedicated server
    // simulates none of them -- the nearest client owns each -- but a hit sent to
    // another player's object passes through it with its attacker, and every
    // destroy and every broken rock area is reported to it.
    //
    // A hit on something the hitter's own game owns never leaves that game, and
    // alone in an area a player owns everything around them. So when no hit was
    // seen, the owner is the one suspect: credited with a creature whose ZDO names
    // them among its attackers, or a tree or rock they stood beside.
    //
    // Game thread only: RouteRPC and HandleDestroyedZDO both run there.
    internal static class Deeds
    {
        public enum Kind { Kill, Tree, Rock }

        // a hit longer ago than this no longer explains a fall
        private const float WindowS = 10f;
        private struct Last { public string who; public float t; }
        private static readonly Dictionary<ZDOID, Last> last = new Dictionary<ZDOID, Last>();
        private static float pruned;

        public static void Hit(ZDOID target, string player, float now)
        {
            if (string.IsNullOrEmpty(player)) return;
            last[target] = new Last { who = player, t = now };
            if (now - pruned < WindowS) return;
            pruned = now;
            var stale = new List<ZDOID>();
            foreach (var kv in last) if (now - kv.Value.t > WindowS) stale.Add(kv.Key);
            foreach (var k in stale) last.Remove(k);
        }

        public static void AreaBroken(ZDOID target, float now, string owner = null) => Credit(Who(target, now, owner), Kind.Rock);

        // Nobody seen hitting it and no owner to suspect: a despawn, not a deed.
        public static void Gone(ZDOID target, Kind kind, float now, string owner = null)
        {
            Credit(Who(target, now, owner), kind);
            last.Remove(target);
        }

        private static string Who(ZDOID target, float now, string owner)
            => last.TryGetValue(target, out var l) && now - l.t <= WindowS ? l.who : owner;

        private static void Credit(string who, Kind kind) { if (!string.IsNullOrEmpty(who)) Stats.Deed(who, kind); }

        internal static void ResetForTests() { last.Clear(); pruned = 0f; }

        // The owner's player, if they stood within m of the spot; null otherwise.
        internal static string OwnerBeside(long owner, Vector3 at, float m)
        {
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(owner) : null;
            if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) return null;
            ZDO me = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (me == null) return null;
            Vector3 p = me.GetPosition();
            float dx = p.x - at.x, dz = p.z - at.z;
            return dx * dx + dz * dz <= m * m ? peer.m_playerName : null;
        }
    }

    // Hits and broken rock areas, as the server forwards them. RouteRPC gets every
    // routed RPC not addressed to the server itself, already parsed, so passing on
    // the rest costs one compare.
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal class DeedsRoutePatch
    {
        private static readonly int DamageHash = "RPC_Damage".GetStableHashCode();
        private static readonly int HitHash = "Hit".GetStableHashCode();                  // MineRock's hit
        private static readonly int AreaHash = "RPC_SetAreaHealth".GetStableHashCode();   // MineRock5, sent as an area breaks
        private static readonly int HideHash = "Hide".GetStableHashCode();                // MineRock, likewise
        // a mined rock is wide, and its pivot can stand well off the face being worked
        private const float AreaM = 32f;
        private static bool warned;

        private static void Prefix(RoutedRPCData rpcData)
        {
            if (rpcData == null) return;
            int h = rpcData.m_methodHash;
            if (h != DamageHash && h != HitHash && h != AreaHash && h != HideHash) return;
            ZPackage p = rpcData.m_parameters;
            if (p == null) return;
            int pos = p.GetPos();          // the game forwards this package after us
            try
            {
                p.SetPos(0);
                float now = Time.realtimeSinceStartup;
                if (h == DamageHash || h == HitHash)
                {
                    var hit = new HitData();
                    hit.Deserialize(ref p);
                    if (hit.m_attacker.IsNone()) return;
                    ZDO a = ZDOMan.instance.GetZDO(hit.m_attacker);   // a creature's has no player name
                    string who = a != null ? a.GetString(ZDOVars.s_playerName, "") : "";
                    Deeds.Hit(rpcData.m_targetZDO, who, now);
                    if (h == DamageHash && who.Length > 0) Gear.Struck(who, rpcData.m_targetZDO, hit);
                }
                else
                {
                    p.ReadInt();                                         // the area
                    if (h == AreaHash && p.ReadSingle() > 0f) return;
                    ZDO rock = ZDOMan.instance.GetZDO(rpcData.m_targetZDO);
                    string owner = rock != null ? Deeds.OwnerBeside(rpcData.m_senderPeerID, rock.GetPosition(), AreaM) : null;
                    Deeds.AreaBroken(rpcData.m_targetZDO, now, owner);
                }
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; ZLog.LogWarning("WebMap: deeds: reading a hit failed: " + e.Message); }
            }
            finally { p.SetPos(pos); }
        }
    }

    // Every destroyed ZDO is reported to the server, which still holds it here.
    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    internal class DeedsGonePatch
    {
        // a felled tree or broken rock is within reach of the one who did it
        private const float BesideM = 16f;
        private static readonly Dictionary<int, int> kinds = new Dictionary<int, int>();
        private static bool warned;

        private static void Prefix(ZDOID uid)
        {
            try
            {
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo == null) return;
                int k = KindOf(zdo.GetPrefab());
                if (k < 0) return;
                var kind = (Deeds.Kind)k;
                string owner;
                if (kind == Deeds.Kind.Kill)
                {
                    if (zdo.GetBool(ZDOVars.s_tamed)) return;
                    ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(zdo.GetOwner()) : null;
                    owner = peer != null ? peer.m_playerName : null;
                    // the creature's own record of the players who hit it, keyed as Character.RPC_Damage keys it
                    if (!string.IsNullOrEmpty(owner) && !zdo.GetBool(ZDOVars.s_attackers + owner)) owner = null;
                }
                else owner = Deeds.OwnerBeside(zdo.GetOwner(), zdo.GetPosition(), BesideM);
                Deeds.Gone(uid, kind, Time.realtimeSinceStartup, owner);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; ZLog.LogWarning("WebMap: deeds: reading a destroy failed: " + e.Message); }
            }
        }

        // -1 for what counts as nothing: players, logs, stumps, pieces, everything else
        internal static int KindOf(int prefab)
        {
            if (kinds.TryGetValue(prefab, out int k)) return k;
            if (ZNetScene.instance == null) return -1;
            k = -1;
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go != null)
            {
                if (go.GetComponent<Character>() != null) { if (go.GetComponent<Player>() == null) k = (int)Deeds.Kind.Kill; }
                else if (go.GetComponent<TreeBase>() != null) k = (int)Deeds.Kind.Tree;
                else if (go.GetComponent<Destructible>() != null
                         && WorldObjects.ClassifyName(go.name.ToLowerInvariant()) == WorldObjects.Cat.Rock) k = (int)Deeds.Kind.Rock;
            }
            kinds[prefab] = k;
            return k;
        }
    }
}
