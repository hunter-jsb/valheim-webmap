using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using static ZRoutedRpc;
using Random = UnityEngine.Random;
using System.Runtime.InteropServices;
using System.Collections;
using System.Dynamic;

namespace WebMap
{
    //This attribute is required, and lists metadata for your plugin.
    //The GUID should be a unique ID for this plugin, which is human readable (as it is used in places like the config). I like to use the java package notation, which is "com.[your name here].[your plugin name here]"
    //The name is the name of the plugin that's displayed on load, and the version number just specifies what version the plugin is.
    [BepInPlugin(GUID, NAME, VERSION)]

    //This is the main declaration of our plugin class. BepInEx searches for all classes inheriting from BaseUnityPlugin to initialize on startup.
    //BaseUnityPlugin itself inherits from MonoBehaviour, so you can use this as a reference for what you can declare and use in your plugin class: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
    public class WebMap : BaseUnityPlugin
    {
        public const string GUID = "com.github.h0tw1r3.valheim.webmap";
        public const string NAME = "WebMap";
        public const string VERSION = "2.7.1";

        private static readonly string[] ALLOWED_PINS = { "dot", "fire", "mine", "house", "cave" };

        public DiscordWebHook discordWebHook;
        public static MapDataServer mapDataServer;
        public static string worldDataPath;
        public static string mapDataPath;
        public static string pluginPath;

        public static int sayMethodHash = 0;
        public static int chatMessageMethodHash = 0;

        public static bool fogTextureNeedsSaving;

        public static string currentWorldName;
        public static Dictionary<string, object> serverInfo;

        private static Harmony harmony;

        public static WebMap instance;

        //The Awake() method is run at the very start when the game is initialized.
        public void Awake()
        {
            instance = this;
            harmony = new Harmony(GUID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            mapDataPath = Path.Combine(pluginPath ?? string.Empty, "map_data");
            Directory.CreateDirectory(mapDataPath);

            WebMapConfig.ReadConfigFile(Config);

            discordWebHook = new DiscordWebHook(WebMapConfig.DISCORD_WEBHOOK);
        }

        public void OnDestroy()
        {
             Config.Save();
        }

        public void Online()
        {
            StaticCoroutine.Start(SaveFogTextureLoop());
            StaticCoroutine.Start(UpdateFogTextureLoop());
            StaticCoroutine.Start(StructureMap.Loop());
            StaticCoroutine.Start(Announce.Pump());
            StaticCoroutine.Start(PlayerSnapshotLoop());
            NotifyOnline();
        }

        public void SetServerInfo(bool openServer, bool publicServer, string serverName, string password, string worldName, string worldSeed)
        {
            serverInfo = new Dictionary<string, object>();
            serverInfo.Add("openServer", openServer);
            serverInfo.Add("publicServer", publicServer);
            serverInfo.Add("serverName", serverName);
            serverInfo.Add("password", password);
            serverInfo.Add("worldName", worldName);
            serverInfo.Add("worldSeed", worldSeed);
        }

        public void NotifyOnline()
        {
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** is *online* 🟢\n💻 {AccessTools.Method(typeof(ZNet), "GetServerIP").Invoke(ZNet.instance, new object[] { })}:{ZNet.instance.GetHostPort()}\n🔑 {serverInfo["password"]}\n🗺 {WebMapConfig.URL}");
        }

        public void NotifyOffline()
        {
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** is *offline* 🔴");
        }

        public void NotifyJoin(ZNetPeer peer)
        {
            string message = $"player _{peer.m_playerName}_ joined";
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** {message}");
            mapDataServer.AddMessage(peer.m_uid, (int)Talker.Type.Normal, "Server", message);
        }

        public void NotifyLeave(ZNetPeer peer)
        {
            string message = $"player _{peer.m_playerName}_ left";
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** {message}");
            // MessageHud is a client HUD; on a dedicated server instance is null, so
            // this threw on every disconnect. Announce.Enqueue reaches players properly.
            Announce.Enqueue(message);
            mapDataServer.AddMessage(peer.m_uid, (int)Talker.Type.Normal, "Server", message);
        }

        public void NewWorld()
        {
            string worldName = WebMapConfig.GetWorldName();
            bool forceReload = (currentWorldName != worldName);

            worldDataPath = Path.Combine(mapDataPath, WebMapConfig.GetWorldName());
            Directory.CreateDirectory(worldDataPath);

            if (mapDataServer == null)
            {
                ZLog.Log($"WebMap: loading existing world: #{worldName}");
                mapDataServer = new MapDataServer();
            }
            else if (forceReload)
            {
                ZLog.Log($"WebMap: loading a new world! old: #{currentWorldName} new: #{worldName}");
            }

            currentWorldName = worldName;

            string mapImagePath = Path.Combine(worldDataPath, "map.png");
            try
            {
                byte[] png = File.ReadAllBytes(mapImagePath);
                // IHDR width sits at bytes 16..19; no need to decode to know the size.
                int width = png.Length > 24 ? (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19] : 0;
                if (width == WebMapConfig.RENDER_SIZE)
                {
                    mapDataServer.mapImageData = png;
                    mapDataServer.BuildMapJpg();
                }
                else
                {
                    ZLog.Log($"WebMap: map.png is {width}px, render_size is {WebMapConfig.RENDER_SIZE}: rebuilding");
                }
            }
            catch (Exception e)
            {
                ZLog.LogError("WebMap: Failed to read map image data from disk. " + e.Message);
            }

            string fogImagePath = Path.Combine(worldDataPath, "fog.png");
            try
            {
                Texture2D fogTexture = new Texture2D(WebMapConfig.TEXTURE_SIZE, WebMapConfig.TEXTURE_SIZE);
                byte[] fogBytes = File.ReadAllBytes(fogImagePath);
                ImageConv.LoadImage(fogTexture, fogBytes);
                mapDataServer.fogTexture = fogTexture;
            }
            catch (Exception e)
            {
                ZLog.LogWarning("WebMap: Failed to read fog image data from disk... Making new fog image..." + e.Message);
                Texture2D fogTexture = new Texture2D(WebMapConfig.TEXTURE_SIZE, WebMapConfig.TEXTURE_SIZE,
                    TextureFormat.R8, false);
                Color32[] fogColors = new Color32[WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE];
                for (int t = 0; t < fogColors.Length; t++) fogColors[t] = Color.black;

                fogTexture.SetPixels32(fogColors);
                byte[] fogPngBytes = ImageConv.EncodeToPNG(fogTexture);

                mapDataServer.fogTexture = fogTexture;
                try
                {
                    File.WriteAllBytes(fogImagePath, fogPngBytes);
                }
                catch (Exception ex)
                {
                    ZLog.LogError("WebMap: FAILED TO WRITE FOG FILE! " + ex.Message);
                }
            }

            string mapPinsFile = Path.Combine(worldDataPath, "pins.csv");
            try
            {
                string[] pinsLines = File.ReadAllLines(mapPinsFile);
                mapDataServer.pins = new List<string>(pinsLines);
            }
            catch (Exception e)
            {
                ZLog.LogError("WebMap: Failed to read pins.csv from disk. " + e.Message);
            }

            if (forceReload)
            {
                mapDataServer.Reload();
            }
        }

        // The broadcast timer and every HTTP request used to read ZDOs and ZNet's
        // peer list themselves, from whatever thread they happened to be on. They
        // read what this builds instead.
        public IEnumerator PlayerSnapshotLoop()
        {
            while (true)
            {
                mapDataServer.RefreshPlayerSnapshot();
                yield return new WaitForSeconds(WebMapConfig.PLAYER_UPDATE_INTERVAL);
            }
        }

        public IEnumerator UpdateFogTextureLoop()
        {
            while(true)
            {
                yield return new WaitForSeconds(WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL);
                UpdateFogTexture();
            }
        }

        public void UpdateFogTexture()
        {
            int pixelExploreRadius = (int)Mathf.Ceil(WebMapConfig.EXPLORE_RADIUS / WebMapConfig.PIXEL_SIZE);
            int pixelExploreRadiusSquared = pixelExploreRadius * pixelExploreRadius;
            int halfTextureSize = WebMapConfig.TEXTURE_SIZE / 2;

            mapDataServer.players.ForEach(player =>
            {
                if (player.m_publicRefPos || WebMapConfig.ALWAYS_MAP || WebMapConfig.ALWAYS_VISIBLE)
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
                        int pixelX = Mathf.RoundToInt(pos.x / WebMapConfig.PIXEL_SIZE + halfTextureSize);
                        int pixelY = Mathf.RoundToInt(pos.z / WebMapConfig.PIXEL_SIZE + halfTextureSize);
                        for (int y = pixelY - pixelExploreRadius; y <= pixelY + pixelExploreRadius; y++)
                        {
                            for (int x = pixelX - pixelExploreRadius; x <= pixelX + pixelExploreRadius; x++)
                                if (y >= 0 && x >= 0 && y < WebMapConfig.TEXTURE_SIZE &&
                                    x < WebMapConfig.TEXTURE_SIZE)
                                {
                                    int xDiff = pixelX - x;
                                    int yDiff = pixelY - y;
                                    int currentExploreRadiusSquared = xDiff * xDiff + yDiff * yDiff;
                                    if (currentExploreRadiusSquared < pixelExploreRadiusSquared)
                                    {
                                        Color fogTexColor = mapDataServer.fogTexture.GetPixel(x, y);
                                        if (fogTexColor != Color.white)
                                        {
                                            if (WebMapConfig.DEBUG && !fogTextureNeedsSaving) ZLog.Log("Fog needs saving");
                                            fogTextureNeedsSaving = true;
                                            mapDataServer.fogPngStale = true;
                                            mapDataServer.fogTexture.SetPixel(x, y, Color.white);
                                        }
                                    }
                                }
                        }
                    }
                }
            });
        }

        public IEnumerator SaveFogTextureLoop()
        {
            while(true)
            {
                yield return new WaitForSeconds(WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL);
                SaveFogTexture();
            }
        }

        public void SaveFogTexture()
        {
            if (mapDataServer.players.Count > 0 && fogTextureNeedsSaving)
            {
                byte[] pngBytes = mapDataServer.GetFogPng();

                if (WebMapConfig.DEBUG) ZLog.Log("Saving Fog");

                try
                {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "fog.png"), pngBytes);
                    fogTextureNeedsSaving = false;
                }
                catch (Exception e)
                {
                    ZLog.LogError("WebMap: FAILED TO WRITE FOG FILE! " + e.Message);
                }
            }
        }

        public static void SavePins()
        {
            string mapPinsFile = Path.Combine(worldDataPath, "pins.csv");
            try
            {
                File.WriteAllLines(mapPinsFile, mapDataServer.pins);
            }
            catch (Exception e)
            {
                ZLog.Log("WebMap: FAILED TO WRITE PINS FILE! " + e.Message);
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
        private class ZoneSystemPatch
        {
            private static readonly Color DeepWaterColor = new Color(0.36105883f, 0.36105883f, 0.43137255f);
            private static readonly Color ShallowWaterColor = new Color(0.574f, 0.50709206f, 0.47892025f);
            private static readonly Color ShoreColor = new Color(0.1981132f, 0.12241901f, 0.1503943f);

            private static Color GetMaskColor(float wx, float wy, float height, Heightmap.Biome biome)
            {
                Color noForest = new Color(0f, 0f, 0f, 0f);
                Color forest = new Color(1f, 0f, 0f, 0f);

                if (height < ZoneSystem.instance.m_waterLevel) return noForest;

                if (biome == Heightmap.Biome.Meadows)
                {
                    if (!WorldGenerator.InForest(new Vector3(wx, 0f, wy))) return noForest;

                    return forest;
                }

                if (biome == Heightmap.Biome.Plains)
                {
                    if (WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wy)) >= 0.8f) return noForest;

                    return forest;
                }

                if (biome == Heightmap.Biome.BlackForest || biome == Heightmap.Biome.Mistlands) return forest;

                return noForest;
            }

            private static Color GetPixelColor(Heightmap.Biome biome)
            {
                Color m_meadowsColor = new Color(0.573f, 0.655f, 0.361f);
                Color m_swampColor = new Color(0.639f, 0.447f, 0.345f);
                Color m_mountainColor = new Color(1f, 1f, 1f);
                Color m_blackforestColor = new Color(0.420f, 0.455f, 0.247f);
                Color m_heathColor = new Color(0.906f, 0.671f, 0.470f);
                Color m_ashlandsColor = new Color(0.690f, 0.192f, 0.192f);
                Color m_deepnorthColor = new Color(1f, 1f, 1f);
                Color m_mistlandsColor = new Color(0.36f, 0.22f, 0.4f);

                switch (biome)
                {
                    case Heightmap.Biome.Meadows:
                        return m_meadowsColor;
                    case Heightmap.Biome.Swamp:
                        return m_swampColor;
                    case Heightmap.Biome.Mountain:
                        return m_mountainColor;
                    case Heightmap.Biome.BlackForest:
                        return m_blackforestColor;
                    case Heightmap.Biome.Plains:
                        return m_heathColor;
                    case Heightmap.Biome.AshLands:
                        return m_ashlandsColor;
                    case Heightmap.Biome.DeepNorth:
                        return m_deepnorthColor;
                    case Heightmap.Biome.Ocean:
                        return Color.white;
                    case Heightmap.Biome.Mistlands:
                        return m_mistlandsColor;
                    default:
                        return Color.white;
                }
            }

            private static void Postfix(ZoneSystem __instance)
            {
                WebMap.instance.NewWorld();

                if (mapDataServer.mapImageData != null)
                {
                    ZLog.Log("WebMap: MAP ALREADY BUILT!");
                    return;
                }

                ZLog.Log("WebMap: BUILD MAP!");
                StaticCoroutine.Start(BuildMap());
            }

            // The terrain picture. Sampled at RENDER_SIZE, which may be finer than the
            // overlays' TEXTURE_SIZE; it covers the same area, so the pitch is scaled
            // to match. Yields as it goes: at 4096 this is 16M samples and used to
            // freeze the server for the duration.
            private static IEnumerator BuildMap()
            {
                int R = WebMapConfig.RENDER_SIZE;
                float rp = WebMapConfig.PIXEL_SIZE * (float)WebMapConfig.TEXTURE_SIZE / R;
                float step = 2f * rp / WebMapConfig.PIXEL_SIZE;   // 2f at 2048: the original look

                int num = R / 2;
                float num2 = rp / 2f;
                Color mask;
                Color32[] colorArray = new Color32[R * R];
                float[] heightArray = new float[R * R];
                for (int i = 0; i < R; i++)
                {
                    yield return null;                       // one row per frame: never freeze the server for this
                    for (int j = 0; j < R; j++)
                    {
                        float wx = (float)(j - num) * rp + num2;
                        float wy = (float)(i - num) * rp + num2;
                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(wx, wy);
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy, out mask);
                        colorArray[i * R + j] = GetPixelColor(biome);
                        heightArray[i * R + j] = biomeHeight;
                    }
                }

                float waterLevel = ZoneSystem.instance.m_waterLevel;
                Vector3 sunDir = new Vector3(-0.57735f, 0.57735f, 0.57735f);
                Color[] newColors = new Color[colorArray.Length];

                for (int t = 0; t < colorArray.Length; t++)
                {
                    if ((t & 0x3FFFF) == 0) yield return null;
                    float h = heightArray[t];

                    int tUp = t - R;
                    if (tUp < 0) tUp = t;

                    int tDown = t + R;
                    if (tDown > colorArray.Length - 1) tDown = t;

                    int tRight = t + 1;
                    if (tRight > colorArray.Length - 1) tRight = t;

                    int tLeft = t - 1;
                    if (tLeft < 0) tLeft = t;

                    float hUp = heightArray[tUp];
                    float hRight = heightArray[tRight];
                    float hLeft = heightArray[tLeft];
                    float hDown = heightArray[tDown];

                    Vector3 va = new Vector3(step, 0f, hRight - hLeft).normalized;
                    Vector3 vb = new Vector3(0f, step, hUp - hDown).normalized;
                    Vector3 normal = Vector3.Cross(va, vb);

                    float surfaceLight = Vector3.Dot(normal, sunDir) * 0.25f + 0.75f;

                    float shoreMask = Mathf.Clamp(h - waterLevel, 0, 1);
                    float shallowRamp = Mathf.Clamp((h - waterLevel + 0.2f * 12.5f) * 0.5f, 0, 1);
                    float deepRamp = Mathf.Clamp((h - waterLevel + 1f * 12.5f) * 0.1f, 0, 1);

                    Color32 mapColor = colorArray[t];
                    Color ans = Color.Lerp(ShoreColor, mapColor, shoreMask);
                    ans = Color.Lerp(ShallowWaterColor, ans, shallowRamp);
                    ans = Color.Lerp(DeepWaterColor, ans, deepRamp);

                    newColors[t] = new Color(ans.r * surfaceLight, ans.g * surfaceLight, ans.b * surfaceLight, ans.a);
                }

                Texture2D newTexture = new Texture2D(R, R,
                    TextureFormat.RGBA32, false);
                newTexture.SetPixels(newColors);
                byte[] pngBytes = ImageConv.EncodeToPNG(newTexture);

                mapDataServer.mapImageData = pngBytes;
                    mapDataServer.BuildMapJpg();
                try
                {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "map.png"), pngBytes);
                    ZLog.Log("WebMap: BUILDING MAP DONE!");
                }
                catch (Exception e)
                {
                    ZLog.LogError("WebMap: FAILED TO WRITE MAP FILE! " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Load))]
        private class ZoneSystemLoadPatch
        {
            private static void Postfix()
            {
                ZoneSystem.LocationInstance startLocation;
                if (ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out startLocation))
                {
                    var p = startLocation.m_position;
                    WebMapConfig.WORLD_START_POS = p;
                    ZLog.Log("WebMap: starting point " + WebMapConfig.WORLD_START_POS.ToString());
                }
                else
                {
                    ZLog.LogError("WebMap: failed to find starting point");
                }

                WebMap.instance.Online();

                mapDataServer.ListenAsync();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        private class ZNetPatchStart
        {
            private static void Postfix(List<ZNetPeer> ___m_peers)
            {
                mapDataServer.players = ___m_peers;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private class ZNetPatchShutdown
        {
            private static void Postfix()
            {
                mapDataServer.Stop();
                WebMap.instance.NotifyOffline();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
        private class ZNetPatchSetServer
        {
            private static void Postfix(bool server, bool openServer, bool publicServer, string serverName, string password, World world)
            {
                WebMap.instance.SetServerInfo(openServer, publicServer, serverName, password, world.m_name, world.m_seedName);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private class ZNetPatchDisconnect
        {
            private static void Prefix(ref ZNetPeer peer)
            {
                if (!peer.m_server && !string.IsNullOrEmpty(peer.m_playerName))
                {
                    WebMap.instance.NotifyLeave(peer);
                }
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.AddPeer))]
        private class ZRoutedRpcAddPeerPatch
        {
            private static void Postfix(ZNetPeer peer)
            {
                if (!peer.m_server && !string.IsNullOrEmpty(peer.m_playerName))
                {
                    WebMap.instance.NotifyJoin(peer);
                }
            }
        }

        // Chat in 1.0 is addressed to each recipient, not broadcast, so the server's
        // HandleRoutedRPC never sees it:
        //     if (target == m_id || target == 0) HandleRoutedRPC(data);
        //     if (m_server && target != m_id)    RouteRPC(data);
        // RouteRPC is the branch a player-to-player message takes, and the server runs
        // it while forwarding. Observing there needs no fake server player, so it stays
        // clear of the join path that patch breaks.
        //
        // A shout is sent once per recipient, so the same message arrives N times and
        // has to be de-duplicated. Everybody-targeted RPCs (pings) are skipped, since
        // HandleRoutedRPC already reports those.
        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        private class ZRoutedRpcRoutePatch
        {
            private static readonly Dictionary<string, float> recent = new Dictionary<string, float>();

            private static readonly int SayHash = "Say".GetStableHashCode();
            private static readonly int ChatHash = "ChatMessage".GetStableHashCode();

            private static void Prefix(ref ZRoutedRpc __instance, RoutedRPCData rpcData)
            {
                if (rpcData == null || rpcData.m_targetPeerID == 0L) return;
                // Decide whether this is chat before doing anything else. The server
                // forwards every routed RPC between players through here -- thousands
                // a second with a few people on -- and hashing each one's body to
                // de-duplicate it was a per-RPC tax on the game thread. Only a chat
                // shout needs de-duplicating, since it arrives once per recipient.
                int h = rpcData.m_methodHash;
                if (h != SayHash && h != ChatHash) return;
                try
                {
                    if (IsDuplicate(rpcData)) return;
                    RoutedRPCData data = rpcData;
                    ZRoutedRpcPatch.Observe(ref __instance, ref data);
                }
                catch (Exception ex)
                {
                    ZLog.LogWarning("WebMap: failed observing a routed rpc: " + ex);
                }
            }

            private static bool IsDuplicate(RoutedRPCData d)
            {
                byte[] body = d.m_parameters != null ? d.m_parameters.GetArray() : null;
                uint h = 2166136261u;
                if (body != null)
                    foreach (byte b in body) { h ^= b; h *= 16777619u; }
                string key = d.m_senderPeerID + ":" + d.m_methodHash + ":" + h;
                float now = Time.realtimeSinceStartup;
                if (recent.TryGetValue(key, out float seen) && now - seen < 2f) return true;
                recent[key] = now;
                if (recent.Count > 256)
                {
                    var stale = new List<string>();
                    foreach (var kv in recent) if (now - kv.Value > 10f) stale.Add(kv.Key);
                    foreach (var k in stale) recent.Remove(k);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC))]
        private class ZRoutedRpcPatch
        {
            private static void Postfix(ref ZRoutedRpc __instance, ref RoutedRPCData data) => Observe(ref __instance, ref data);

            internal static void Observe(ref ZRoutedRpc __instance, ref RoutedRPCData data)
            {
                // Since this also runs from the RouteRPC prefix it sees every routed
                // RPC the server forwards, not just the few addressed to it -- so
                // decide whether this is chat before doing any work. The peer lookup
                // below used to run for all of them, and threw and caught a
                // NullReferenceException each time the sender was not a live peer.
                int hash = data?.m_methodHash ?? 0;
                if (hash == 0) return;          // no data: must not match the caches, which start at 0
                bool isSay = hash == sayMethodHash || hash == "Say".GetStableHashCode();
                bool isChat = hash == chatMessageMethodHash || hash == "ChatMessage".GetStableHashCode();
                if (!isSay && !isChat)
                {
                    if (WebMapConfig.DEBUG) ZLog.Log("RoutedRPC: " + hash);
                    return;
                }

                ZNetPeer peer = ZNet.instance.GetPeer(data.m_senderPeerID);
                string steamid = "";
                if (peer != null)
                {
                    try
                    {
                        steamid = peer.m_rpc.GetSocket().GetHostName();
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (isSay)
                {
                    sayMethodHash = data.m_methodHash;
                    try
                    {
                        ZDO zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        Vector3 pos = zdoData.GetPosition();
                        // A copy: RouteRPC still has to forward this packet onward,
                        // so the original's read position must not move.
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());
                        var messageType = package.ReadInt();
                        var userInfo = new UserInfo();
                        userInfo.Deserialize(ref package);
                        string message = package.ReadString() ?? "";
                        message = message.Trim();

                        if (message.ToUpper().StartsWith("!PIN"))
                        {
                            string[] messageParts = message.Split(' ');
                            string pinType = "dot";
                            int startIdx = 1;
                            if (messageParts.Length > 1 && Array.Exists(ALLOWED_PINS, e => e == messageParts[1].ToLower()))
                            {
                                pinType = messageParts[1].ToLower();
                                startIdx = 2;
                            }

                            string pinText = "";
                            if (startIdx < messageParts.Length)
                                pinText = string.Join(" ", messageParts, startIdx, messageParts.Length - startIdx);

                            if (pinText.Length > 20) pinText = pinText.Substring(0, 20);

                            string safePinsText = Regex.Replace(pinText, "[^a-zA-Z0-9 ]", "");

                            long timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

                            string pinId = $"{timestamp}-{Random.Range(1000, 9999)}";
                            mapDataServer.AddPin(steamid, pinId, pinType, userInfo.Name, pos, safePinsText);

                            List<string> usersPins = mapDataServer.pins.FindAll(pin => pin.StartsWith(steamid));
                            int numOverflowPins = usersPins.Count - WebMapConfig.MAX_PINS_PER_USER;
                            for (int t = numOverflowPins; t > 0; t--)
                            {
                                int pinIdx = mapDataServer.pins.FindIndex(pin => pin.StartsWith(steamid));
                                mapDataServer.RemovePin(pinIdx);
                            }

                            SavePins();
                        }
                        else if (message.ToUpper().StartsWith("!UNDOPIN"))
                        {
                            int pinIdx = mapDataServer.pins.FindLastIndex(pin => pin.StartsWith(steamid));
                            if (pinIdx > -1)
                            {
                                mapDataServer.RemovePin(pinIdx);
                                SavePins();
                            }
                        }
                        else if (message.ToUpper().StartsWith("!DELETEPIN"))
                        {
                            string[] messageParts = message.Split(' ');
                            string pinText = "";
                            if (messageParts.Length > 1)
                                pinText = string.Join(" ", messageParts, 1, messageParts.Length - 1);

                            int pinIdx = mapDataServer.pins.FindLastIndex(pin =>
                            {
                                string[] pinParts = pin.Split(',');
                                return pinParts[0] == steamid && pinParts[pinParts.Length - 1] == pinText;
                            });

                            if (pinIdx > -1)
                            {
                                mapDataServer.RemovePin(pinIdx);
                                SavePins();
                            }
                        }
                        else
                        {
                            if (messageType != (int)Talker.Type.Whisper)
                            {
                                mapDataServer.AddMessage(data.m_senderPeerID, messageType, userInfo.Name, message);
                            }
                            // one console line per chat message and per ping is spam on a
                            // busy server; the web feed is where these are meant to be read
                            if (WebMapConfig.DEBUG)
                                ZLog.Log($"WebMap: (say) {pos} | {messageType} | {userInfo.Name} | {message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ZLog.LogWarning("WebMap: failed handling a chat message: " + ex);
                    }
                }
                else
                {
                    chatMessageMethodHash = data.m_methodHash;
                    try
                    {
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());
                        Vector3 pos = package.ReadVector3();
                        var messageType = package.ReadInt();
                        var userInfo = new UserInfo();
                        userInfo.Deserialize(ref package);

                        if (messageType == (int)Talker.Type.Ping)
                        {
                            mapDataServer.BroadcastPing(data.m_senderPeerID, userInfo.Name, pos);
                            if (WebMapConfig.DEBUG)
                                ZLog.Log($"WebMap: (ping) {pos} | {messageType} | {userInfo.Name}");
                        }
                        else
                        {
                            var message = package.ReadString() ?? "";
                            message = message.Trim();

                            mapDataServer.AddMessage(data.m_senderPeerID, messageType, userInfo.Name, message);
                            if (WebMapConfig.DEBUG)
                                ZLog.Log($"WebMap: (chat) {pos} | {messageType} | {userInfo.Name} | {message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (WebMapConfig.DEBUG) ZLog.LogError(ex.ToString());
                    }
                }
            }
        }
    }

    public class StaticCoroutine {
        private static StaticCoroutineRunner runner;

        public static Coroutine Start(IEnumerator coroutine) {
            EnsureRunner();
            return runner.StartCoroutine(coroutine);
        }

        private static void EnsureRunner() {
            if (runner == null) {
                runner = new GameObject("[Static Coroutine Runner]").AddComponent<StaticCoroutineRunner>();
                UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            }
        }

        private class StaticCoroutineRunner : MonoBehaviour { }
    }
}
