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
  The sweep behind it runs only while someone is looking at the map.
* **Forest and logging** — standing trees shade the terrain and felled ground stops being
  shaded, so clearings show through. Stumps are counted as the record of felling.
* **Boats and carts**, in explored territory only.
* **Portals, graves and every placed piece** as JSON, for a front-end of your own. The
  bundled page does not draw them yet.
* **World render at the resolution you choose** — `render_size` 4096 halves the metres
  per pixel, and rendering no longer stalls the server.
* **The world in 3D.** Drop the little figure on any walked spot, as in Street View, and
  you stand there at eye height: the ground as the game shapes it, terraforming included,
  water, and every building, tree and rock drawn with the game's own model, under a sky
  lit by the server's clock, and the players online standing there as themselves, in what
  they wear. Look around, walk, or switch to an overview.
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

The viewer at `http://your_ip:port` is five pages — the live map (with the world in 3D
as a mode of it), the world's own page (its records, its lands, its bosses), a portal atlas drawn on a biome chart (a hub opens close up: the base's
floor plan with each gate where it stands, and going through a gate lands you at its far
side with the gate you arrive at lit), a planning board for drawing and sharing routes,
and per-player tallies (a player's own page draws them as the 3D view does, turning slowly:
live while they are online, as last seen once they have gone) —
with a legend that switches each layer and a Layers card, as on Google Maps, holding
presets of the legend and the choice of ground: the render, or the flat biome atlas.
The sidebar folds away with the ☰ in the bar, on phones it is a drawer, and the choice
holds across pages. Its source is `WebMap/web`: `site.css` is the style system every
page draws from, `map-core.js` the rendering core, `js/card.js` a player as the roster
reads them (for the players page and the tour), and each page holds only its own.
Our own hosted copy at
[xn-valheim] deploys the same files with a `site-config.js` that names our server.

Players only appear once they set **visible to other players** on the in-game map (`m`).

### The cinematic

The ▶ among the map's buttons, or a link ending in `#tour`, plays the world for a screen
left open: its places one after another, flown to on the map with the layer that makes
each worth seeing, then circled in 3D where the ground is walked. Any touch ends it.
A stop about a player -- someone online, the latest death, a grave -- carries their card
at the side, as their row on the players page reads them: the class, the pentagon, the
four figures, and their Viking turning beside them (a phone keeps the figures alone).

### The 3D view

A mode of the map, entered as Street View is: drag the figure at the top of the map's
buttons onto walked ground and drop it (on a phone: tap it, then tap the map), pick **3D**
under View in the Layers card (you stand in the middle of the map), or **See in 3D** on a
place or a pin. `#3d=<x>,<z>` (world metres; `&look=<degrees from north>` optional) links
straight in, and the hash follows you as you go. You stand at eye height, 1.8 m above the
ground, looking north. Drag to look around; the wheel or a pinch zooms the eye, not the
distance; `W A S D` or the arrows walk (`Shift` runs, `Q E` turn); a click on the ground
walks there. ⇅ swaps in an orbit camera over the same spot, and back; ☼ holds
the light (the server's clock, or a time you pick), shadows (off on phones) and what is
shown. **Map** or `Esc` puts the map back, centred where you stood, and the hash back to
`#at`. The renderer and its three.js load on the first step into 3D, never with the map.

The ground is the game's own: the generator's heights blended across each 64 m zone the
way the game blends them, plus every terraform players have made, coloured by biome from
the chart, with water at 30 m. On it stands every object in the three 256 m chunks around
you -- buildings, trees, rocks, bushes, ruins, boats -- each drawn with the game's model
of it and placed, turned and scaled as in the world; a rock mined for its ore or for a
base shows only the pieces still standing. The world's locations stand there too: the
traders' camps, the Bog Witch's hut, crypt, cave and dwarven entrances, the boss altars,
stone circles and runestones, as every game spawns them for itself. Who lives there is
not drawn -- Haldor, Hildir and the Bog Witch are missing from their own camps -- and a
part the game leaves to chance is drawn as its likelier outcome, so a location can differ
in a detail from the one in game. Only chunks somebody has walked are sent at all; the rest
stay dark, as on the map.

The players online (those the map shows) are drawn as themselves, as every other game draws
them: the body they chose, their skin and hair colour, their hair and beard, and what they
wear and hold -- helmet, chest, legs, cape, belt, what is in each hand and what is slung on
their back -- facing the way they face, their name above. The server runs no animation, so
they all stand in the game's idle pose, the first moment of it: no walking, swinging or
sitting, and a cape hangs as it was modelled rather than in the wind. A player whose parts
the library has not exported yet stands as a plain figure until it has.

Pull the overview out and the view reaches further in rings measured from the camera:
the nine chunks under it with everything, while the camera is within 700 m; out to about
the camera's distance (at most 3 km) the ground a little coarser and only what reads from
afar -- player pieces two metres across or three high, big rocks, trees as their crowns
or as cones; beyond, out to five times the distance (at most 16 km), the ground alone at
8 or 16 m a sample. From 4 km up the whole walked world of our copy is 1,019 chunks at
16 m, 575 KB over 1,019 requests, three seconds from the mod on a desktop. The explored
mask says which chunks to ask for, fetches go nearest first six at a time, and whatever
no ring wants is let go (at most 1,600 chunks of ground and 120 of objects). In the
overview, `W A S D` or the arrows carry the circled spot over the land, `E` or `Space`
raise it and `Q` or `Shift` alone lower it; `Shift` with a key goes faster.

### The model library

The first time the sweep meets a prefab, the mod exports it as a glTF model on the game
thread, within `export_ms_per_frame` a frame. The dedicated server cannot read most
textures, or about one mesh in eight, so a background thread then reads those straight out
of the game's own asset files, once per game version, and the models that wanted them are
exported again. The log says so:

```
WebMap: exporting models into .../map_data/models as the sweep finds them, 6 ms a frame
WebMap: extracting 199 textures from the game files in .../valheim_server_Data
WebMap: 199 of 199 textures extracted from 1 files in 5s
WebMap: 446 models to re-export with newly extracted textures
WebMap: model export done: 1001 prefabs in 15s; 507 with a model, 39 waiting on locked meshes, library 64.4 MB
WebMap: extracting 48 meshes from the game files in .../valheim_server_Data
WebMap: 48 of 48 meshes extracted from 1 files in 3s
WebMap: 45 models to re-export with newly extracted meshes
WebMap: model export done: 45 prefabs in 0s; 539 with a model, 7 waiting on locked meshes, library 71.2 MB
```

That was our world on a desktop: two and a half minutes from the first sweep to the last
model. It starts when someone first opens a page that reads the sweep, and a restart
exports only what is new.

A location is no network prefab: the game keeps its prefab apart and each game spawns it
from a marker, so the mod loads it for the export and lets it go after, and a big one is
exported over as many frames as it takes, its file written off the game thread.

A live player is exported in parts as players are seen: each body model, and each item a
look wears, as it hangs on the body (`ArmorBronzeChest@armor`, `SwordBronze@RightHand_Attach`).
The mod plays the game's own idle clip once on a copy of the Player's rig and keeps that
pose; every part carries the Player's skeleton in it, the body and the garments skinned to
it and a held or slung item as a node under its attach point, so the parts stand together
wherever the viewer puts them. The viewer tints the skin and the hair and lays the body's
chest and legs paint over the skin itself, so one part serves every player who wears it.

It all lives in `map_data/models/`, beside the worlds' own folders, since prefabs are the
same in every world: `index.json` (what was exported), `textures.json` (what the models
want), a `<hash>.glb` per prefab, `tex_<name>.png` per texture and `meshes/*.bin` for the
extracted meshes. On our world that is about 73 MB -- 57 MB of models, 14 MB of textures,
2.4 MB of meshes -- and it survives restarts and updates of the mod. The models and
textures are served to the 3D view; the meshes are game data and never leave the server.
Delete the folder to export everything again.

### Chat commands

* `!pin` — a dot where you stand. `!pin some text` labels it.
* `!pin <type> <text>` — types are `dot`, `fire`, `mine`, `house`, `cave`.
* `!undoPin` — remove your most recent pin.
* `!deletePin <text>` — remove the pin matching that text. With no text, your most recent
  unnamed pin.

Not case sensitive. Past the configured limit a player's oldest pin is dropped.
A deployment with a sign-in (see `/pins` below) lets members do the same from the map:
tap walked ground and **Pin here**, or tap a pin to relabel, retype, move or delete it.

### Server announcements

`POST /announce`, message as the body, `X-Announce-Token` header. The secret lives in
`announce.token` beside the DLL — not in the BepInEx config, which is rewritten on
shutdown and would discard it. No token file means the route is closed.

### Discord

Set `discord_bot_token` (from the site's settings page, or the config) and the mod talks
to Discord on its own, over the bot API rather than a webhook:

* **The audit log.** Every name, pin and setting change made from the site is posted to
  `discord_log_channel` as it happens (`Discord.Tell`, from the same code that already
  logs it to the console). Joins, leaves and deaths are unaffected — they keep going out
  the existing `discord_webhook`.
* **Chat, both ways.** With `discord_chat_channel` set and `chat_relay` on (the default),
  in-game chat is posted there as **name**: text, and the mod polls the channel every
  three seconds for the reverse: a human's line (bots, webhooks and the mod's own posts
  are skipped) is spoken in game as `[Discord] name: text`, by guild nickname where the
  member has one. A restart never replays history — the poll starts from the newest
  message it finds — and at most five messages a poll are spoken, so a flood on the
  Discord side cannot flood the server.
* **The settings picker.** Once a token is set, `discord_guild` and the two channel
  settings offer a dropdown on the settings page instead of a bare id field
  (`GET /discord/guilds`, `GET /discord/channels?guild=`, behind the same token and
  `X-Admin` gate as `/settings`); without a token they're just a plain field.
* Discord's bot API needs the **Message Content** privileged intent turned on for the
  chat relay to read anything (Discord blanks `content` on every message otherwise):
  the app's page, Bot → Privileged Gateway Intents → Message Content Intent.

An empty `discord_bot_token` turns all three off; nothing here touches `discord_webhook`.

## Configuration

Standard BepInEx config, plus:

* `render_size` — pixels across the world render, default 2048. Same area as
  `texture_size`, only sharper: 4096 halves the metres per pixel for a one-time render of
  about a minute and a larger download. The overlays stay at `texture_size`, where extra
  resolution buys nothing. A `map.png` of the wrong size is rebuilt on start.
* `show_vehicles` — report boats and carts at `/vehicles`. They are only ever reported in
  explored territory; off stops them being reported at all.

For the 3D view, under `[Models]` (the host rewrites the config on restart, so the
defaults are what runs):

* `export_models` — export each prefab as a glTF model into the library, default true.
  Off, the 3D view has ground and nothing on it.
* `extract_textures` — read the models' textures out of the game files, default true.
  Off: flat colours.
* `extract_meshes` — read the meshes the engine keeps locked out of the game files,
  default true. Off: those models are drawn as boxes of their size.
* `export_ms_per_frame` — game-thread milliseconds a frame the export may take, default 6.
* `texture_max_size` — longest edge of an extracted texture in pixels, default 512.
* `object_categories` — what the 3D view is sent, of `piece,other,rock,bush,tree`, default
  all five.

## HTTP endpoints

| Path | Returns |
|------|---------|
| `/map`, `/map.jpg` | the world render; the JPEG is about a seventh the size |
| `/fog` | explored mask (PNG) |
| `/chart` | the world as a chart: each pixel its biome's flat colour, water one blue, no relief (PNG, once per world) |
| `/structures`, `/structures/stats` | structures overlay; counts by prefab and the last sweep's cost |
| `/forest`, `/forest/stats` | forest overlay, tree and stump counts with density percentiles |
| `/trails` | where players have walked: a count per map pixel, drawn as a faint blue band; 503 until the first sweep after someone walks (PNG) |
| `/features` | the world's geography with its names: landmasses, ranges (with peaks), lakes, bays, rivers (with their course), biome regions; `?v=` from `rev.features` |
| `/names` | `POST {"id","name"}` names a place (empty name: back to the world's own) or, with an id of `hub@x,z`, a portal hub standing there; needs `X-Announce-Token`, credits `X-User` |
| `/pins` (POST) | `{"op":"add","x","z","type","text"}`, `{"op":"edit","id"}` with any of `x`, `z`, `type`, `text`, or `{"op":"delete","id"}`: places, changes or takes up a pin, only ever on walked ground; answers `{"ok","id","pins"}`, 400 with `{"error"}`. Needs `X-Announce-Token`; `X-User` owns a new pin and is logged for every write |
| `/at` | `?x=&z=` in world metres: the biome and height at a walked spot and the places it lies in, with how much of each has been walked and what stands on it; 404 for unwalked ground |
| `/settings` | `GET` the mod's settings as the site shows them; `POST {"key","value"}` sets one (empty value: back to the config's); needs `X-Announce-Token` and `X-Admin: 1`, credits `X-User` |
| `/discord/guilds` | the bot's guilds, `{"guilds":[{"id","name"}]}`, for the settings picker; same gate as `/settings` |
| `/discord/channels` | `?guild=` a guild's text and announcement channels, `{"channels":[{"id","name"}]}`; same gate |
| `/pieces` | every placed piece as `[prefab, x, z, yaw]` against a table of prefab footprint and colour; a torch, fire pit or hearth carries a fifth field, `1` while it has fuel (JSON, about 60 KB for a world) |
| `/portals` | portals with their tag and the portal each is linked to, as the game has connected them (JSON) |
| `/graves` | tombstones still holding gear: owner, position, seconds since the death (JSON) |
| `/vehicles` | boats and carts, position and type (JSON) |
| `/stats/players` | per-player tallies: joins, deaths, chat, distance (by biome, the sea as `Ocean`), portal hops, pins, standing pieces/portals/ships, graves, kills, trees felled, rock pieces broken, and `gear`: seconds with each kind of thing in hand and in each weight of chest armour, the set last worn, hits on creatures by kind; and `look` and `yaw`, how the player was last seen, as `/state` carries them (JSON) |
| `/players`, `/pins`, `/messages` | live state (JSON) |
| `/state` | all of the small JSON blocks in one document -- players, messages, pins, vehicles, portals, graves, traders, the last 500 deaths with where they happened -- plus a content revision per layer (`rev.fog`, `rev.forest`, `rev.structures`, `rev.pieces`, `rev.chart`, `rev.trails`, `rev.features`, and for the 3D view `rev.objects`, `rev.height`, `rev.models`) so a viewer fetches a layer only when its picture changed; pass the revision as `?v=`. `time` is the game's clock: `{"day", "frac"}`, the fraction of the day the sun goes by (0.25 sunrise, 0.5 noon, 0.75 sunset). A player the map shows carries `yaw` (degrees clockwise from north) and `look`: `model` (the body), `skin` and `hair` (the colours, 0..1), `slots` (the prefab in each slot) and `parts`, the library parts to draw, body first |
| `/height` | `?cx=&cz=[&step=]`, a 256 m chunk (`cx = floor(x / 256)`, `cz` likewise): (256/step + 1)² little-endian int16, decimetres of world height, `step` metres apart (1, 2, 4, 8 or 16: 257 a side down to 17; default 1, each step cached on its own under the same revision); row 0 is the south edge (`z = cz*256`), column 0 the west, both edges included so neighbours share a seam. Terraforming included; water stands at 30 m. 404 for a chunk nobody has walked; the chunk's terraform revision in `X-Rev` |
| `/objects` | `?cx=&cz=`, every visible object in the chunk as `OBJ2`, little-endian: `'OBJ2'`, u32 count, u32 prefab count, i32 prefab hashes, then 44 bytes an object -- u16 prefab index, u8 flags (1 = player-built, 2 = pieces mined off), u8 pad, f32 x y z, f32 rotation quaternion x y z w, f32 scale x y z (Unity's frame, y up, z north) -- then the mined rocks: u32 count, and per rock u32 object index, u16 bit count, bits/8 bytes, bit i set when its hit area i is gone. (`OBJ1`, the first cut, was the same without the flag and the table; the page reads both.) 404 for a chunk nobody has walked; its revision in `X-Rev` |
| `/prefabs` | the model library's index: per prefab hash, its name `n`, category `c`, whether it has a model `m` and its version `v`, bounds `b`, and for foliage the canopy bounds `k`, leaf texture `kt` and tint `kc`; for a rock mined in pieces its kind `rk` (5 MineRock5, 1 MineRock) and `pa`, the hit area of each glTF primitive in order (-1 none, -2 MineRock's whole-rock model); for a live player's part (category `rig`, named as a look's `parts`) the chest and legs paint it lays over the skin, `ct` and `lt` (JSON) |
| `/models/<file>` | a model (`<hash>.glb`, the hash as eight hex digits) or a texture (`tex_<name>.png`) from the library; cacheable for a day, `ETag` the file's time |
| `/announce` | POST, see above |

The structure sweep walks every ZDO on the game thread, a few thousand per frame;
everything after the walk runs on a pool thread. It runs only while someone is reading the map: a request to any layer or to the sweep-fed JSON
arms it for two minutes, sweeps start at least a minute apart, and an idle server does
none at all. `/config`, `/players`, `/map`, `/pins` and `/messages` do not arm it, so a
monitor probing those keeps the game idle. `/structures/stats` reports the last sweep —
ZDOs walked, game-thread milliseconds, frames, wall time, gen-2 collections — so the cost
can be read rather than guessed.

## Notes for developers

**Chat.** Valheim 1.0 addresses chat to each recipient rather than broadcasting it, so a
hook on `HandleRoutedRPC` sees only pings. It still passes through the server, though:
`RPC_RoutedRPC` calls `RouteRPC` to forward it, and that is where this mod observes chat.
A shout arrives once per recipient and is de-duplicated on sender, method and payload.

Upstream instead registered a fake server-side player so clients would address the server.
**On 1.0 that stops anyone joining at all** — the server stays healthy and registered but
logs zero connection attempts — so it is gone here. Don't put it back.

**The sweep yields only between sector lists.** Yielding inside a `List<ZDO>` lets the
game's removals shift the index under the walk and skip ZDOs; a whole sector between
yields is the smallest safe step.

**Announcements** use `MessageHud`'s `ShowMessage` rather than chat: `Chat` gates every
message on a `RelationsManager` permission check against the sender's platform user id,
which a server does not have, so chat sent from a server is dropped in silence.

**Kills, trees and rocks.** The server runs no creature, tree or rock -- the nearest
player's game does -- so it counts what passes through it:

* **kills** -- a creature that fell within ten seconds of a player's hit, for the last
  player seen hitting it. A tamed creature counts for nothing, and one that despawns is
  nobody's kill.
* **trees** -- a tree felled; its logs and stump do not count again.
* **rocks** -- each piece broken off a rock or an ore deposit, and a small rock broken whole.

A hit reaches the server only on its way to another player's game. A player alone in an
area owns everything around them, and their own hits never leave their game, so when no
hit was seen the owner is credited instead: with a creature whose own record lists them
among the players who hit it, a tree felled within 16 m of them, a rock piece within
32 m. A creature the owner kills with a single blow can go uncounted, since its record
may not reach the server before it falls.

**Gear.** A player's ZDO carries what every other game draws them holding and wearing,
so once a second, while alive and out of bed, the server adds a second to:

* **`hand`** -- the family of what each hand holds, sorted by the item's own skill and
  type: `bow`, `crossbow`, `staff`, `shield`, `knife`, `sword`, `axe`, `mace`, `spear`,
  `polearm`, `hammer`, `hoe`, `cultivator` (the scythe too), `pickaxe`, `torch`,
  `fishing`, `tool` for anything else held (a tankard), `none` with both hands empty or
  sheathed (fist weapons are the game's own unarmed). A sword and shield is a second of
  each. `twohanded` counts on top of the family, for a two-handed melee weapon.
* **`armor`** -- the chest piece's weight, from its movement penalty: `light` none
  (leather, troll, lox, fenring, Askvin), `medium` up to 3.5% (root), `heavy` more
  (bronze, iron, wolf, padded, carapace, flametal: 5%), `mage` whatever speeds eitr
  (eitr-weave), `none` bare.

`diet` is seconds by what the food behind the bars leans to: `hearty` (health half
again the stamina), `quick` (the other way), `eitr` (forty or more of it, one eitr food),
`balanced`, `none` (under thirty of anything). The server never sees the foods, only the
max health they set and the stamina and eitr they refill to when the player stands
still, so a slowly fading peak of each stands in for the maximum; `food` is the latest
`hp`, `st` and `eitr` above the base 25 and 75.

`worn` is the latest set: `right`, `left` (a sheathed weapon still counts), `chest`,
`legs`, `helmet`, `shoulder`, and the two back slots `rightBack` and `leftBack`, by prefab name. **`hits`** are colour, not a record: a hit
on a creature counts only when it passes through the server on its way to another
player's game, never one on a creature the hitter's own game runs, which alone in an
area is all of them. Each is `melee`, `ranged` or `magic` (a staff's), and `backstab`
counts on top when the weapon has a backstab bonus and the creature was not yet alerted.

### The style system

`site.css` is the whole look: the tokens on `:root` (surfaces darkest to lightest, ink,
accents, the map's own hues for portals, traders and graves, the halos over the world,
radii, shadows, fonts, the bar and sidebar widths), the base, and the components every
page shares -- `.title .eyebrow .sub .note .section .foot`, `.card .well .glass .stat .dot`,
`.btn .chip .field .btnrow`, `.stage .mapctl .legend`, `.spot` (a spotlight: one figure, a line of
context and a small picture from `MapCore.pic` -- a roster strip, a share ring, labelled bars),
`.cls .mini .radar .viking` (a player as `js/card.js` draws them), the nav and the sidebar shell. A
page's own `<style>` holds only what that page alone draws, and names no colour of its
own: every colour is a token, so a restyle is an edit to `:root` and the pages follow.
The two exceptions are data, not chrome: the forest swatch's greens and the biome
inks the map sets names in (`NAME_STYLE` in `map-core.js`).

### Tests

```bash
dotnet test WebMap.Tests     # the mod's logic: builds the mod, then loads WebMap.dll on .NET 10
node --test                  # map-core.js's pure functions, from the repo root
```

The first needs `libs/` set up as for a build. [TESTING.md](TESTING.md) says what each
layer covers, and what nothing covers yet. CI runs both on every push and pull request.

### Checking the viewer against a live server

`tools/sameorigin.py WebMap/web http://your_ip:port 8766` serves the viewer the way the
mod does -- the pages from disk, every other path forwarded to the server -- and, with a
headless Chrome started with `--remote-debugging-port=9334` (and, for the 3D view's
WebGL, `--use-angle=swiftshader --enable-unsafe-swiftshader`),
`node tools/check.mjs http://127.0.0.1:8766 9334 [/tmp/shots]` checks every page over it:
no exceptions, markers and names drawn, a tapped name opening its place card, the names
flyout, ground and objects in 3D at the world's start, hubs and dial lines, a card per
player, the plan loading, the tour landing through a resize, a stopped one staying stopped
and a player's card turning in it until it goes. One line per check, a
non-zero exit on any failure, and a screenshot of each page when given a directory. A
viewer change is done when that is clean.
`tools/bench.mjs http://127.0.0.1:8766 9334` pans and zooms the map over the same Chrome
and prints ms per frame: 16.7 is the screen's own rate, anything above it is lag.

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

A fresh world has nothing in it until a player walks there -- the server builds zones only
around players -- so the 3D view and the sweep want a real world. Copy one in (Valheim 1.0
saves a world as a folder, `worlds_local/<name>/`) and run it under its own name, with its
`fog.png` so the same ground counts as walked:

```bash
VALHEIM_TEST_WORLD=Mothership ./testserver.sh up
podman cp Mothership valheim-test:/valheim-saves/worlds_local/
podman cp fog.png valheim-test:/valheim/BepInEx/plugins/WebMap/map_data/Mothership/fog.png
podman exec valheim-test chown -R steam:steam /valheim-saves/worlds_local /valheim/BepInEx/plugins/WebMap/map_data
podman restart valheim-test
```

## Licence and credit

MIT where applicable.

* 1.0 update, structures, forest, vehicles, portals, graves and pieces by [Hunter Boyd](https://github.com/hunterjsb)
* Maintained upstream by [Jeff Clark](https://github.com/h0tw1r3)
* Original work by [Kyle Paulsen](https://github.com/kylepaulsen)
* The 3D view's model export, asset readers, world objects, instanced viewer and sky are
  ported from [f00d4tehg0dz/valheim-webmap] (MIT); its ground and street-view camera are ours
* [three.js] r180 (MIT) is vendored under `WebMap/web/vendor/three`
* Background by [webtreats], [CC BY 2.0]

[h0tw1r3/valheim-webmap]: https://github.com/h0tw1r3/valheim-webmap
[xn-valheim]: https://github.com/hunter-jsb/xn-valheim
[BepInEx]: https://github.com/BepInEx/BepInEx
[indifferentbroccoli/valheim-server-docker]: https://github.com/indifferentbroccoli/valheim-server-docker
[f00d4tehg0dz/valheim-webmap]: https://github.com/f00d4tehg0dz/valheim-webmap
[three.js]: https://threejs.org
[webtreats]: https://www.flickr.com/photos/webtreatsetc/4081217254
[CC BY 2.0]: https://creativecommons.org/licenses/by/2.0/
