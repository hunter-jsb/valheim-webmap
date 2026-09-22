using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using static WebMap.WebMapConfig;

namespace WebMap
{
    [Serializable]
    public struct MapMessage
    {
        public long id;
        public int type;
        public string name;
        public string message;
        public string ts;
        // 1 for the server's own joined/left/died lines, 0 for anything a person
        // said -- chat and announcements. The feed is read as a chat monitor, and
        // the events outnumber chat by thirty to one.
        public int ev;

        public MapMessage(long id, int type, string name, string message, bool ev = false)
        {
            this.id = id;
            this.type = type;
            this.name = name;
            this.message = message;
            this.ev = ev ? 1 : 0;
            this.ts = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }

        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
    }

    public class WebSocketHandler : WebSocketBehavior
    {
        protected override void OnOpen()
        {
            string endpoint = Context.Headers.Get("X-Forwarded-For");
            if (endpoint.IsNullOrEmpty())
            {
                endpoint = Context.UserEndPoint.ToString();
            }
            ZLog.Log("WebMap: new visitor connected from " + endpoint);
            base.OnOpen();
        }

        // protected override void OnClose(CloseEventArgs e) {
        // }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.Data.ToString() == "players")
            {
                Send(MapDataServer.getInstance().PlayersWs);
            }
            base.OnMessage(e);
        }
    }

    public class MapDataServer
    {
        private static readonly Dictionary<string, string> contentTypes = new Dictionary<string, string> {
            {"html", "text/html"},
            {"js", "text/javascript"},
            {"css", "text/css"},
            {"png", "image/png"},
            {"jpg", "image/jpeg"},
            {"webp", "image/webp"}
        };

        private readonly System.Threading.Timer broadcastTimer;
        // Written from HTTP threads, and a browser opens several connections at once
        // on the first page load: a plain Dictionary can corrupt under that.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> fileCache;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> fileStamp;   // when each cached file was read
        // The fog as bytes, laid out like a texture. A Texture2D is only the PNG
        // decoder at load; every read and write after that is on this array, so the
        // encode can run on any thread. A pixel only ever turns white, so an encode
        // that overlaps a write is at worst one pixel behind.
        public byte[] fogRgba;
        public void SetFog(Texture2D tex, bool fresh)
        {
            int n = WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE;
            var b = new byte[n * 4];
            if (fresh)
            {
                for (int i = 3; i < b.Length; i += 4) b[i] = 255;      // opaque black: nothing explored
            }
            else
            {
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length && i < n; i++) { int o = i * 4; b[o] = px[i].r; b[o + 1] = px[i].g; b[o + 2] = px[i].b; b[o + 3] = px[i].a; }
            }
            fogRgba = b;
            fogPngStale = true;
        }
        private readonly HttpServer httpServer;

        public byte[] mapImageData;
        private byte[] mapJpgCache;          // built once; the world render never changes

        // The fog changes a few pixels every couple of seconds and is asked for far
        // more often than that. Encode when it has changed, serve the bytes otherwise.
        // EncodeArrayToPNG is thread-safe, so this may run on the HTTP thread.
        public volatile bool fogPngStale = true;
        public volatile int fogRev = 1;          // bumped once per pass that revealed anything
        private byte[] fogPngCache;
        private readonly object fogPngLock = new object();
        public byte[] GetFogPng()
        {
            lock (fogPngLock)
            {
                if (fogPngStale || fogPngCache == null)
                {
                    if (fogRgba == null) return fogPngCache ?? new byte[0];
                    int size = WebMapConfig.TEXTURE_SIZE;
                    fogPngCache = ImageConv.EncodeRgbaToPNG(fogRgba, size, size);
                    fogPngStale = false;
                }
                return fogPngCache;
            }
        }

        public void BuildMapJpg()
        {
            if (mapJpgCache != null) return;
            if (mapImageData == null || mapImageData.Length == 0) return;
            try
            {
                int size = WebMapConfig.RENDER_SIZE;      // LoadImage resizes anyway
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                if (!ImageConv.LoadImage(tex, mapImageData)) return;
                mapJpgCache = ImageConv.EncodeToJPG(tex, 85);
                UnityEngine.Object.Destroy(tex);
                ZLog.Log($"WebMap: world render as jpeg: {mapJpgCache.Length} bytes (png was {mapImageData.Length})");
            }
            catch (Exception ex)
            {
                ZLog.LogWarning("WebMap: jpeg encode failed: " + ex);
            }
        }
        public List<string> pins = new List<string>();
        public List<MapMessage> sentMessages = new List<MapMessage>();
        public List<MapMessage> newMessages = new List<MapMessage>();
        // Chat arrives on the game thread, is drained by a timer on a pool thread,
        // and is read by HTTP on a third -- so the lists are never touched without
        // this, and /messages serves a snapshot the timer builds rather than walking
        // a list another thread is editing. Latent until 2.9.0: before chat could be
        // observed at all, these lists were almost always empty.
        private readonly object messageLock = new object();
        private volatile string messagesJson = "[]";
        public List<ZNetPeer> players = new List<ZNetPeer>();
        public string lastPlayerResponse = "";
        // Player state comes from ZDOMan and from ZNet's own live m_peers list, and
        // neither may be read off the game thread: the broadcast timer runs on a pool
        // thread and HTTP on others again, so both would be walking a list the game is
        // editing. RefreshPlayerSnapshot runs on the game thread and everyone else
        // only ever reads these strings.
        private volatile string playersWs = "players";
        private volatile string playersJson = "{\"count\":0,\"players\":[]}";
        public string PlayersWs => playersWs;
        private bool forceReload = false;
        // /config carries the world name, which lives on ZNet: main-thread state.
        // The main thread builds this when the world loads; HTTP only serves it.
        private volatile string configJson = "{}";
        public void RefreshConfig() => configJson = MakeClientConfigJson();
        // Reads that the world sweep exists to serve. Anything a monitor probes
        // (/config, /players, /map, /pins, /messages) must not keep it running.
        private static readonly HashSet<string> sweepReads = new HashSet<string> {
            "/structures", "/forest", "/fog", "/vehicles", "/portals", "/graves", "/pieces", "/trails",
            "/forest/stats", "/structures/stats", "/state"
        };
        private readonly string publicRoot;
        private readonly WebSocketServiceHost webSocketHandler;
        private static MapDataServer __instance;

        public MapDataServer()
        {
            __instance = this;

            httpServer = new HttpServer(SERVER_PORT);
            httpServer.AddWebSocketService<WebSocketHandler>("/");
            httpServer.KeepClean = true;

            webSocketHandler = httpServer.WebSocketServices["/"];

            broadcastTimer = new System.Threading.Timer(e =>
            {
                string dataString = "";
                if (forceReload)
                {
                    webSocketHandler.Sessions.Broadcast("reload\n");
                    forceReload = false;
                }
                else
                {
                    dataString = playersWs;
                    if (dataString != lastPlayerResponse)
                    {
                        webSocketHandler.Sessions.Broadcast(dataString);
                        lastPlayerResponse = dataString;
                    }

                    List<string> tosend = null;
                    lock (messageLock)
                    {
                        if (newMessages.Count > 0)
                        {
                            tosend = new List<string>();
                            newMessages.ForEach(message =>
                            {
                                tosend.Add(message.ToJson());
                                sentMessages.Add(message);
                            });
                            newMessages.Clear();
                            newMessages.TrimExcess();
                            // Per kind, not over the whole list: a quiet evening of
                            // joins and deaths would otherwise push every line of
                            // chat out of a feed someone is reading for the chat.
                            TrimToDepth(sentMessages, 0, WebMapConfig.MAX_MESSAGES);
                            TrimToDepth(sentMessages, 1, WebMapConfig.MAX_MESSAGES);
                            var all = new List<string>(sentMessages.Count);
                            sentMessages.ForEach(m => all.Add(m.ToJson()));
                            messagesJson = "[" + string.Join(", ", all) + "]";
                        }
                    }
                    if (tosend != null && tosend.Count > 0)
                        webSocketHandler.Sessions.Broadcast("messages\n[" + string.Join(",", tosend) + "]");
                }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(PLAYER_UPDATE_INTERVAL));

            publicRoot = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "web");

            fileCache = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
            fileStamp = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

            httpServer.OnGet += (sender, e) =>
            {
                if (ProcessSpecialRoutes(e)) return;

                ServeStaticFiles(e);
            };
            // /announce is a POST; without this websocket-sharp answers 501
            httpServer.OnPost += (sender, e) =>
            {
                if (ProcessSpecialRoutes(e)) return;

                e.Response.StatusCode = 404;
                e.Response.Close();
            };
        }

        // Called only from RefreshPlayerSnapshot, i.e. only on the game thread.
        private string BuildPlayerResponse()
        {
            string dataString = "players\n";

            players.ForEach(player =>
            {
                ZDO zdoData = null;
                try
                {
                    zdoData = ZDOMan.instance.GetZDO(player.m_characterID);
                }
                catch { }

                if (zdoData != null)
                {
                    Vector3 pos = zdoData.GetPosition();
                    int maxHealth = (int)Math.Ceiling(zdoData.GetFloat(ZDOVars.s_maxHealth, 25));
                    int health = (int)Math.Ceiling(zdoData.GetFloat(ZDOVars.s_health, maxHealth));
                    int dead = zdoData.GetBool(ZDOVars.s_dead) ? 1 : 0;
                    int pvp = zdoData.GetBool(ZDOVars.s_pvp) ? 1 : 0;
                    int inbed = zdoData.GetBool(ZDOVars.s_inBed) ? 1 : 0;

                    maxHealth = Math.Max(maxHealth, health);

                    dataString += $"{player.m_uid}\n{player.m_playerName}\n{health}\n{maxHealth}\n";
                    if (!player.m_publicRefPos)
                        dataString += "hidden\n";
                    if (player.m_publicRefPos || WebMapConfig.ALWAYS_VISIBLE || WebMapConfig.ALWAYS_MAP)
                        dataString += FormattableString.Invariant($"{pos.x:0.##},{pos.z:0.##}\n");
                    dataString += $"{dead}{pvp}{inbed}\n\n";
                }

            });
            return dataString.Trim();
        }

        // Same data the websocket "players" frame carries, as JSON, so plain HTTP
        // clients don't have to speak websocket. Position is omitted for players
        // who chose to be hidden (unless ALWAYS_VISIBLE), matching the map's own
        // behaviour -- a hidden player must not leak coordinates over HTTP either.
        private string BuildPlayersJson()
        {
            var entries = new List<string>();
            players.ForEach(player =>
            {
                ZDO zdoData = null;
                try { zdoData = ZDOMan.instance.GetZDO(player.m_characterID); } catch { }
                if (zdoData == null) return;

                Vector3 pos = zdoData.GetPosition();
                int maxHealth = (int)Math.Ceiling(zdoData.GetFloat(ZDOVars.s_maxHealth, 25));
                int health = (int)Math.Ceiling(zdoData.GetFloat(ZDOVars.s_health, maxHealth));
                maxHealth = Math.Max(maxHealth, health);
                bool hidden = !player.m_publicRefPos;
                bool showPos = player.m_publicRefPos || WebMapConfig.ALWAYS_VISIBLE;

                var sb = new StringBuilder();
                sb.Append("{\"name\":\"").Append(JsonEscape(player.m_playerName)).Append("\"");
                sb.Append(",\"health\":").Append(health);
                sb.Append(",\"maxHealth\":").Append(maxHealth);
                sb.Append(",\"dead\":").Append(zdoData.GetBool(ZDOVars.s_dead) ? "true" : "false");
                sb.Append(",\"inBed\":").Append(zdoData.GetBool(ZDOVars.s_inBed) ? "true" : "false");
                sb.Append(",\"hidden\":").Append(hidden ? "true" : "false");
                if (showPos)
                {
                    sb.Append(FormattableString.Invariant($",\"x\":{pos.x:0.##},\"z\":{pos.z:0.##}"));
                }
                sb.Append("}");
                entries.Add(sb.ToString());
            });
            return "{\"count\":" + entries.Count + ",\"players\":[" + string.Join(",", entries) + "]}";
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
        }

        public static MapDataServer getInstance()
        {
            return __instance;
        }
        public void Stop()
        {
            broadcastTimer.Dispose();
            httpServer.Stop();
        }

        private void ServeStaticFiles(HttpRequestEventArgs e)
        {
            HttpListenerRequest req = e.Request;
            HttpListenerResponse res = e.Response;

            string rawRequestPath = req.RawUrl.Split('?')[0];   // ?v= is for caches, not for us
            if (rawRequestPath == "/") rawRequestPath = "/index.html";

            // GetFileName, not the last '/'-separated part: splitting on '/' alone
            // leaves backslashes intact, and Path.Combine would honour them as
            // separators on Windows -- an escape from the web root.
            string requestedFile = Path.GetFileName(rawRequestPath);
            string[] fileParts = requestedFile.Split('.');
            string fileExt = fileParts[fileParts.Length - 1];

            if (contentTypes.ContainsKey(fileExt))
            {
                byte[] requestedFileBytes = new byte[0];
                string filePath = Path.Combine(publicRoot, requestedFile);
                // A deploy writes a new file under the same name, so the cache holds
                // bytes only for as long as the file still carries the timestamp they
                // were read at: one stat per request, and a new viewer shows at once.
                DateTime stamp = DateTime.MinValue;
                try { stamp = File.GetLastWriteTimeUtc(filePath); } catch { }
                if (!fileCache.TryGetValue(requestedFile, out requestedFileBytes)
                    || !fileStamp.TryGetValue(requestedFile, out var readAt) || readAt != stamp)
                {
                    requestedFileBytes = new byte[0];
                    try
                    {
                        requestedFileBytes = File.ReadAllBytes(filePath);
                        if (CACHE_SERVER_FILES) { fileCache[requestedFile] = requestedFileBytes; fileStamp[requestedFile] = stamp; }
                    }
                    catch (Exception ex)
                    {
                        ZLog.LogError("WebMap: FAILED TO READ FILE! " + ex.Message);
                    }
                }

                if (requestedFileBytes.Length > 0)
                {
                    // a page must pick up a new build on the next visit; its assets can wait a bit
                    res.Headers.Add(HttpResponseHeader.CacheControl, fileExt == "html" ? "no-cache" : "public, max-age=300");
                    res.ContentType = contentTypes[fileExt];
                    res.StatusCode = 200;
                    res.ContentLength64 = requestedFileBytes.Length;
                    res.Close(requestedFileBytes, true);
                }
                else
                {
                    res.StatusCode = 404;
                    res.Close();
                }
            }
            else
            {
                res.StatusCode = 404;
                res.Close();
            }
        }

        private bool ProcessSpecialRoutes(HttpRequestEventArgs e)
        {
            HttpListenerRequest req = e.Request;
            HttpListenerResponse res = e.Response;
            string rawRequestPath = req.RawUrl.Split('?')[0];
            byte[] textBytes;

            if (sweepReads.Contains(rawRequestPath)) StructureMap.LastRead = Environment.TickCount;

            switch (rawRequestPath)
            {
                case "/config":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(configJson);
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/map.jpg":
                    // The world render is opaque and several MB as a PNG, which is a
                    // long transatlantic download on a cold edge. As a JPEG it is
                    // roughly a seventh of that, and the loss is invisible on terrain.
                    {
                        byte[] jpg = mapJpgCache;
                        if (jpg == null || jpg.Length == 0)
                        {
                            res.StatusCode = 503;
                            res.Close();
                            return true;
                        }
                        res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=604800, immutable");
                        res.ContentType = "image/jpeg";
                        res.StatusCode = 200;
                        res.ContentLength64 = jpg.Length;
                        res.Close(jpg, true);
                        return true;
                    }
                case "/map":
                    if (mapImageData == null || mapImageData.Length == 0)
                    {
                        res.StatusCode = 503;          // still rendering; never cached
                        res.Close();
                        return true;
                    }
                    // Doing things this way to make the full map harder to accidentally see.
                    res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=604800, immutable");
                    res.ContentType = "application/octet-stream";
                    res.StatusCode = 200;
                    res.ContentLength64 = mapImageData.Length;
                    res.Close(mapImageData, true);
                    return true;
                case "/fog":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] fogBytes = GetFogPng();
                    if (fogBytes.Length == 0) { res.StatusCode = 503; res.Close(); return true; }   // not rendered yet: never a cacheable empty 200
                    res.ContentLength64 = fogBytes.Length;
                    res.Close(fogBytes, true);
                    return true;
                case "/chart":
                    // one image per world, never changing: let the edge keep it
                    res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=3600");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] chartBytes = Chart.GetPng();
                    if (chartBytes.Length == 0) { res.StatusCode = 503; res.Close(); return true; }   // not rendered yet: never a cacheable empty 200
                    res.ContentLength64 = chartBytes.Length;
                    res.Close(chartBytes, true);
                    return true;
                case "/messages":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(messagesJson);
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/players":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(playersJson);
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/structures":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] structureBytes = StructureMap.GetPng();
                    if (structureBytes.Length == 0) { res.StatusCode = 503; res.Close(); return true; }   // not rendered yet: never a cacheable empty 200
                    res.ContentLength64 = structureBytes.Length;
                    res.Close(structureBytes, true);
                    return true;
                case "/forest":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] forestBytes = ForestMap.GetPng();
                    if (forestBytes.Length == 0) { res.StatusCode = 503; res.Close(); return true; }   // not rendered yet: never a cacheable empty 200
                    res.ContentLength64 = forestBytes.Length;
                    res.Close(forestBytes, true);
                    return true;
                case "/trails":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] trailBytes = Trails.GetPng();
                    if (trailBytes.Length == 0) { res.StatusCode = 503; res.Close(); return true; }   // nothing walked yet, or not rendered
                    res.ContentLength64 = trailBytes.Length;
                    res.Close(trailBytes, true);
                    return true;
                case "/forest/stats":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(ForestMap.GetStats());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/vehicles":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Vehicles.GetJson());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/portals":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Portals.GetJson());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/graves":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Graves.GetJson());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/pieces":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Pieces.GetJson());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/state":
                    // One document per tick for a viewer: every small block the page
                    // polls, and a revision per large layer so it fetches a layer only
                    // when the picture changed. Each block is a string another thread
                    // already built; this is concatenation.
                    {
                        string pinsJson;
                        lock (pins)
                        {
                            var q = new List<string>(pins.Count);
                            foreach (string line in pins) q.Add("\"" + JsonEscape(line) + "\"");
                            pinsJson = "[" + string.Join(",", q) + "]";
                        }
                        string state = "{\"now\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            + ",\"rev\":{\"fog\":" + fogRev + ",\"pieces\":" + Pieces.Rev + ",\"forest\":" + ForestMap.Rev
                            + ",\"structures\":" + StructureMap.Rev + ",\"chart\":" + Chart.Rev + ",\"trails\":" + Trails.Rev
                            + ",\"features\":" + Features.Rev + "}"
                            + ",\"players\":" + playersJson + ",\"messages\":" + messagesJson + ",\"pins\":" + pinsJson
                            + ",\"vehicles\":" + Vehicles.GetJson() + ",\"portals\":" + Portals.GetJson() + ",\"graves\":" + Graves.GetJson()
                            + ",\"traders\":" + Traders.Json() + ",\"deaths\":" + Stats.DeathsJson()
                            + ",\"structures\":" + StructureMap.GetStats() + ",\"forest\":" + ForestMap.GetStats()
                            + ",\"stats\":" + Stats.Json(PinsByName()) + "}";
                        res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                        res.ContentType = "application/json";
                        res.StatusCode = 200;
                        textBytes = Encoding.UTF8.GetBytes(state);
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                case "/stats/players":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Stats.Json(PinsByName()));
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/structures/stats":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(StructureMap.GetStats());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/at":
                    // what is at a spot, for a click on the map; nothing for unwalked ground
                    {
                        res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                        res.ContentType = "application/json";
                        string body = null;
                        if (float.TryParse(req.QueryString["x"], NumberStyles.Float, CultureInfo.InvariantCulture, out float ax)
                            && float.TryParse(req.QueryString["z"], NumberStyles.Float, CultureInfo.InvariantCulture, out float az))
                            body = Features.At(ax, az);
                        res.StatusCode = body == null ? 404 : 200;
                        textBytes = Encoding.UTF8.GetBytes(body ?? "{\"error\":\"unexplored\"}");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                case "/features":
                    // the world's geography with its names; the fog is the viewer's to apply
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(Features.Json());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/names":
                    // A name given on the site. The same shared secret as /announce
                    // guards it, and the caller says who: the public Worker adds both
                    // once it has seen a signed-in Discord member.
                    {
                        string want = Announce.Token;
                        string got = req.Headers["X-Announce-Token"] ?? "";
                        res.ContentType = "application/json";
                        if (req.HttpMethod != "POST" || want == null || got != want)
                        {
                            res.StatusCode = 403;
                            textBytes = Encoding.UTF8.GetBytes("{\"error\":\"forbidden\"}");
                            res.ContentLength64 = textBytes.Length;
                            res.Close(textBytes, true);
                            return true;
                        }
                        string body;
                        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                            body = sr.ReadToEnd();
                        string who = req.Headers["X-User"] ?? "";
                        try { who = Uri.UnescapeDataString(who); } catch { }
                        string err = Features.ParseBody(body, out string id, out string name)
                            ? Features.SetName(id, name, who) : "expected {\"id\":..,\"name\":..}";
                        res.StatusCode = err == null ? 200 : 400;
                        textBytes = Encoding.UTF8.GetBytes(err == null
                            ? "{\"ok\":true,\"rev\":" + Features.Rev + "}"
                            : "{\"error\":\"" + JsonEscape(err) + "\"}");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                case "/announce":
                    // Shared-secret only, and deliberately absent from the public
                    // Worker's allowlist: this writes into everyone's chat.
                    {
                        string want = Announce.Token;
                        string got = req.Headers["X-Announce-Token"] ?? "";
                        if (want == null || got != want)
                        {
                            res.StatusCode = 403;
                            textBytes = Encoding.UTF8.GetBytes("{\"error\":\"forbidden\"}");
                            res.ContentType = "application/json";
                            res.ContentLength64 = textBytes.Length;
                            res.Close(textBytes, true);
                            return true;
                        }
                        string body;
                        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                            body = sr.ReadToEnd();
                        body = (body ?? "").Trim();
                        if (body.Length == 0)
                        {
                            res.StatusCode = 400;
                            textBytes = Encoding.UTF8.GetBytes("{\"error\":\"empty\"}");
                            res.ContentType = "application/json";
                            res.ContentLength64 = textBytes.Length;
                            res.Close(textBytes, true);
                            return true;
                        }
                        Announce.Enqueue(body);
                        res.ContentType = "application/json";
                        res.StatusCode = 202;
                        textBytes = Encoding.UTF8.GetBytes("{\"queued\":true}");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                case "/pins":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "text/csv";
                    res.StatusCode = 200;
                    string text;
                    lock (pins) text = string.Join("\n", pins);
                    textBytes = Encoding.UTF8.GetBytes(text);
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
            }

            return false;
        }

        // Game thread only. Rebuilds what every other thread serves.
        public void RefreshPlayerSnapshot()
        {
            try
            {
                playersWs = BuildPlayerResponse();
                playersJson = BuildPlayersJson();
                foreach (var player in players)
                {
                    ZDO z = null;
                    try { z = ZDOMan.instance.GetZDO(player.m_characterID); } catch { }
                    if (z == null) continue;
                    long pid = 0L;
                    try { pid = z.GetLong(ZDOVars.s_playerID, 0L); } catch { }
                    Stats.Seen(player.m_playerName, pid, z.GetPosition());
                    Trails.Mark(pid != 0L ? pid : player.m_playerName.GetHashCode(), z.GetPosition());
                }
                Stats.MaybeSave();
                Trails.MaybeSave();
            }
            catch (Exception ex)
            {
                if (WebMapConfig.DEBUG) ZLog.LogWarning("WebMap: player snapshot failed: " + ex.Message);
            }
        }

        public void Reload()
        {
            forceReload = true;
        }

        public void ListenAsync()
        {
            httpServer.Start();

            if (httpServer.IsListening)
                ZLog.Log($"WebMap: HTTP Server Listening on port {SERVER_PORT}");
            else
                ZLog.LogError("WebMap: HTTP Server Failed To Start !!!");
        }

        public void BroadcastPing(long id, string name, Vector3 position)
        {
            webSocketHandler.Sessions.Broadcast($"ping\n{id}\n{name}\n{FixedValue(position.x)},{FixedValue(position.z)}");
        }

        // Pins by the character name in each CSV line, for the player tallies.
        public Dictionary<string, int> PinsByName()
        {
            var d = new Dictionary<string, int>();
            lock (pins)
                foreach (string line in pins)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 4) continue;
                    d.TryGetValue(parts[3], out int n);
                    d[parts[3]] = n + 1;
                }
            return d;
        }

        public void AddPin(string id, string pinId, string type, string name, Vector3 position, string pinText)
        {
            lock (pins) pins.Add($"{id},{pinId},{type},{name},{FixedValue(position.x)},{FixedValue(position.z)},{pinText}");
            webSocketHandler.Sessions.Broadcast(
                $"pin\n{id}\n{pinId}\n{type}\n{name}\n{FixedValue(position.x)},{FixedValue(position.z)}\n{pinText}");
        }

        public void RemovePin(int idx)
        {
            string[] pinParts;
            lock (pins)
            {
                pinParts = pins[idx].Split(',');
                pins.RemoveAt(idx);
            }
            webSocketHandler.Sessions.Broadcast($"rmpin\n{pinParts[1]}");
        }

        // Newest first, keep `max` of this kind, drop the rest. Caller holds messageLock.
        private static void TrimToDepth(List<MapMessage> list, int ev, int max)
        {
            int n = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].ev != ev) continue;
                if (++n > max) { list.RemoveAt(i); n--; }
            }
        }

        public void AddMessage(long id, int type, string name, string message, bool ev = false)
        {
            lock (messageLock) newMessages.Add(new MapMessage(id, type, name, message, ev));
        }

        private static string FixedValue(float f)
        {
            return f.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
