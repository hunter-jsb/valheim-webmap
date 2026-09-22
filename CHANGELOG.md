# Changelog

## Unreleased

* The land has names. Once per world the mod reads the generator's own geography --
  landmasses, lakes, bays and mountain ranges from a sampled grid of biome and height,
  rivers from the generator's river list -- and gives each an Old Norse name from the
  seed: Morland, Stenarey, Frostfjell, Eikvatn, Nordsvik, Langará. `/features` lists
  them (kind, where the name sits, extent, a range's peak, a river's course), `rev.features`
  in `/state` says when they changed. The map sets them the way an atlas does, once the
  fog has lifted somewhere on the place; a Names detail switches them off.
* Naming a place. `POST /names` with `{"id","name"}` gives a place a name (an empty name
  returns the world's own), guarded by the announce token and attributed to an `X-User`
  header; the hosted site's Worker adds both for a signed-in Discord member, who taps a
  name on the map to rename it.
* The bar offers a sign-in where the deployment's API has one (`/auth/me`); served by
  the mod alone it shows nothing.

## 2.12.0

* A Layers card on the map, as on Google Maps: a thumbnail of the other ground in the
  corner, and behind it the choice of ground -- the world render, or the flat biome
  atlas the portals page draws on, which carries no forest shading -- and presets of
  the legend (Default, Travel, Builds, Wilds, Bare). `L` cycles the ground. Graves are
  off by default, like portals.
* One sidebar shell for the map, the portals and the plan: the ☰ in the bar folds it
  away, and the choice holds across pages. The portals page now draws its diagram
  full-height with the hub and pair lists beside it instead of a boxed chart above them.
* Phones: the sidebar is a drawer, shut on arrival, opened by the ☰ or by the online
  pill on the map; the layers tray opens upward; the bar fades at its edge where it
  swipes.
* Torches and fires: a Details row in the Layers card, off by default, draws torches,
  fire pits, hearths and braziers as warm points over the map -- one glow where a base
  is kept -- and a burnt-out one as a grey ring. The sweep now reads each fire's fuel
  and sends it as a fifth field on those pieces; a viewer on an older server treats
  every fire as lit.
* Deaths and trails, two more details in the Layers card. Every death is kept with
  where it happened (`deaths` in `/state`, the last 500) and drawn as a red glow that
  fades over a month. Trails count, per map pixel, the times a player walked into it,
  kept in `trails.bin` beside the stats and rendered each sweep as `/trails`, a faint
  blue over the ground; `rev.trails` in `/state` says when it moved.
* The bundled viewer's file cache keys on each file's timestamp, so a viewer file
  replaced on disk is served at once instead of after the next restart.
* Panning no longer lags. The map and the plan rendered every raster and every one
  of the thousands of build footprints again on every pointer event; now the scene
  renders once per zoom into a window wider than the screen, a pan slides it, and a
  zoom stretches it until a fresh render lands -- at once where that is quick, after
  the zoom settles where it is not. The footprints themselves draw as one path per
  colour instead of a fill each, and the deaths re-render only when the list changes.
* One style system in `site.css`: the tokens (surfaces, ink, accents, radii, shadows),
  the base, and the pieces every page shares -- cards, stats, buttons, chips, fields,
  the legend, the map's button column, section rules. The pages' own styles shrank
  to what each alone draws. The bar underlines the page you are on, controls ease
  between states, focus is visible, and scrollbars are thin everywhere.
* Hildir is her camp and nothing else. The trader scan matched any location whose name
  contained "hildir", which caught her three quest sites -- the mountain cave, the swamp
  crypt and the plains fortress -- and drew her standing in each of them. The three
  camps are now matched by name (`Vendor_BlackForest`, `Hildir_camp`, `BogWitch_Camp`),
  and the scan logs which it found.
* The Log reads as chat again. A player asked for the web map's chat back: it had
  never gone, but the panel had become a join-and-death ticker with a line of chat
  every few hours lost in it -- and every disconnect was written twice, once by the
  announcement and once by the notifier. The server now marks its own lines (`ev` on
  each message), keeps chat and events to their own depth so a busy evening cannot
  push the chat out, and writes a leave once. The panel has a **chat / all** switch,
  chat by default, remembered per browser; a viewer on an older server falls back to
  treating a line with no speaker as an event.
* Four bugs found by reading the paths the last few features added to:
  * A chat message was dropped whole if the server could not resolve the sender's
    character -- the position was looked up before anything else, though only the
    pin commands use it. Chat is now read first and the lookup happens only for a
    pin.
  * A pin command from a sender with no id would have matched every pin in the
    list (`StartsWith("")`), so the per-player trim could delete other players'
    pins and `!undoPin` could remove someone else's. Pins now need a known owner.
  * An RPC addressed to everybody runs both branches of `RPC_RoutedRPC`, so the
    two observation points saw it twice; only one of them de-duplicated. Both do.
  * The Discord webhook posted from the join and disconnect handlers on the game
    thread: a slow Discord stalled a player's join for as long as it took to fail,
    and a bad URL threw inside the handshake. The post goes to a pool thread, and
    an unset webhook no longer logs a line per join.

## 2.11.0

* The bundled viewer is now the same site that fronts our own server: the live map with
  builds drawn as shaded roofs and floor plans, the portal atlas on a biome chart, the
  planning board, and the players page — served by the mod on its own port, so it needs
  no proxy and no hosting. The old viewer, its webpack build and the websocket pings it
  showed are gone; the pages poll `/state` instead.
* Pages are served with `no-cache` so a new build shows on the next visit; assets for
  five minutes. They were cached for a week.

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
* `/state`: every small JSON block in one document per tick, with a content revision per
  layer. A viewer polls one URL and fetches a layer only when its revision moved; `?v=` on
  a layer request lets a cache keep it as long as it likes. The bundled viewer polls it
  every 30 s instead of re-pulling both overlays on timers, and loads the world render as
  the JPEG: browsers gave up on the 16 MB PNG a 4096 render produces part way through,
  after which the viewer never got to its overlays, pins or messages.
* `/chart`: the world as a flat chart, each pixel its biome's colour and water one blue,
  sampled from the world generator once per world and kept beside `map.png`.
* Traders (Haldor, Hildir, the Bog Witch) in `/state`, in explored ground only: the world
  generated them, and a player's own map pins them once they have been near.
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
