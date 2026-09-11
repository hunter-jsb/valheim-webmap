using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace WebMap
{
    // Server-sent chat announcements: restart warnings and anything else players
    // should see without being in Discord.
    //
    // This is the ChatMessage RPC the mod already reads, sent in reverse --
    // position, type, sender, text -- addressed to Everybody.
    //
    // The shared secret lives in a file next to the DLL rather than in the
    // BepInEx config, because BepInEx rewrites its config on shutdown: a token
    // written while the server is running would be thrown away. No file or an
    // empty one means the endpoint is closed.
    internal static class Announce
    {
        private const string TokenFile = "announce.token";
        private static readonly Queue<string> pending = new Queue<string>();
        private static string tokenCache;
        private static System.DateTime tokenStamp;

        public static string Token
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                    string path = Path.Combine(dir, TokenFile);
                    if (!File.Exists(path)) return null;
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (tokenCache == null || stamp != tokenStamp)
                    {
                        tokenCache = File.ReadAllText(path).Trim();
                        tokenStamp = stamp;
                    }
                    return string.IsNullOrEmpty(tokenCache) ? null : tokenCache;
                }
                catch { return null; }
            }
        }

        // Called from the HTTP thread, so it may only enqueue -- Unity objects
        // must be touched on the main thread.
        public static void Enqueue(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (pending) pending.Enqueue(text.Length > 300 ? text.Substring(0, 300) : text);
        }

        public static IEnumerator Pump()
        {
            while (true)
            {
                string msg = null;
                lock (pending) { if (pending.Count > 0) msg = pending.Dequeue(); }
                if (msg != null) Send(msg);
                yield return new WaitForSeconds(0.5f);
            }
        }

        // Sent as MessageHud's "ShowMessage", not chat.
        //
        // Chat is not usable from a server-only mod. Chat registers
        //   Register<Vector3, int, UserInfo, string>("ChatMessage", RPC_ChatMessage)
        // and Chat.OnNewChatMessage then runs
        //   RelationsManager.CheckPermissionAsync(sender.UserId, CommunicateWithUsingText, ...)
        // before displaying anything. The server has no platform UserId to put in
        // that UserInfo, the permission check does not come back granted, and the
        // message is dropped in silence.
        //
        // MessageHud registers Register<int, string>("ShowMessage", RPC_ShowMessage),
        // whose body is just ShowMessage((MessageType)type, text) -- no sender check,
        // no relations lookup, no distance filter. That is the channel a server can
        // actually reach an unmodded client on.
        private static void Send(string text)
        {
            try
            {
                if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
                // Target 0 is "everybody", exactly what MessageHud.MessageAll sends.
                // Addressing each peer by m_uid instead looked equivalent and reached
                // nobody in game while still reaching the web feed -- which is what an
                // empty peer list looks like from the outside. Broadcast needs no list.
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, "ShowMessage",
                    (int)MessageHud.MessageType.Center, text);
                int sent = 0;
                try { var ps = ZNet.instance.GetPeers(); sent = ps == null ? -1 : ps.Count; } catch { sent = -2; }
                ZLog.Log($"WebMap: announced (peers seen: {sent}): \"{text}\"");
                // the web feed no longer sees this via the chat observer, so add it here
                try { WebMap.mapDataServer?.AddMessage(0L, (int)Talker.Type.Shout,
                                                       WebMapConfig.ANNOUNCE_NAME, text); } catch { }
            }
            catch (System.Exception ex)
            {
                ZLog.LogWarning("WebMap: announce failed: " + ex);
            }
        }
    }
}
