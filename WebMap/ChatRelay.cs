using System;
using System.Collections.Generic;
using System.Threading;

namespace WebMap
{
    // Discord -> game. A Timer callback polls on a pool thread; the lines it finds
    // go through Announce.Enqueue, the same hand-off the game-thread pump already
    // drains for server announcements, so nothing here ever touches a Unity object.
    internal static class ChatRelay
    {
        private const int PollMs = 3000;
        internal const int Cap = 5;      // a flood in Discord must not flood the server

        private static System.Threading.Timer timer;
        private static string afterId;          // null until seeded
        private static string seededChannel;    // which channel afterId belongs to
        private static int warned;

        public static void Start()
        {
            if (timer != null) return;
            timer = new System.Threading.Timer(_ => { try { Poll(); } catch (Exception ex) { WarnOnce(ex); } }, null, PollMs, PollMs);
        }

        private static void Poll()
        {
            string channel = WebMapConfig.DISCORD_CHAT_CHANNEL;
            if (string.IsNullOrEmpty(WebMapConfig.DISCORD_BOT_TOKEN) || string.IsNullOrEmpty(channel) || !WebMapConfig.CHAT_RELAY)
            {
                seededChannel = null;   // off, or pointed elsewhere: reseed from scratch if it comes back
                return;
            }
            if (seededChannel != channel || afterId == null)
            {
                // A fresh channel, or an empty one still waiting for its first message:
                // start from what's already there so a restart never replays history.
                afterId = Discord.LatestMessageId(channel);
                seededChannel = channel;
                return;
            }

            var msgs = Discord.MessagesAfter(channel, afterId, 50);
            if (msgs.Count == 0) return;
            afterId = msgs[0].Id;   // newest first; the cursor moves past everything fetched, spoken or not
            foreach (string line in LinesToSpeak(msgs)) Announce.Enqueue(line);
        }

        // The pure decision: which of a poll's messages (newest first, as Discord
        // answers) get spoken, oldest first, capped so a flood cannot spam the
        // server. A message from a bot or a webhook is skipped -- which is also how
        // this mod's own posts, made through the same bot, never echo back.
        internal static List<string> LinesToSpeak(List<DiscordMessage> newestFirst)
        {
            var lines = new List<string>();
            for (int i = newestFirst.Count - 1; i >= 0 && lines.Count < Cap; i--)
            {
                var m = newestFirst[i];
                if (m.Bot || m.Webhook || string.IsNullOrEmpty(m.Content)) continue;
                lines.Add($"[Discord] {m.DisplayName}: {m.Content}");
            }
            return lines;
        }

        private static void WarnOnce(Exception ex)
        {
            if (Interlocked.CompareExchange(ref warned, 1, 0) == 0)
                ZLog.LogWarning("WebMap: discord chat relay failed, will keep trying quietly: " + ex.Message);
        }

        // for the tests: back to unseeded, as at boot
        internal static void ResetForTests() { timer = null; afterId = null; seededChannel = null; warned = 0; }
    }
}
