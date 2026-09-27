using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WebMap
{
    // A message as the chat relay and the settings picker see it.
    internal struct DiscordMessage
    {
        public string Id, Content, DisplayName;
        public bool Bot, Webhook;
    }
    internal struct DiscordRef { public string Id, Name; }

    // The bot's REST calls: posting (the audit log, the chat relay), and reading a
    // channel or a guild back (the relay's poll, the settings picker). API v10,
    // "Authorization: Bot <token>". Every call runs on a pool thread -- posting from
    // the game thread would put Discord's latency inside a chat line or a death.
    // discord_bot_token empty turns every method here into a no-op; DiscordWebHook
    // still carries joins, leaves and deaths on its own webhook.
    internal static class Discord
    {
        private const string Api = "https://discord.com/api/v10";
        private static string Token => WebMapConfig.DISCORD_BOT_TOKEN;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal struct Result { public int Status; public string Body; public double RetryAfter; }

        // The wire, swappable so tests fake it instead of calling Discord.
        internal static Func<string, string, string, Result> Transport = DefaultTransport;

        // Each call type warns once, then stays quiet -- a token pointed at a dead
        // channel, or Discord itself being down, must not spam the console forever
        // from a poller that retries every few seconds.
        private static int warnedPost, warnedGuilds, warnedChannels, warnedMessages;

        // ---------- posting ----------

        // The audit log: a name, a pin, a setting. Joins/leaves/deaths keep using
        // the webhook (DiscordWebHook) -- this is everything else.
        public static void Tell(string line) => Post(WebMapConfig.DISCORD_LOG_CHANNEL, line);

        // Game chat, relayed to Discord. Server lines and this mod's own
        // announcements share the announce name and are not a player talking.
        public static void PostChat(string name, string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return;
            if (name == WebMapConfig.ANNOUNCE_NAME) return;
            if (!WebMapConfig.CHAT_RELAY) return;
            Post(WebMapConfig.DISCORD_CHAT_CHANNEL, $"**{name}**: {text}");
        }

        // Fire-and-forget: the caller (a Harmony patch, a settings write) never
        // waits on Discord.
        public static void Post(string channelId, string content)
        {
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(content)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string body = "{\"content\":\"" + JsonEsc(Clip(content, 1900)) + "\",\"allowed_mentions\":{\"parse\":[]}}";
                    Call("POST", $"/channels/{channelId}/messages", body);
                }
                catch (Exception ex) { WarnOnce(ref warnedPost, "post a message", ex); }
            });
        }

        // ---------- reading ----------

        // The bot's own guilds, for the settings picker.
        public static List<DiscordRef> Guilds()
        {
            var list = new List<DiscordRef>();
            if (string.IsNullOrEmpty(Token)) return list;
            try
            {
                foreach (string item in Body.Items(Call("GET", "/users/@me/guilds", null).Body))
                    list.Add(new DiscordRef { Id = Body.Str(item, "id"), Name = Body.Str(item, "name") });
            }
            catch (Exception ex) { WarnOnce(ref warnedGuilds, "list guilds", ex); }
            return list;
        }

        // A guild's text (0) and announcement (5) channels -- what a message can
        // actually be posted to.
        public static List<DiscordRef> Channels(string guildId)
        {
            var list = new List<DiscordRef>();
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(guildId)) return list;
            try
            {
                foreach (string item in Body.Items(Call("GET", $"/guilds/{guildId}/channels", null).Body))
                {
                    double type = Body.Num(item, "type");
                    if (type == 0 || type == 5) list.Add(new DiscordRef { Id = Body.Str(item, "id"), Name = Body.Str(item, "name") });
                }
            }
            catch (Exception ex) { WarnOnce(ref warnedChannels, "list channels", ex); }
            return list;
        }

        // Messages after an id, in the order Discord answers with -- newest first.
        // Empty (never null) on any failure, so the poller just sees nothing new.
        public static List<DiscordMessage> MessagesAfter(string channelId, string afterId, int limit)
        {
            var list = new List<DiscordMessage>();
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(channelId)) return list;
            try
            {
                string path = $"/channels/{channelId}/messages?limit={limit.ToString(Inv)}"
                    + (string.IsNullOrEmpty(afterId) ? "" : "&after=" + afterId);
                foreach (string item in Body.Items(Call("GET", path, null).Body)) list.Add(Parse(item));
            }
            catch (Exception ex) { WarnOnce(ref warnedMessages, "read messages", ex); }
            return list;
        }

        // The newest message's id, to seed the relay without replaying history.
        public static string LatestMessageId(string channelId)
        {
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(channelId)) return null;
            try
            {
                var items = Body.Items(Call("GET", $"/channels/{channelId}/messages?limit=1", null).Body);
                return items.Count > 0 ? Body.Str(items[0], "id") : null;
            }
            catch (Exception ex) { WarnOnce(ref warnedMessages, "read messages", ex); return null; }
        }

        // Discord writes a mention as <@id>, a role as <@&id>, a channel as <#id> and an
        // emoji as <:name:id>; in game they read as the names people would say.
        internal static string Plain(string content, string item)
        {
            if (string.IsNullOrEmpty(content)) return content;
            var names = new Dictionary<string, string>();
            foreach (var u in Body.Items(Body.Arr(item ?? "", "mentions") ?? "[]"))
            {
                string id = Body.Str(u, "id"); if (id == null) continue;
                names[id] = Body.Str(u, "global_name") ?? Body.Str(u, "username") ?? "someone";
            }
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<@!?(\d+)>", m => "@" + (names.TryGetValue(m.Groups[1].Value, out var n) ? n : "someone"));
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<@&(\d+)>", m => "@" + RoleName(m.Groups[1].Value));
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<#(\d+)>", "#channel");
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<a?:(\w+):\d+>", ":$1:");
            return content;
        }
        private static Dictionary<string, string> roleNames;
        private static DateTime rolesAt;
        private static string RoleName(string id)
        {
            try
            {
                if (roleNames == null || (DateTime.UtcNow - rolesAt).TotalMinutes > 10)
                {
                    var map = new Dictionary<string, string>();
                    string guild = WebMapConfig.DISCORD_GUILD;
                    if (!string.IsNullOrEmpty(guild))
                        foreach (var r in Body.Items(Call("GET", $"/guilds/{guild}/roles", null).Body))
                        { string rid = Body.Str(r, "id"), name = Body.Str(r, "name"); if (rid != null && name != null) map[rid] = name; }
                    roleNames = map; rolesAt = DateTime.UtcNow;
                }
                return roleNames.TryGetValue(id, out var n) ? n : "role";
            }
            catch { return "role"; }
        }

        private static DiscordMessage Parse(string item)
        {
            string author = Body.Obj(item, "author") ?? "{}";
            string member = Body.Obj(item, "member");
            string nick = member != null ? Body.Str(member, "nick") : null;
            string global = Body.Str(author, "global_name");
            string user = Body.Str(author, "username") ?? "someone";
            return new DiscordMessage
            {
                Id = Body.Str(item, "id"),
                Content = Plain(Body.Str(item, "content") ?? "", item),
                Bot = Body.Bool(author, "bot"),
                Webhook = Body.Has(item, "webhook_id"),
                DisplayName = !string.IsNullOrEmpty(nick) ? nick : !string.IsNullOrEmpty(global) ? global : user,
            };
        }

        // ---------- transport ----------

        // One retry on 429, honouring retry_after; anything else past that is the
        // caller's problem, and every caller here already catches and warns once.
        internal static Result Call(string method, string path, string body)
        {
            Result r = Transport(method, path, body);
            if (r.Status == 429)
            {
                Thread.Sleep((int)Math.Ceiling(r.RetryAfter * 1000) + 50);
                r = Transport(method, path, body);
            }
            if (r.Status >= 300) throw new Exception($"discord {method} {path}: HTTP {r.Status}");
            return r;
        }

        private static Result DefaultTransport(string method, string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Api + path);
            req.Method = method;
            req.Headers["Authorization"] = "Bot " + Token;
            req.UserAgent = "WebMap (https://github.com/hunter-jsb/valheim-webmap, 1.0)";
            req.Timeout = 10000;
            if (body != null)
            {
                req.ContentType = "application/json";
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return new Result { Status = (int)resp.StatusCode, Body = ReadBody(resp), RetryAfter = 0 };
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse errResp)
            {
                string errBody = ReadBody(errResp);
                // Discord's own 429 carries retry_after (seconds) in the JSON body;
                // the header is a fallback for anything else that 429s.
                double retry = Body.Num(errBody, "retry_after");
                if (double.IsNaN(retry))
                    double.TryParse(errResp.Headers["Retry-After"], NumberStyles.Float, Inv, out retry);
                return new Result { Status = (int)errResp.StatusCode, Body = errBody, RetryAfter = retry };
            }
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var sr = new StreamReader(s ?? Stream.Null, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static string Clip(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

        private static string JsonEsc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void WarnOnce(ref int flag, string what, Exception ex)
        {
            if (Interlocked.CompareExchange(ref flag, 1, 0) == 0)
                ZLog.LogWarning($"WebMap: discord {what} failed, will keep trying quietly: {ex.Message}");
        }
    }
}
