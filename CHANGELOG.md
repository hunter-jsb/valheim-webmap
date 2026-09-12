# Changelog

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
