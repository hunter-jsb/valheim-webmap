using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace WebMap
{
    internal static class WebMapConfig
    {
        public static int TEXTURE_SIZE = 2048;
        public static int PIXEL_SIZE = 12;
        public static int RENDER_SIZE = 2048;
        public static float EXPLORE_RADIUS = 100f;
        public static float UPDATE_FOG_TEXTURE_INTERVAL = 2f;
        public static float SAVE_FOG_TEXTURE_INTERVAL = 30f;
        public static int MAX_PINS_PER_USER = 50;
        public static int MAX_MESSAGES = 100;
        public static bool ALWAYS_MAP = true;
        public static bool ALWAYS_VISIBLE = false;
        public static bool DEBUG = false;

        public static int SERVER_PORT = 3000;
        public static float PLAYER_UPDATE_INTERVAL = 1f;
        public static bool CACHE_SERVER_FILES = true;

        public static string WORLD_NAME = "";
        public static Vector3 WORLD_START_POS = Vector3.zero;
        public static int DEFAULT_ZOOM = 100;

        public static bool SHOW_VEHICLES = true;

        public static string ANNOUNCE_NAME = "Server";
        public static string DISCORD_WEBHOOK = "";

        // The bot side: the audit log and the two-way chat relay. Empty token turns
        // all of it off; a deployment that never sets these keeps working as before.
        public static string DISCORD_BOT_TOKEN = "";
        public static string DISCORD_GUILD = "";
        public static string DISCORD_LOG_CHANNEL = "";
        public static string DISCORD_CHAT_CHANNEL = "";
        public static bool CHAT_RELAY = true;

        // Sign-in, through the project's sign-in service: DISCORD_GUILD set turns it on
        // and gates it. Empty AUTH_URL and AUTH_PUBLIC_KEY mean the project's own, so a
        // config written by an older build follows the service if it moves.
        public static string DISCORD_ADMIN_ROLE = "";
        public static string PUBLIC_URL = "";
        public static string AUTH_URL = "";
        public static string AUTH_PUBLIC_KEY = "";
        public static long SIGN_OUT_BEFORE = 0;           // unix seconds: sessions issued earlier are refused

        public static string URL = "";

        // The 3D view's model library. The host rewrites the .cfg on every restart,
        // so these defaults are what a server actually runs with.
        public static bool EXPORT_MODELS = true;
        public static bool EXTRACT_TEXTURES = true;
        public static bool EXTRACT_MESHES = true;
        public static int EXPORT_MS_PER_FRAME = 6;
        public static int TEXTURE_MAX_SIZE = 512;
        public static string OBJECT_CATEGORIES = "piece,other,rock,bush,tree";

        public static void ReadConfigFile(ConfigFile config)
        {
            TEXTURE_SIZE = config.Bind("Texture", "texture_size",
                WebMapConfig.TEXTURE_SIZE,
                "How large is the map texture? Probably dont change this.").Value;

            PIXEL_SIZE = config.Bind("Texture", "pixel_size",
                WebMapConfig.PIXEL_SIZE,
                "How many in game units does a map pixel represent? Probably dont change this.").Value;

            RENDER_SIZE = config.Bind("Texture", "render_size",
                WebMapConfig.RENDER_SIZE,
                "Pixels across the world render (the terrain picture). Covers the same "
                + "area as texture_size, just sharper: 4096 halves the metres per pixel. "
                + "Costs a one-time render and a bigger download; the overlays stay at "
                + "texture_size, where extra resolution buys nothing.").Value;
            if (RENDER_SIZE < TEXTURE_SIZE) RENDER_SIZE = TEXTURE_SIZE;

            EXPLORE_RADIUS = config.Bind<float>("Texture", "explore_radius",
                WebMapConfig.EXPLORE_RADIUS,
                "A larger explore_radius reveals the map more quickly.").Value;

            UPDATE_FOG_TEXTURE_INTERVAL = config.Bind<float>("Interval", "update_fog_texture_interval",
                WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL,
                "How often do we update the fog texture on the server in seconds.").Value;

            SAVE_FOG_TEXTURE_INTERVAL = config.Bind<float>("Interval", "save_fog_texture_interval",
                WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL,
                "How often do we save the fog texture in seconds.").Value;

            MAX_PINS_PER_USER = config.Bind("User", "max_pins_per_user",
                WebMapConfig.MAX_PINS_PER_USER,
                "How many pins each client is allowed to make before old ones start being deleted.").Value;

            SERVER_PORT = config.Bind("Server", "server_port",
                WebMapConfig.SERVER_PORT,
                "HTTP port for the website. The map will be display on this site.").Value;

            PLAYER_UPDATE_INTERVAL = config.Bind("Interval", "player_update_interval",
                WebMapConfig.PLAYER_UPDATE_INTERVAL,
                "How often do we send position data to web browsers in seconds.").Value;

            CACHE_SERVER_FILES = config.Bind("Server", "cache_server_files",
                WebMapConfig.CACHE_SERVER_FILES,
                "Should the server cache web files to be more performant?").Value;

            DEFAULT_ZOOM = config.Bind("Texture", "default_zoom",
                WebMapConfig.DEFAULT_ZOOM,
                "How zoomed in should the web map start at? Higher is more zoomed in.").Value;

            MAX_MESSAGES = config.Bind("Server", "max_messages",
                WebMapConfig.MAX_MESSAGES,
                "How many messages to keep buffered and display to client.").Value;

            ALWAYS_MAP = config.Bind("User", "always_map",
                WebMapConfig.ALWAYS_MAP,
                "Update the map to show where hidden players have traveled.").Value;

            ALWAYS_VISIBLE = config.Bind("User", "always_visible",
                WebMapConfig.ALWAYS_VISIBLE,
                "Completely ignore the players preference to be hidden.").Value;

            DEBUG = config.Bind("Server", "debug",
                WebMapConfig.DEBUG,
                "Output debugging information.").Value;

            SHOW_VEHICLES = config.Bind("Server", "show_vehicles",
                WebMapConfig.SHOW_VEHICLES,
                "Report boats and carts at /vehicles. They are only ever reported in "
                + "territory players have already explored, but turning this off stops "
                + "the endpoint reporting anything at all.").Value;

            DISCORD_WEBHOOK = config.Bind("Server", "discord_webhook",
                WebMapConfig.DISCORD_WEBHOOK,
                "Discord webhook URL").Value;

            DISCORD_BOT_TOKEN = config.Bind("Server", "discord_bot_token",
                WebMapConfig.DISCORD_BOT_TOKEN,
                "Discord bot token (Bot ...). Empty turns off the audit log, the chat "
                + "relay and the settings picker; the webhook above keeps working either way.").Value;

            DISCORD_GUILD = config.Bind("Server", "discord_guild",
                WebMapConfig.DISCORD_GUILD,
                "Your Discord server's id (Developer Mode on, right-click the server, Copy Server ID). "
                + "Its members can sign in to the map, and its owner and administrators are the map's "
                + "admins; the settings picker lists its channels. Empty: no sign-in.").Value;

            DISCORD_ADMIN_ROLE = config.Bind("Server", "discord_admin_role",
                WebMapConfig.DISCORD_ADMIN_ROLE,
                "A role id whose members are the map's admins too, beside the server's owner and administrators.").Value;

            PUBLIC_URL = config.Bind("Server", "public_url",
                WebMapConfig.PUBLIC_URL,
                "The map's address when visitors reach it through a proxy or a name (https://map.example.com). "
                + "Empty: whatever address it is visited at.").Value;

            AUTH_URL = config.Bind("Server", "auth_url",
                WebMapConfig.AUTH_URL,
                "The sign-in service. Empty: the project's own, " + Auth.Broker + ". Only for running your own.").Value;

            AUTH_PUBLIC_KEY = config.Bind("Server", "auth_public_key",
                WebMapConfig.AUTH_PUBLIC_KEY,
                "The public key that service signs sessions with, as <modulus base64>.<exponent base64>. "
                + "Empty: the project's own.").Value;
            if (AUTH_PUBLIC_KEY.Length > 0 && Auth.Key(AUTH_PUBLIC_KEY) == null)
                ZLog.LogWarning("WebMap: auth_public_key is not an RSA key of 2048 bits or more; nobody can sign in");

            SIGN_OUT_BEFORE = config.Bind("Server", "sign_out_before",
                WebMapConfig.SIGN_OUT_BEFORE,
                "Unix seconds: sessions signed in before this are refused, so everyone signs in again. "
                + "The settings page's Sign everyone out sets it to now.").Value;

            DISCORD_LOG_CHANNEL = config.Bind("Server", "discord_log_channel",
                WebMapConfig.DISCORD_LOG_CHANNEL,
                "Channel that gets what people do on the site: names, pins, settings changes.").Value;

            DISCORD_CHAT_CHANNEL = config.Bind("Server", "discord_chat_channel",
                WebMapConfig.DISCORD_CHAT_CHANNEL,
                "Channel relayed both ways with in-game chat.").Value;

            CHAT_RELAY = config.Bind("Server", "chat_relay",
                WebMapConfig.CHAT_RELAY,
                "Relay chat between the game and discord_chat_channel.").Value;

            URL = config.Bind("Server", "webmap_url",
                WebMapConfig.URL,
                "URL to view the web map.").Value;

            EXPORT_MODELS = config.Bind("Models", "export_models",
                WebMapConfig.EXPORT_MODELS,
                "Export each prefab the world holds as a glTF model for the 3D view, a few a "
                + "frame, into map_data/models. Once per prefab; kept across restarts.").Value;

            EXTRACT_TEXTURES = config.Bind("Models", "extract_textures",
                WebMapConfig.EXTRACT_TEXTURES,
                "Read the models' textures out of the game's own asset files on a background "
                + "thread, once per game version (the dedicated server cannot read them any other "
                + "way). Off: flat colours.").Value;

            EXTRACT_MESHES = config.Bind("Models", "extract_meshes",
                WebMapConfig.EXTRACT_MESHES,
                "Read the meshes the engine keeps locked (most of them) out of the game's asset "
                + "files the same way. Off: those models are left out or drawn as boxes.").Value;

            EXPORT_MS_PER_FRAME = config.Bind("Models", "export_ms_per_frame",
                WebMapConfig.EXPORT_MS_PER_FRAME,
                "Game-thread milliseconds a frame the model export may take.").Value;

            TEXTURE_MAX_SIZE = config.Bind("Models", "texture_max_size",
                WebMapConfig.TEXTURE_MAX_SIZE,
                "Longest edge of an extracted texture, in pixels.").Value;

            OBJECT_CATEGORIES = config.Bind("Models", "object_categories",
                WebMapConfig.OBJECT_CATEGORIES,
                "What the 3D view is sent, of piece, other, rock, bush, tree (comma separated).").Value;
        }

        public static string GetWorldName()
        {
            if (ZNet.instance != null)
            {
                WORLD_NAME = ZNet.instance.GetWorldName();
            }
            else
            {
                string[] arguments = Environment.GetCommandLineArgs();
                string worldName = "";
                for (int t = 0; t < arguments.Length; t++)
                    if (arguments[t] == "-world")
                    {
                        worldName = arguments[t + 1];
                        break;
                    }
                WORLD_NAME = worldName;
            }
            return WORLD_NAME;
        }

        public static string MakeClientConfigJson()
        {
            Dictionary<string, object> config = new Dictionary<string, object>();

            config["world_name"] = GetWorldName();
            config["world_start_pos"] = WORLD_START_POS;
            config["default_zoom"] = DEFAULT_ZOOM;
            config["texture_size"] = TEXTURE_SIZE;
            config["pixel_size"] = PIXEL_SIZE;
            config["update_interval"] = PLAYER_UPDATE_INTERVAL;
            config["explore_radius"] = EXPLORE_RADIUS;
            config["max_messages"] = MAX_MESSAGES;
            config["always_map"] = ALWAYS_MAP;
            config["always_visible"] = ALWAYS_VISIBLE;

            string json = DictionaryToJson(config);
            return json;
        }

        static string DictionaryToJson(Dictionary<string, object> dict)
        {
            var entries = dict.Select(d =>
            {
                switch (d.Value)
                {
                    case float o:
                        return $"\"{d.Key}\": {o.ToString("F2", CultureInfo.InvariantCulture)}";
                    case double o:
                        return $"\"{d.Key}\": {o.ToString("F2", CultureInfo.InvariantCulture)}";
                    case string o:
                        return $"\"{d.Key}\": \"{o}\"";
                    case bool o:
                        return $"\"{d.Key}\": {o.ToString().ToLower()}";
                    case Vector3 o:
                        return $"\"{d.Key}\": \"{o.x.ToString("F2", CultureInfo.InvariantCulture)}," +
                               $"{o.y.ToString("F2", CultureInfo.InvariantCulture)}," +
                               $"{o.z.ToString("F2", CultureInfo.InvariantCulture)}\"";
                    default:
                        return $"\"{d.Key}\": {d.Value}";
                }
            });
            return "{\n    " + string.Join(",\n    ", entries) + "\n}\n";
        }
    }
}
