using System;
using System.Collections.Specialized;
using System.Net;
using System.Threading;
using WebSocketSharp;

namespace WebMap
{
    public class DiscordWebHook : IDisposable
    {
        private readonly string webHookUrl;

        public DiscordWebHook(string url)
        {
            webHookUrl = url;
        }

        // Called from the join and disconnect handlers, which are the game thread in
        // the middle of the handshake: posting there put Discord's latency -- up to
        // WebClient's 100-second timeout, or a throw on a bad URL -- inside a player's
        // join. The post goes to the pool and its failures stay there.
        public void SendMessage(string msgSend)
        {
            if (webHookUrl.IsNullOrEmpty()) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var values = new NameValueCollection { { "content", msgSend } };
                    using (var webClient = new WebClient()) webClient.UploadValues(webHookUrl, values);
                }
                catch (Exception e) { ZLog.LogWarning("WebMap: discord webhook failed: " + e.Message); }
            });
        }

        public void Dispose() { }
    }
}
