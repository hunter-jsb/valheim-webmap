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

        private static void Send(string text)
        {
            try
            {
                if (ZRoutedRpc.instance == null) return;
                var user = new UserInfo { Name = WebMapConfig.ANNOUNCE_NAME };
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ChatMessage",
                    Vector3.zero, (int)Talker.Type.Shout, user, text, "");
                ZLog.Log($"WebMap: announced \"{text}\"");
            }
            catch (System.Exception ex)
            {
                ZLog.LogWarning("WebMap: announce failed: " + ex);
            }
        }
    }
}
