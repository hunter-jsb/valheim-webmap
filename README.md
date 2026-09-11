# Valheim WebMap

A **server-side** mod that publishes a live web map of your Valheim world. Share
`http://your_ip:port` and anyone can watch the map — **clients do not need any mods
installed.**

This is a fork of [h0tw1r3/valheim-webmap] rebuilt for **Valheim 1.0 (Deep North)**,
with an overlay of player-built structures added.

![screenshot](https://github.com/user-attachments/assets/981287f3-f5fa-4e09-878e-e2c94f6cc19c)

For players to appear on the map they must set **visible to other players** on the
in-game map screen (press `m`).

Dedicated server only.

## Features

* An explorable map of your world in the browser — mousewheel zoom, pinch zoom on mobile.
* Shared fog of war: the map reveals only what players have actually explored.
* Connected players list, live positions, and auto-follow.
* In-game map pings show up on the web map.
* **Player-built structures overlay** — every placed piece is drawn in the colour of its
  material (wood, stone, black marble, thatch, metal, portals), so bases read as bases
  instead of blobs. Natural terrain and world-generated ruins are not drawn; the sweep
  keys off the piece's creator, so only things a player placed appear.
* **Forest and logging overlay** — standing trees shade the terrain, and felled ground
  stops being shaded, so clearings show through as bare terrain. Stumps are counted as
  the positive record of felling.
* **Server announcements** — `POST /announce` puts a message on every player's screen,
  for restart warnings and anything else worth saying in game.
* Connect / chat messages and Discord server-status notifications.

## Installation

1. With [BepInEx] installed and working, place the `WebMap` directory in:

       Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins\WebMap

2. Start the server once; a default config is written to:

       Steam\steamapps\common\Valheim dedicated server\BepInEx\config

3. **Stop the server**, edit the config, then start it again. BepInEx rewrites its config
   on shutdown, so changes made while the server is running are lost.

4. Open the configured port (default `8080`) and visit `http://your_ip:port`.

## HTTP endpoints

Besides the map UI, the server exposes:

| Path | Returns |
|------|---------|
| `/map` | the world render (PNG) |
| `/map.jpg` | the same render as JPEG — about a seventh the size, and the render is opaque so nothing is lost |
| `/forest` | forest cover and logging (PNG, transparent) |
| `/forest/stats` | tree and stump counts, and canopy density percentiles (JSON) |
| `/vehicles` | boats and carts, with position and type (JSON) |
| `/announce` | POST a line to every player's screen (see below) |
| `/fog` | the explored mask (PNG) |
| `/structures` | player-built structures overlay (PNG, transparent) |
| `/structures/stats` | piece counts by prefab (JSON) |
| `/structures/refresh` | queue an immediate structure sweep |
| `/players`, `/pins`, `/messages` | live state (JSON) |

The structure sweep walks every ZDO on the main thread in slices, so it runs on a slow
cadence (2 minutes by default) rather than with the map refresh.

## Updating

**Clear your browser cache** after updating, or hold `shift` and click reload.

## Chat commands

Pins can be placed from in-game chat:

* `!pin` — a dot pin where you stand.
* `!pin my pin name` — a dot pin with a label.
* `!pin [type] [text]` — types are `dot`, `fire`, `mine`, `house`, `cave`.
* `!undoPin` — remove your most recent pin.
* `!deletePin [text]` — remove the most recent pin whose text matches exactly.

Commands are not case sensitive. Past the configured limit, a player's oldest pin is dropped.

## Server announcements

`POST /announce` with the message as the body and an `X-Announce-Token` header shows the
text on every connected player's screen. The shared secret goes in a file named
`announce.token` beside the DLL — not in the BepInEx config, which is rewritten on
shutdown and would discard it. With no token file the route is closed.

It deliberately uses `MessageHud`'s `ShowMessage` RPC rather than chat: `Chat` gates every
message on `RelationsManager.CheckPermissionAsync(sender.UserId, …)`, and a server has no
platform user id to satisfy that with, so chat sent from a server is dropped in silence.

## How chat reaches the server on 1.0

Worth writing down, because it is not obvious. Valheim 1.0 sends player chat **addressed
to each permitted recipient**, never broadcast:

```csharp
Chat.SendText (shout)      -> InvokeRoutedRPC(user, "ChatMessage", headPoint, 2, userInfo, text)
Talker.Say   (normal/whisper) -> m_nview.InvokeRPC(user, "Say", (int)type, userInfo, text)
```

The server's `RPC_RoutedRPC` only calls `HandleRoutedRPC` when the target is itself or
Everybody, so a hook there sees pings (which *are* broadcast) and nothing else. Player
chat instead goes down the `RouteRPC` branch, which the server runs while forwarding the
packet — so that is where this mod observes it. A shout arrives once per recipient and is
de-duplicated on sender, method and payload within two seconds.

Upstream solved this by registering a fake server-side player so clients would address the
server too. **On 1.0 that patch stops anyone joining at all** — the server stays healthy and
registered but logs zero connection attempts — so it is not used here and the code is gone.

## Local test server

`./testserver.sh` runs the same image the hosts do
([indifferentbroccoli/valheim-server-docker]) in podman, so a change can be tried
against a real server before it goes anywhere near a live one.

```bash
./testserver.sh up       # first run downloads the game, ~10 min
./testserver.sh deploy   # build the mod, copy it in, restart
./testserver.sh status   # container + endpoints
./testserver.sh logs
```

Then join it in game with **Join by IP -> `127.0.0.1:2456`**, password `testpass123`.

Two things it works around, both worth knowing:

* The image's `install.scmd` runs `force_install_dir` **before** `login`, and steamcmd
  then fails with `Failed to install app '896660' (Missing configuration)` having
  downloaded nothing. The script mounts a corrected copy with the order swapped. (The
  `-beta` argument is fine; it is only the ordering.)
* On an SELinux host the bind mounts need `:z`, or the container silently sees nothing.

## Licence

MIT where applicable.

## Credit

* 1.0 update and structures overlay by [Hunter Boyd](https://github.com/hunterjsb)
* Maintained upstream by [Jeff Clark](https://github.com/h0tw1r3)
* Original work by [Kyle Paulsen](https://github.com/kylepaulsen)
* Background by [webtreats], released under the [CC BY 2.0] license.

[h0tw1r3/valheim-webmap]: https://github.com/h0tw1r3/valheim-webmap
[BepInEx]: https://github.com/BepInEx/BepInEx
[indifferentbroccoli/valheim-server-docker]: https://github.com/indifferentbroccoli/valheim-server-docker
[webtreats]: https://www.flickr.com/photos/webtreatsetc/4081217254
[CC BY 2.0]: https://creativecommons.org/licenses/by/2.0/
