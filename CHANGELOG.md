# Changelog

## 2.10.0

* `render_size`: the world render at its own resolution. 4096 halves the metres per pixel
  over the same area; the overlays stay at `texture_size`. The render is a coroutine that
  yields every row, so it no longer freezes the server, and a `map.png` of the wrong width
  is rebuilt rather than served.
* Structure sweeps run only while someone is reading the map. A request to any layer or
  sweep-fed JSON arms them for two minutes and they start at least a minute apart; an idle
  server does no sweep work. The fixed two-minute timer and the immediate re-sweep on
  `/structures/refresh` are gone. Every sweep measures itself — ZDOs, game-thread
  milliseconds for walk and finish, frames yielded, wall time, gen-2 collections — as a
  `sweep` field on `/structures/stats` and one log line.
* `/pieces`: every placed piece as `[prefab, x, z, yaw]` against a small table of prefab
  footprint and colour, for viewers that draw builds as vectors. Recorded in the same sweep
  and the same ZDO visit; about 60 KB for a world.
* `/portals`: portals with their tag and the portal each is linked to, taken from the
  game's own connection rather than from matching names.
* `/graves`: tombstones still holding gear, with owner and seconds since the death.
* The chat observer compares the method hash before doing anything else. It used to hash,
  key and de-duplicate every routed RPC the server forwards, on the game thread, thousands
  a second with a few players on. Also removed: a postfix on `GetStableHashCode` that wrote
  two dictionaries on every string the game hashes, and the `ZSyncAnimation` hooks beside it.
* `/fog` encodes its PNG once per change instead of on every request, and request paths
  drop their query string before routing, so a cache-busting parameter no longer 404s.
* Player state is read by ZDOVars hash and `/config` is built on the game thread when the
  world loads, so HTTP threads no longer touch `ZNet`.
* Everything a sweep does after the walk — rendering the overlays, the forest blur, the
  JSON, the PNG encodes — runs on a pool thread. The game thread pays for the walk and
  nothing else; the ~140 ms finish it used to absorb is gone. Overlays are encoded with
  `EncodeArrayToPNG`, which Unity marks thread-safe; `EncodeToPNG` on a `Texture2D`, which
  was being called from HTTP threads, is not.
* The fog is kept as bytes and encoded the same thread-safe way; it was a `Texture2D`
  encoded on HTTP threads while the game thread painted it. `/structures/refresh` is gone:
  reading any layer arms a sweep, which is all it did.
* `/stats/players`: per-player tallies — joins, deaths, chat lines, distance covered, portal
  hops, pins, and what is standing in the world with their name on it (pieces, portals,
  ships, graves). No time played, by design. Persisted beside the world's map data.

## 2.9.1

* Removed a Harmony prefix on `ZRoutedRpc.InvokeRoutedRPC` that ran for every outgoing
  routed RPC and did nothing unless `debug` or `test` was set. Chat is observed at
  `RouteRPC` instead, so it was redundant as well as costly.
* Removed the `test` setting it existed for, which rerouted `DiscoverLocationRespons` to
  everybody, and `discord_invite_url`, which was read by nothing at all.
* Removed `MapDataServer.BroadcastMessage`, which had no callers.

## 2.9.0

* Forest and logging overlay (`/forest`, `/forest/stats`): trees shade the terrain,
  cleared ground shows through. The shading saturates smoothly rather than clamping,
  so thinning a wood is visible and not just clear-felling it; `/forest/stats` reports
  the density percentiles behind the picture.
* Boats and carts (`/vehicles`): reported with position and type rather than painted
  into the structures layer as anonymous pieces. Detected by `Ship`/`Vagon` component,
  so later additions are picked up without a name list.
* Server announcements (`POST /announce`) via `MessageHud.ShowMessage`, so they reach
  unmodded clients; chat cannot be used for this, see the README.
* World render also served as JPEG (`/map.jpg`), roughly a seventh of the PNG.
* Chat is observed at `ZRoutedRpc.RouteRPC`, where 1.0 actually routes it, instead of
  `HandleRoutedRPC`, which never sees a message addressed to another player.
* Removed the fake server-client patches: they block joins on 1.0 and are unnecessary now.

Fixes that matter now that chat is actually observed:

* The chat lists are no longer edited from three threads at once. They are written from
  the game thread, drained by a timer on a pool thread and read by HTTP on a third;
  `/messages` now serves a snapshot instead of walking a list another thread is editing.
* Chat observation decides whether a routed RPC is chat *before* looking up the sender.
  It now runs for every RPC the server forwards, and used to throw and catch a
  NullReferenceException on each one whose sender was not a live peer.
* No more one console line per chat message and per ping; both are behind `debug`.
* The static file cache is concurrent: a browser opens several connections on first load
  and a plain Dictionary can corrupt under that.
* Static files resolve through `Path.GetFileName`, so a backslash in the URL cannot walk
  out of the web root on Windows.
* `/messages` sends `application/json` rather than `applicaion/json`.
* Player state is built once on the game thread and read from there. The broadcast
  timer runs on a pool thread and HTTP on others again, and both used to read ZDOs and
  walk ZNet's live peer list themselves.
* `/vehicles` only reports what is in explored territory, and `show_vehicles` turns it
  off entirely.
* **The bundled map viewer draws the structures and forest layers.** The server has
  produced both since 2.8.0, but the viewer never asked for them, so the structures
  heatmap looked missing even with the sweep plainly running in the log. The menu has
  a toggle for each.
* `package.sh` builds the web bundle. `web/main.js` is a build product and gitignored,
  so packaging from a fresh clone shipped a viewer with no script at all.

## 2.8.0

* Build for **Valheim 1.0 (Deep North)**.
* Add a player-built structures overlay (`/structures`, `/structures/stats`,
  `/structures/refresh`), coloured by material albedo with piece density driving opacity.
* Add death notices to the message feed.
* Route image encode/decode through reflection so the mod builds against the 1.0 assemblies.
* Disable the fake server-side chat client: on 1.0 it prevents players from joining.
  See "Known issues" in the README.

## 2.7.1 and earlier

See [upstream](https://github.com/h0tw1r3/valheim-webmap).
