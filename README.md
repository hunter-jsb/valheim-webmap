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

## Known issues on 1.0

* **Chat commands are unverified on 1.0.** Upstream made pins work by registering a fake
  server-side player so clients would route chat to it; on 1.0 that patch stops players
  joining the server entirely, so it is disabled here. Chat RPCs do still reach the
  server, but pin placement has not been confirmed working since the 1.0 update. Map
  pings, players, fog and structures are unaffected.
* Death notices in the feed are new and lightly tested.

## Licence

MIT where applicable.

## Credit

* 1.0 update and structures overlay by [Hunter Boyd](https://github.com/hunterjsb)
* Maintained upstream by [Jeff Clark](https://github.com/h0tw1r3)
* Original work by [Kyle Paulsen](https://github.com/kylepaulsen)
* Background by [webtreats], released under the [CC BY 2.0] license.

[h0tw1r3/valheim-webmap]: https://github.com/h0tw1r3/valheim-webmap
[BepInEx]: https://github.com/BepInEx/BepInEx
[webtreats]: https://www.flickr.com/photos/webtreatsetc/4081217254
[CC BY 2.0]: https://creativecommons.org/licenses/by/2.0/
