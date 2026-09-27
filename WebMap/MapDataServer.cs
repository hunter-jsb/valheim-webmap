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
        // Every kind of file under web/: anything else answers 404, so a new asset type needs a row here.
        internal static readonly Dictionary<string, string> contentTypes = new Dictionary<string, string> {
            {"html", "text/html"},
            {"js", "text/javascript"},
            {"css", "text/css"},
            {"png", "image/png"},
            {"jpg", "image/jpeg"},
            {"webp", "image/webp"},
            {"woff2", "font/woff2"},         // web/fonts, the brand's face
            {"txt", "text/plain"}
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
            MapFog.RebuildChunks(b);
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
            "/forest/stats", "/structures/stats", "/state", "/objects", "/height", "/prefabs"
        };
        // The game's clock for the 3D view's sky, built on the game thread with the players:
        // the day and the fraction of it EnvMan lights the world by (0.25 sunrise, 0.5 noon).
        private volatile string timeJson = "null";
        private volatile Tuple<int, byte[]> prefabsBytes;       // /prefabs as sent, per library revision
        private readonly string publicRoot;
        private readonly WebSocketServiceHost webSocketHandler;
        private static MapDataServer __instance;

        // for the tests (WebMap.Tests): the pin logic without a listening socket or a timer
        internal MapDataServer(bool forTests) { }

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
                // how they look is no spoiler of where they are: kept for their page either way
                int yaw = (int)Math.Round(zdoData.GetRotation().eulerAngles.y);
                string look = null;
                try { look = Models.RigExporter.LookJson(zdoData); } catch (Exception e) { if (WebMapConfig.DEBUG) ZLog.LogWarning("WebMap: a player's look failed: " + e.Message); }
                Stats.Looked(player.m_playerName, look, yaw);

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
                    // the 3D view draws them as the game does: facing their way, in what they wear
                    sb.Append(",\"yaw\":").Append(yaw);
                    if (look != null) sb.Append(",\"look\":").Append(look);
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

            // A path under web/, subfolders included (the vendored three.js keeps its
            // layout, which its own imports depend on). Plain names and forward slashes
            // only: a backslash is a separator on Windows and a dot-segment climbs out,
            // so either is refused, and the resolved path must still be under web/.
            string requestedFile = rawRequestPath.TrimStart('/');
            string filePath = null;
            if (requestedFile.Length > 0 && requestedFile.IndexOf('\\') < 0 && requestedFile.IndexOf(':') < 0
                && Array.TrueForAll(requestedFile.Split('/'), s => s.Length > 0 && s[0] != '.'))
            {
                try
                {
                    string rootFull = Path.GetFullPath(publicRoot) + Path.DirectorySeparatorChar;
                    string full = Path.GetFullPath(Path.Combine(publicRoot, requestedFile.Replace('/', Path.DirectorySeparatorChar)));
                    if (full.StartsWith(rootFull, StringComparison.Ordinal)) filePath = full;
                }
                catch { }
            }
            string[] fileParts = requestedFile.Split('.');
            string fileExt = fileParts[fileParts.Length - 1];

            if (filePath != null && contentTypes.ContainsKey(fileExt))
            {
                byte[] requestedFileBytes = new byte[0];
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
                    // text goes gzipped where it can: three.js is 2 MB as written, a quarter of that zipped
                    byte[] gz = null;
                    if ((fileExt == "html" || fileExt == "js" || fileExt == "css") && TakesGzip(req, requestedFileBytes))
                    {
                        if (staticGz.TryGetValue(requestedFile, out var kept) && kept.Item1 == stamp) gz = kept.Item2;
                        else staticGz[requestedFile] = Tuple.Create(stamp, gz = Gzip(requestedFileBytes));
                    }
                    Send(res, requestedFileBytes, gz, contentTypes[fileExt]);
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

            if (rawRequestPath.StartsWith("/models/", StringComparison.Ordinal) && req.HttpMethod == "GET")
            {
                ServeModel(req, res, rawRequestPath.Substring(8));
                return true;
            }

            switch (rawRequestPath)
            {
                case "/prefabs":
                    // the model library's index: which prefabs have a model, their bounds, their canopy
                    {
                        res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                        var pb = prefabsBytes;
                        if (pb == null || pb.Item1 != Models.ModelStore.Rev)
                            prefabsBytes = pb = Tuple.Create(Models.ModelStore.Rev, Encoding.UTF8.GetBytes(Models.ModelStore.PrefabsJson));
                        SendBytes(req, res, pb.Item2, "application/json");
                        return true;
                    }
                case "/objects":
                case "/height":
                    // one 256 m chunk of the 3D view, never for ground nobody has walked
                    {
                        res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                        if (!int.TryParse(req.QueryString["cx"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cx)
                            || !int.TryParse(req.QueryString["cz"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cz))
                        { Answer(res, 400, "{\"error\":\"cx and cz are chunk numbers: floor(metres / 256)\"}"); return true; }
                        if (!MapFog.ChunkExplored(cx, cz)) { Answer(res, 404, "{\"error\":\"unexplored\"}"); return true; }
                        int rev;
                        byte[] bytes;
                        if (rawRequestPath == "/objects") bytes = WorldObjects.ChunkBytes(cx, cz, out rev);
                        else
                        {
                            // step: metres between samples, for the view's far rings; 1 unless asked
                            string st = req.QueryString["step"];
                            int step = 1;
                            if (st != null && (!int.TryParse(st, NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || !Heights.ValidStep(step)))
                            { Answer(res, 400, "{\"error\":\"step is 1, 2, 4, 8 or 16\"}"); return true; }
                            try { bytes = Heights.Chunk(cx, cz, step, false, out rev); }
                            catch (Exception ex) { ZLog.LogWarning("WebMap: heights failed: " + ex.Message); bytes = null; rev = 0; }
                        }
                        if (bytes == null) { Answer(res, 503, "{\"error\":\"not ready\"}"); return true; }
                        res.Headers.Add("X-Rev", rev.ToString(CultureInfo.InvariantCulture));
                        SendBytes(req, res, bytes, "application/octet-stream");
                        return true;
                    }
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
                    // already built; this is concatenation. The tallies are not among
                    // them: no page reads them here, and /stats/players has them.
                    {
                        string pinsJson = PinsJson();
                        string state = "{\"now\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            + ",\"rev\":{\"fog\":" + fogRev + ",\"pieces\":" + Pieces.Rev + ",\"forest\":" + ForestMap.Rev
                            + ",\"structures\":" + StructureMap.Rev + ",\"chart\":" + Chart.Rev + ",\"trails\":" + Trails.Rev
                            + ",\"features\":" + Features.Rev + ",\"objects\":" + WorldObjects.Rev + ",\"height\":" + TerrainPatches.Rev
                            + ",\"models\":" + Models.ModelStore.Rev + "}"
                            + ",\"time\":" + timeJson
                            + ",\"players\":" + playersJson + ",\"messages\":" + messagesJson + ",\"pins\":" + pinsJson
                            + ",\"vehicles\":" + Vehicles.GetJson() + ",\"portals\":" + Portals.GetJson() + ",\"graves\":" + Graves.GetJson()
                            + ",\"traders\":" + Traders.Json() + ",\"deaths\":" + Stats.DeathsJson()
                            + ",\"structures\":" + StructureMap.GetStats() + ",\"forest\":" + ForestMap.GetStats() + "}";
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
                case "/settings":
                    // The settings an admin changes from the site. The Worker checks the
                    // Discord role and says so in X-Admin; the shared token guards both.
                    {
                        string want = Announce.Token, got = req.Headers["X-Announce-Token"] ?? "";
                        if (want == null || got != want || req.Headers["X-Admin"] != "1") { Answer(res, 403, "{\"error\":\"forbidden\"}"); return true; }
                        if (req.HttpMethod != "POST") { Answer(res, 200, Settings.Json()); return true; }
                        string body;
                        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                        string who = req.Headers["X-User"] ?? "";
                        try { who = Uri.UnescapeDataString(who); } catch { }
                        string log = null; bool restart = false;
                        string err = Features.ParseBody(body, out string key, out string value, "key", "value")
                            ? Settings.Set(key, value, who, out log, out restart) : "expected {\"key\":..,\"value\":..}";
                        Answer(res, err == null ? 200 : 400, err == null
                            ? "{\"ok\":true,\"restart\":" + (restart ? "true" : "false") + ",\"log\":\"" + JsonEscape(log ?? "") + "\",\"settings\":" + Settings.Json() + "}"
                            : "{\"error\":\"" + JsonEscape(err) + "\"}");
                        return true;
                    }
                case "/discord/guilds":
                    // The bot's own guilds, for the settings picker; same gate as /settings.
                    // Empty (never an error) when there is no token yet, so the picker
                    // just falls back to the plain field.
                    {
                        string want = Announce.Token, got = req.Headers["X-Announce-Token"] ?? "";
                        if (want == null || got != want || req.Headers["X-Admin"] != "1") { Answer(res, 403, "{\"error\":\"forbidden\"}"); return true; }
                        Answer(res, 200, "{\"guilds\":" + RefsJson(Discord.Guilds()) + "}");
                        return true;
                    }
                case "/discord/channels":
                    // A guild's text channels, for the same picker.
                    {
                        string want = Announce.Token, got = req.Headers["X-Announce-Token"] ?? "";
                        if (want == null || got != want || req.Headers["X-Admin"] != "1") { Answer(res, 403, "{\"error\":\"forbidden\"}"); return true; }
                        string guild = req.QueryString["guild"];
                        if (string.IsNullOrEmpty(guild)) { Answer(res, 400, "{\"error\":\"guild is required\"}"); return true; }
                        Answer(res, 200, "{\"channels\":" + RefsJson(Discord.Channels(guild)) + "}");
                        return true;
                    }
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
                        if (!SiteWrite(req, res, out string body, out string who)) return true;
                        string log = null;
                        string err = Features.ParseBody(body, out string id, out string name)
                            ? Features.SetName(id, name, who, out log) : "expected {\"id\":..,\"name\":..}";
                        Answer(res, err == null ? 200 : 400, err == null
                            ? "{\"ok\":true,\"rev\":" + Features.Rev + ",\"log\":\"" + JsonEscape(log ?? "") + "\"}"
                            : "{\"error\":\"" + JsonEscape(err) + "\"}");
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
                    // A POST places, changes or takes up a pin from the site, guarded like /names.
                    if (req.HttpMethod == "POST")
                    {
                        if (!SiteWrite(req, res, out string body, out string who)) return true;
                        string err = WritePin(body, who, out string pinId, out string log);
                        Answer(res, err == null ? 200 : 400, err == null
                            ? "{\"ok\":true,\"id\":\"" + JsonEscape(pinId) + "\",\"pins\":" + PinsJson() + ",\"log\":\"" + JsonEscape(log ?? "") + "\"}"
                            : "{\"error\":\"" + JsonEscape(err) + "\"}");
                        return true;
                    }
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

        // A write from the site: the shared token says the Worker sent it, X-User which
        // signed-in member. False when this has already answered 403.
        private static bool SiteWrite(HttpListenerRequest req, HttpListenerResponse res, out string body, out string who)
        {
            body = who = null;
            string want = Announce.Token, got = req.Headers["X-Announce-Token"] ?? "";
            if (req.HttpMethod != "POST" || want == null || got != want)
            {
                Answer(res, 403, "{\"error\":\"forbidden\"}");
                return false;
            }
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();
            who = req.Headers["X-User"] ?? "";
            try { who = Uri.UnescapeDataString(who); } catch { }
            return true;
        }

        // A body, gzipped for a client that takes it: the 3D view's chunks are arrays of
        // numbers that shrink to a third. Each body is compressed once and kept with it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> gzipped =
            new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]>();
        // A static file is read afresh on every request when cache_server_files is off, so
        // its gzip is kept by name and file time instead.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Tuple<DateTime, byte[]>> staticGz =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Tuple<DateTime, byte[]>>();
        private static bool TakesGzip(HttpListenerRequest req, byte[] body) =>
            body.Length > 1024 && (req.Headers["Accept-Encoding"] ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
        private static byte[] Gzip(byte[] raw)
        {
            using (var ms = new MemoryStream(raw.Length / 3))
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }
        private static void SendBytes(HttpListenerRequest req, HttpListenerResponse res, byte[] body, string contentType, bool compressible = true)
        {
            byte[] gz = compressible && TakesGzip(req, body) ? gzipped.GetValue(body, Gzip) : null;
            Send(res, body, gz, contentType);
        }
        private static void Send(HttpListenerResponse res, byte[] body, byte[] gz, string contentType)
        {
            if (gz != null) { body = gz; res.Headers.Add("Content-Encoding", "gzip"); }
            res.Headers.Add("Vary", "Accept-Encoding");
            res.ContentType = contentType;
            res.StatusCode = 200;
            res.ContentLength64 = body.Length;
            res.Close(body, true);
        }

        // /models/<file>: a model (<hash>.glb) or a texture (tex_<name>.png) from the
        // library, by name only. The mesh cache beside them is game data and never served.
        private static readonly System.Text.RegularExpressions.Regex ModelFile =
            new System.Text.RegularExpressions.Regex(@"^([0-9a-f]{8}\.glb|tex_[A-Za-z0-9_-]{1,120}\.png)$");
        private static void ServeModel(HttpListenerRequest req, HttpListenerResponse res, string name)
        {
            string root = Models.ModelStore.Root;
            string path = root != null && ModelFile.IsMatch(name) ? Path.Combine(root, name) : null;
            byte[] body = null;
            DateTime stamp = DateTime.MinValue;
            try { if (path != null && File.Exists(path)) { stamp = File.GetLastWriteTimeUtc(path); body = File.ReadAllBytes(path); } } catch { }
            if (body == null) { Answer(res, 404, "{\"error\":\"no such model\"}"); return; }
            // a re-export writes a new file: its time is the version
            string etag = "\"" + stamp.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";
            res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=86400");
            res.Headers.Add(HttpResponseHeader.ETag, etag);
            if (req.Headers["If-None-Match"] == etag) { res.StatusCode = 304; res.Close(); return; }
            bool glb = name.EndsWith(".glb", StringComparison.Ordinal);
            SendBytes(req, res, body, glb ? "model/gltf-binary" : "image/png", compressible: glb);
        }

        // [{"id","name"}, ...] for the settings picker's two Discord lookups.
        private static string RefsJson(List<DiscordRef> refs)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < refs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(JsonEscape(refs[i].Id)).Append("\",\"name\":\"").Append(JsonEscape(refs[i].Name)).Append("\"}");
            }
            return sb.Append(']').ToString();
        }

        private static void Answer(HttpListenerResponse res, int status, string json)
        {
            res.ContentType = "application/json";
            res.StatusCode = status;
            byte[] b = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = b.Length;
            res.Close(b, true);
        }

        public string PinsJson()
        {
            lock (pins)
            {
                var q = new List<string>(pins.Count);
                foreach (string line in pins) q.Add("\"" + JsonEscape(line) + "\"");
                return "[" + string.Join(",", q) + "]";
            }
        }

        // Pins from the site. Any signed-in member may change any pin -- one party --
        // and the log says who did what. A site pin's placer field is "web", where a
        // game pin has its player's platform id: the chat commands match on that id,
        // so they never reach one. Its id is "w" + a millisecond stamp, apart from the
        // game's "<seconds>-<random>". Null when the write took, else what was wrong.
        public const string SitePlacer = "web";
        private static readonly System.Text.RegularExpressions.Regex Unprintable =
            new System.Text.RegularExpressions.Regex(@"[\p{Cc}\p{Cf}]");
        public string WritePin(string body, string who, out string id) => WritePin(body, who, out id, out _);
        public string WritePin(string body, string who, out string id, out string log)
        {
            log = null;
            string op = Body.Str(body, "op");
            id = Body.Str(body, "id");
            // the owner is a CSV field, so no commas; 32 is Discord's own cap on a name
            who = Unprintable.Replace(who ?? "", "").Replace(",", "").Trim();
            if (who.Length > 32) who = who.Substring(0, 32).Trim();
            if (who.Length == 0) who = "someone";
            if (op != "add" && op != "edit" && op != "delete") return "op is add, edit or delete";

            string type = Body.Str(body, "type"), text = Body.Str(body, "text");
            if (type != null && Array.IndexOf(WebMap.ALLOWED_PINS, type) < 0)
                return "type is one of " + string.Join(", ", WebMap.ALLOWED_PINS);
            if (text != null)
            {
                text = Unprintable.Replace(text, "").Trim();
                if (text.Length > 60) return "a pin's text is at most 60 characters";
            }
            bool moves = op == "add" || Body.Has(body, "x") || Body.Has(body, "z");
            double x = Body.Num(body, "x"), z = Body.Num(body, "z");
            if (moves)
            {
                if (!(Math.Abs(x) <= 12000 && Math.Abs(z) <= 12000)) return "x and z are numbers within 12000 m of the centre";
                // the honour system: the site pins only ground somebody has walked
                if (!MapFog.Explored((float)x, (float)z)) return "nobody has walked there yet";
            }
            string sx = FixedValue((float)x), sz = FixedValue((float)z);

            string[] was = null, now = null;
            lock (pins)
            {
                if (op == "add")
                {
                    long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    do id = "w" + t++; while (FindPin(id) >= 0);
                    string line = $"{SitePlacer},{id},{type ?? "dot"},{who},{sx},{sz},{text ?? ""}";
                    pins.Add(line);
                    now = line.Split(',');
                }
                else
                {
                    int i = string.IsNullOrEmpty(id) ? -1 : FindPin(id);
                    if (i < 0) return "no such pin";
                    was = pins[i].Split(',');
                    if (op == "delete") pins.RemoveAt(i);
                    else
                    {
                        string line = $"{was[0]},{was[1]},{type ?? was[2]},{was[3]},{(moves ? sx : was[4])},{(moves ? sz : was[5])},{text ?? PinText(was)}";
                        pins[i] = line;
                        now = line.Split(',');
                    }
                }
            }
            WebMap.SavePins();

            // the websocket feed carries pins as they come and go; the pin is saved
            // by now, so a feed that cannot take it must not fail the write
            try
            {
                if (was != null) webSocketHandler.Sessions.Broadcast($"rmpin\n{was[1]}");
                if (now != null)
                    webSocketHandler.Sessions.Broadcast($"pin\n{now[0]}\n{now[1]}\n{now[2]}\n{now[3]}\n{now[4]},{now[5]}\n{PinText(now)}");
            }
            catch (Exception e) { if (WebMapConfig.DEBUG) ZLog.LogWarning("WebMap: pin not broadcast: " + e.Message); }

            if (op == "add")
                log = $"{who} pinned a {now[2]} '{PinText(now)}' at {sx}, {sz}";
            else if (op == "delete")
                log = $"{who} took up {was[3]}'s {was[2]} pin '{PinText(was)}'";
            else
            {
                var what = new List<string>();
                if (now[2] != was[2]) what.Add("a " + now[2]);
                if (PinText(now) != PinText(was)) what.Add($"'{PinText(now)}'");
                if (now[4] != was[4] || now[5] != was[5]) what.Add($"moved to {now[4]}, {now[5]}");
                log = $"{who} changed {was[3]}'s pin '{PinText(was)}': " + (what.Count > 0 ? string.Join(", ", what) : "no change");
            }
            ZLog.Log("WebMap: " + log + " from the site");
            Discord.Tell(log);
            return null;
        }
        // under lock (pins); a line is placer,id,type,owner,x,z,text
        private int FindPin(string id) => pins.FindIndex(l => { var f = l.Split(','); return f.Length >= 6 && f[1] == id; });
        // the text is last and may hold commas
        private static string PinText(string[] f) => f.Length > 6 ? string.Join(",", f, 6, f.Length - 6) : "";

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
                    float hp = -1f, maxHp = -1f;
                    try { maxHp = z.GetFloat(ZDOVars.s_maxHealth, -1f); hp = z.GetFloat(ZDOVars.s_health, -1f); } catch { }
                    Stats.Seen(player.m_playerName, pid, z.GetPosition(), hp, maxHp);
                    Gear.Sample(player.m_playerName, z, PLAYER_UPDATE_INTERVAL);
                    Trails.Mark(pid != 0L ? pid : player.m_playerName.GetHashCode(), z.GetPosition());
                }
                Stats.MaybeSave();
                Trails.MaybeSave();
                timeJson = BuildTimeJson();
            }
            catch (Exception ex)
            {
                if (WebMapConfig.DEBUG) ZLog.LogWarning("WebMap: player snapshot failed: " + ex.Message);
            }
        }

        // EnvMan.RescaleDayFraction's rule: the clock's 0.15..0.85 is daylight, stretched
        // to 0.25..0.75 so the sun is on the horizon at a quarter and three quarters.
        private static string BuildTimeJson()
        {
            if (ZNet.instance == null) return "null";
            double t = ZNet.instance.GetTimeSeconds();
            long len = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1200L;
            float f = (float)(t % len / len);
            if (f >= 0.15f && f <= 0.85f) f = 0.25f + (f - 0.15f) / 0.7f * 0.5f;
            else if (f < 0.5f) f = f / 0.15f * 0.25f;
            else f = 0.75f + (f - 0.85f) / 0.15f * 0.25f;
            return FormattableString.Invariant($"{{\"day\":{(long)(t / len)},\"frac\":{f:0.0000}}}");
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
