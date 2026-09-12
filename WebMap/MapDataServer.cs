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

        public MapMessage(long id, int type, string name, string message)
        {
            this.id = id;
            this.type = type;
            this.name = name;
            this.message = message;
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
        public Texture2D fogTexture;
        private readonly HttpServer httpServer;

        public byte[] mapImageData;
        private byte[] mapJpgCache;          // built once; the world render never changes

        // Must run on the main thread: a Texture2D cannot be created from the HTTP
        // thread. Called right after the world render is built or loaded.
        public void BuildMapJpg()
        {
            if (mapJpgCache != null) return;
            if (mapImageData == null || mapImageData.Length == 0) return;
            try
            {
                int size = WebMapConfig.TEXTURE_SIZE;
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
                                if (WebMapConfig.MAX_MESSAGES < sentMessages.Count) sentMessages.RemoveAt(0);
                                tosend.Add(message.ToJson());
                                sentMessages.Add(message);
                            });
                            newMessages.Clear();
                            newMessages.TrimExcess();
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
                    int maxHealth = (int)Math.Ceiling(zdoData.GetFloat("max_health", 25));
                    int health = (int)Math.Ceiling(zdoData.GetFloat("health", maxHealth));
                    int dead = zdoData.GetBool("dead") ? 1 : 0;
                    int pvp = zdoData.GetBool("pvp") ? 1 : 0;
                    int inbed = zdoData.GetBool("inBed") ? 1 : 0;

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
                int maxHealth = (int)Math.Ceiling(zdoData.GetFloat("max_health", 25));
                int health = (int)Math.Ceiling(zdoData.GetFloat("health", maxHealth));
                maxHealth = Math.Max(maxHealth, health);
                bool hidden = !player.m_publicRefPos;
                bool showPos = player.m_publicRefPos || WebMapConfig.ALWAYS_VISIBLE;

                var sb = new StringBuilder();
                sb.Append("{\"name\":\"").Append(JsonEscape(player.m_playerName)).Append("\"");
                sb.Append(",\"health\":").Append(health);
                sb.Append(",\"maxHealth\":").Append(maxHealth);
                sb.Append(",\"dead\":").Append(zdoData.GetBool("dead") ? "true" : "false");
                sb.Append(",\"inBed\":").Append(zdoData.GetBool("inBed") ? "true" : "false");
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

            string rawRequestPath = req.RawUrl;
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
                if (!fileCache.TryGetValue(requestedFile, out requestedFileBytes))
                {
                    requestedFileBytes = new byte[0];
                    string filePath = Path.Combine(publicRoot, requestedFile);
                    try
                    {
                        requestedFileBytes = File.ReadAllBytes(filePath);
                        if (CACHE_SERVER_FILES) fileCache[requestedFile] = requestedFileBytes;
                    }
                    catch (Exception ex)
                    {
                        ZLog.LogError("WebMap: FAILED TO READ FILE! " + ex.Message);
                    }
                }

                if (requestedFileBytes.Length > 0)
                {
                    res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=604800, immutable");
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
            string rawRequestPath = req.RawUrl;
            byte[] textBytes;

            switch (rawRequestPath)
            {
                case "/config":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(MakeClientConfigJson());
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
                    byte[] fogBytes = ImageConv.EncodeToPNG(fogTexture);
                    res.ContentLength64 = fogBytes.Length;
                    res.Close(fogBytes, true);
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
                    res.ContentLength64 = structureBytes.Length;
                    res.Close(structureBytes, true);
                    return true;
                case "/forest":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    byte[] forestBytes = ForestMap.GetPng();
                    res.ContentLength64 = forestBytes.Length;
                    res.Close(forestBytes, true);
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
                case "/structures/stats":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(StructureMap.GetStats());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
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
                case "/structures/refresh":
                    // Ask for a sweep; the scan itself must happen on the main thread.
                    StructureMap.RefreshRequested = true;
                    res.ContentType = "application/json";
                    res.StatusCode = 202;
                    textBytes = Encoding.UTF8.GetBytes("{\"queued\":true}");
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
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

        public void BroadcastMessage(long id, int type, string name, string message)
        {
            webSocketHandler.Sessions.Broadcast($"message\n{id}\n{type}\n{name}\n{message}");
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

        public void AddMessage(long id, int type, string name, string message)
        {
            lock (messageLock) newMessages.Add(new MapMessage(id, type, name, message));
        }

        private static string FixedValue(float f)
        {
            return f.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
