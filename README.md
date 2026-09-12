# Valheim WebMap

A **server-side** mod that publishes a live web map of your world. Share
`http://your_ip:port` and anyone can watch — **clients need no mods.** Dedicated
server only.

A fork of [h0tw1r3/valheim-webmap] rebuilt for **Valheim 1.0 (Deep North)**.

![screenshot](https://github.com/user-attachments/assets/981287f3-f5fa-4e09-878e-e2c94f6cc19c)

## Features

* Explorable map in the browser — wheel zoom, pinch on mobile.
* Shared fog of war: only what players have actually explored.
* Live player list and positions, auto-follow, and in-game pings.
* **Structures** — placed pieces drawn in the colour of their material, so bases read as
  bases. Keyed off the piece's creator, so terrain and world-generated ruins never appear.
* **Forest and logging** — standing trees shade the terrain and felled ground stops being
  shaded, so clearings show through. Stumps are counted as the record of felling.
* **Boats and carts**, in explored territory only.
* **Server announcements** on every player's screen, for restart warnings and the like.
* Chat, deaths and joins in the message log; optional Discord notifications.

## Install

1. With [BepInEx] working, put the `WebMap` directory in
   `Valheim dedicated server/BepInEx/plugins/WebMap`.
2. Start the server once to write a default config.
3. **Stop the server** before editing that config — BepInEx rewrites it on shutdown, so
   edits made while it runs are discarded.
4. Open the configured port (default `8080`) and visit `http://your_ip:port`.

After updating, hard-reload the page (`shift`+reload) to clear cached layers.

## Using the map

The structures and forest layers are drawn by default; the menu (top-left) has a toggle
for each, alongside the pin filters.

Players only appear once they set **visible to other players** on the in-game map (`m`).

### Chat commands

* `!pin` — a dot where you stand. `!pin some text` labels it.
* `!pin <type> <text>` — types are `dot`, `fire`, `mine`, `house`, `cave`.
* `!undoPin` — remove your most recent pin.
* `!deletePin <text>` — remove the pin matching that text. With no text, your most recent
  unnamed pin.

Not case sensitive. Past the configured limit a player's oldest pin is dropped.

### Server announcements

`POST /announce`, message as the body, `X-Announce-Token` header. The secret lives in
`announce.token` beside the DLL — not in the BepInEx config, which is rewritten on
shutdown and would discard it. No token file means the route is closed.

## Configuration

Standard BepInEx config, plus `show_vehicles` to stop `/vehicles` reporting boats and
carts at all. They are only ever reported in explored territory.

## HTTP endpoints

| Path | Returns |
|------|---------|
| `/map`, `/map.jpg` | the world render; the JPEG is about a seventh the size |
| `/fog` | explored mask (PNG) |
| `/structures`, `/structures/stats`, `/structures/refresh` | structures overlay, counts by prefab, queue a sweep |
| `/forest`, `/forest/stats` | forest overlay, tree and stump counts with density percentiles |
| `/vehicles` | boats and carts, position and type (JSON) |
| `/players`, `/pins`, `/messages` | live state (JSON) |
| `/announce` | POST, see above |

The structure sweep walks every ZDO on the main thread in slices, so it runs on a slow
cadence (2 minutes) rather than with the map refresh. `/structures/refresh` cuts the wait
short.

## Notes for developers

**Chat.** Valheim 1.0 addresses chat to each recipient rather than broadcasting it, so a
hook on `HandleRoutedRPC` sees only pings. It still passes through the server, though:
`RPC_RoutedRPC` calls `RouteRPC` to forward it, and that is where this mod observes chat.
A shout arrives once per recipient and is de-duplicated on sender, method and payload.

Upstream instead registered a fake server-side player so clients would address the server.
**On 1.0 that stops anyone joining at all** — the server stays healthy and registered but
logs zero connection attempts — so it is gone here. Don't put it back.

**Announcements** use `MessageHud`'s `ShowMessage` rather than chat: `Chat` gates every
message on a `RelationsManager` permission check against the sender's platform user id,
which a server does not have, so chat sent from a server is dropped in silence.

### Local test server

`./testserver.sh` runs the same image the hosts do
([indifferentbroccoli/valheim-server-docker]) under podman.

```bash
./testserver.sh up       # first run downloads the game, ~10 min
./testserver.sh deploy   # build, copy in, restart
./testserver.sh status   # container + endpoints
./testserver.sh logs
```

Join with **Join by IP → `127.0.0.1:2456`**, password `testpass123`.

Two things worth knowing: steamcmd fails `app_update` with
`Missing configuration`, having downloaded nothing, in roughly one run in four — it is
transient, so just run it again. And on an SELinux host the bind mounts need `:z` or the
container silently sees nothing.

## Licence and credit

MIT where applicable.

* 1.0 update, structures, forest and vehicles by [Hunter Boyd](https://github.com/hunterjsb)
* Maintained upstream by [Jeff Clark](https://github.com/h0tw1r3)
* Original work by [Kyle Paulsen](https://github.com/kylepaulsen)
* Background by [webtreats], [CC BY 2.0]

[h0tw1r3/valheim-webmap]: https://github.com/h0tw1r3/valheim-webmap
[BepInEx]: https://github.com/BepInEx/BepInEx
[indifferentbroccoli/valheim-server-docker]: https://github.com/indifferentbroccoli/valheim-server-docker
[webtreats]: https://www.flickr.com/photos/webtreatsetc/4081217254
[CC BY 2.0]: https://creativecommons.org/licenses/by/2.0/
