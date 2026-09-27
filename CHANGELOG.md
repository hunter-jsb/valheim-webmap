# Changelog

## Unreleased

* The 3D view's light and air. Everything it draws is hazed toward the sky's own colour
  with distance, an exponential height fog summed along each sight line (1/3,800 a metre
  at the sea, thinning by e every 1,200 m up): thickest along level lines low down, thin
  looking down from high, so from 2 km up a continent fades into the distance while the
  ground under you stays clear; contrast and colour fall with it, the mist over unwalked
  sea included, and the fog at the edge of what is loaded takes the sky's colour too. The
  sun carries the day: it crosses the north 45 degrees up at noon (65 before), at 3.4
  (2.6) over a cooler sky fill, and the environment the ground is lit by now sees dim ground
  below the horizon rather than more sky, so a slope turned from the sun shades; dawn and
  dusk are low and warm. The overview stops down as it climbs, to 0.57 of the exposure
  under a high sun and 0.78 at dusk, so the land sits in the mid-tones below a brighter
  haze; a standing view keeps its exposure. A chunk's edge normals span the coarsest step
  meeting there and are taken again as its neighbours load, so no seam or ring boundary
  shows in the light. `setClock(frac)` eases to a time of day and holds it over the live
  clock for a beat of the tour; `setClock(null)` gives the clock back. On the far view in
  a headless Chrome: the same 4,937 draw calls, and no slower, 34 ms a frame against 37.

* The tour shows who a stop is about. At someone online, the latest death or a grave,
  their card stands at the side of the stage, over the map and the 3D view alike: the
  name, the class with its reason under the pointer, "online now" and where or when they
  were last seen, the pentagon and the four figures of their roster row, and their Viking
  turning as on their page (a phone keeps the figures and drops the Viking). One renderer
  serves every card, loaded with the first look; the card's turntable stops and its rig is
  freed as the card goes, and none of it outlasts a stopped tour. The class, the pentagon,
  the figures and the turntable moved out of the players page into `js/card.js`
  (`PlayerCard`), which both pages share; the players page draws exactly as before.
* A debugging and optimisation pass over the night's work, each change measured first.
  `/state` no longer carries the per-player tallies (a fifth of its bytes, rebuilt every
  request while anyone was on, read by no page: `/stats/players` has them). The chat
  dedupe hashed every RPC sent to everybody, every damage number among them; it now
  looks at chat alone. A fault in the second's player snapshot is logged once instead of
  silently stopping looks, gear or the stats saves for the run, and a pin placed from the
  site counts at once. A 3D chunk holds its objects or their bytes, not both (53 MB less
  on our world), and "model export done" names its longest frame and slowest step. The
  World page's minute takes 6 ms, not 235. The tour lands on its spot when the window is
  resized in flight, a stopped tour's last stop no longer stops the next tour circling or
  undoes layers shown since, and its fold of the sidebar is not kept as the site's
  choice. The 3D view frees a departed player's name tag and, two minutes after you
  leave it for the map, lets its world go (a 64 MB heap and 1,800 GPU geometries down
  to 9 MB and 17). Each page asks `/auth/me` once, not twice.
* A player's page draws them: above the pentagon, their Viking as the 3D view draws them,
  turning slowly on the map's ground colour under a key light (a still with reduced
  motion) -- the live look while they are online, the one the server remembers once they
  have gone, captioned "online now" or "as last seen"; a player with no look yet shows no
  card. The mod keeps each player's latest look (the JSON `/state` carries) and yaw in
  `stats.tsv` as an `l` line, taken as `/state`'s players are built and stored only when it
  changes; a player hidden from the map is kept too, since a look says nothing of where
  they are. `/stats/players` carries it as `look` with `yaw`; an older file reads as none.
  three.js loads with the first look shown. The rig builder moved out of the 3D view into
  `js/rig.js` (`RigBuilder`), which both pages share.
* Live players in 3D as themselves: the body they chose (the game's two), their skin and hair
  colour, their hair and beard, and what they wear and hold -- helmet, chest, legs, cape,
  belt, trinket, what is in each hand and what is slung on the back -- facing their way,
  their name above. The server runs no animation, so the mod plays the game's own idle clip
  once on a copy of the Player's rig and keeps that standing pose; the body and every
  garment are exported as skinned glTF (joints, weights, inverse binds) over the Player's
  skeleton in that pose, and a held or slung item hangs from its attach point as a node
  under its bone, so everything a look names stands together where it belongs. A look
  resolves as VisEquipment attaches it: a helmet hides the hair or swaps it for its hat
  cut, a sheathed bow goes to the bow's place on the back and a torch to its own. Skin and
  hair are tinted in the viewer, the body's chest and legs paint (the tunic or trousers a
  garment paints on the skin) laid over the skin there too. `/state` players carry `yaw`
  and `look` (the body, the colours, each slot's prefab and the parts to draw); the library
  gains a part per body and per worn item (category `rig`) as players are seen, about 20 KB
  each. A player is dressed again only when their look changes, from parts baked once and
  shared, so six players cost a few dozen draws. The index format is unchanged. The mod now
  builds against the game's `UnityEngine.AnimationModule.dll` too.
* The world's locations in 3D: the traders' camps, the Bog Witch's hut, crypt, cave and
  dwarven entrances, the boss altars, stone circles and runestones. The game networks a
  location only as a marker (a LocationProxy) and every game spawns the rest itself, so the
  marker goes out in `/objects` as the location's own prefab, at its place and turn, and
  the model library exports the location from the game's list of them: what every game
  spawns for itself, without who lives there (the traders are not drawn), what is
  networked on its own (chests, beehives, spawners, the ruins' and houses' own pieces --
  already drawn from their own records), lights, particles, the traders' force fields and
  a dungeon's rooms (kept 5000 m up), each part left to chance as its likelier outcome,
  merged by material into a dozen draws. On our world the sweep meets 128 kinds of
  location; 36 have a part of their own to draw, the rest are wholly networked. A big
  location exports over as many frames as `export_ms_per_frame` needs, its file written off
  the game thread. The index format is unchanged, so nothing already in the library is
  exported again; the locations join it as the sweep meets them. The mod now builds
  against the game's `SoftReferenceableAssets.dll` too.
* What each player holds and wears, for the site to tell an archer from a builder: `gear`
  in `/stats/players`, kept in `stats.tsv` (an older file reads as none yet). Once a
  second, alive and out of bed, the equipment the player's ZDO syncs adds a second to the
  family in each hand (`bow`, `sword`, `hammer`, `pickaxe` ... `none`, plus `twohanded`
  for a two-handed melee weapon) and to the chest's weight class (`light`, `medium`,
  `heavy` by movement penalty, `mage` for eitr-weave), all sorted by the item's own data
  so new gear sorts itself; `worn` is the latest set by prefab name. Hits on creatures
  that pass through the server count as `melee`, `ranged` or `magic`, with `backstab` on
  top when the creature was not yet alerted -- partial, since a hit on a creature the
  hitter's own game runs never reaches the server.
* Players have a class in three parts. The first word is a compound: what is fought with
  gives the prefix (battle for a two-hander, blade, shield, bow, knife, spell) and the body
  gives the root (mage, knight, warrior, rogue, brute), read from the clothes and the food
  -- the server never sees the foods, only the health, stamina and eitr they add, so the
  mod tallies a diet (`hearty`, `quick`, `eitr`, `balanced`, `none`) beside the armour and
  reports the latest food-borne figures. The two make Battlemage, Spellsword, Spellguard,
  Nightblade, Arcane Archer; Templar, Champion, Paladin, Marksman, Executioner; Warlock,
  Reaver, Fighter, Warden, Ranger; Enchanter, Berserker, Duelist, Skirmisher, Archer,
  Assassin; Shaman, Bruiser, Guardian, Hunter, Cutthroat; and the weapon itself colours a
  few: Arbalist, Vanguard, Huscarl, Hoplite, Raider. Before any gear is seen, twenty deaths
  at a worse pace than half the roster say Berserker. The second word is the trade, from
  the deeds: the roster's top builder is the Architect, a builder who also mines an
  Engineer, who also fells a Carpenter, who also farms a Homesteader; the roster's Slayer;
  Builders, Miners, Lumberjacks, Farmers; a roamer an Explorer or, a third of it at sea, a
  Seafarer; an Angler; a Wayfarer of the portals. Any part may be missing. The title sits
  on the roster row (under the name when the list is narrow) and on the player's page with
  its reason spelled out part by part, a Gear group shows what they were last seen wearing,
  and the spotlight turns through it. Time in hand is the working evidence: the server
  sees blows landed only on creatures another player owns, and skills never leave the
  character file. Faint until ten minutes have had something in hand; the worn set carries
  the two back slots so a mage building all night stays a mage.
* A continent from 2 km², not 4: this world's lands top out near 4.6, and the home lands of
  two to three are what people call continents. A land that grew into one keeps its stem
  (Ragnsey is Ragnsland), and a name given while it counted as an island still holds.
* A World page, second in the nav: the world at a glance (its day, explored share, what
  stands, what fell, the gates, the boats, the names, the deaths and the graves), the bosses,
  and its records -- largest continent and island, most and least explored land, tallest
  peak, longest river, biggest lake, widest bay, most built, deadliest and most gated place,
  the farthest build, the northern- and southernmost walked ground, the oldest grave, the
  latest name -- each with the way to the spot on the map and in 3D; danger by biome for
  everyone together; and every land in a table that sorts by area, walked share, pieces,
  gates, deaths or peak. A spotlight turns through the records against their runners-up.
  The tour visits the records too: the tallest peak circled, the biggest lake from above,
  and the longest river flown along its own course. The nav reads Map, World, Portals,
  Players, Plan.
* The tour has more to visit: the pins players placed, the places they named, a mountain,
  a bay, a walked corner with no build near it, and the sacrificial stones at the centre;
  a dozen stops a cycle with no more than two of a kind.
* A cinematic, for a screen left open: the ▶ among the map's buttons (or a link ending in
  `#tour`) starts a tour of the world's places one after another -- who is on, the portal
  hubs, the biggest build, where people die, the graves, the traders, a boat, the largest
  lands -- each flown to on the map with the layer that makes it worth seeing, held, then
  circled slowly in 3D where the ground is walked, a land from kilometres up. A caption
  names the place and why. Any touch ends it and puts the layers and the sidebar back.
  The 3D view gained `setSpin` and `setOrbitDist` for it. A stop switches its layer back off
  on the way out; the way between stops is flown high, where the fog reads as the world's
  shape rather than a black wall, and a spot still under fog is not flown to; in 3D the sea
  nobody has sailed is mist, so from far up water and the unknown read apart.
* Back to one face. The brand, a page's title and the map's capitals are the site's own sans
  in tracked capitals again; no font is served with the pages any more.
* The players page reads as a roster: one row per player with the four figures people
  compare, and a click opens the player's page beside it while the roster steps to the
  left as a column (on a phone, one or the other, with a way back). Sort and Show are
  dropdowns -- everyone, online now, seen this week, gear still out. The bosses wear
  icons of their own, in the order they fell, the standing ones dim. An online player's
  page says where they are in the world's own names, with a link to the spot. Each player
  has a pentagon -- Explorer (km, portal hops and km at sea together), Builder (pieces),
  Survivor (km per death), Slayer (kills), Harvester (trees and rocks together), each side
  a share of the roster's best, square-rooted -- small on the row and labelled on the page. Picking a player slides the page open, and its figures read
  in five groups: Survival, Journeys, Deeds, Building, Presence. A spotlight beside the pentagon
  turns through the player's figures one at a time -- where each sits among the roster, a
  share of everything, the closest call, the streak against the walk since -- with a small
  picture for each; a hover holds it, a click moves on. The map's sidebar has one for the world:
  how much is walked, who built the most of what stands, the boats by kind, the gates
  linked, the bosses down, where people die, the graves out there, the names given, the
  roster's farthest, deadliest and loudest, and the latest death. The sidebar's tiles are gone with it: the status row
  carries the explored share and the count online, small, and the felled trees and the boats
  afloat turn in the spotlight. The tab wears the world's name, the map plain and the other
  pages as "world · page".
* Kills, trees felled and rocks broken, per player: `kills`, `trees` and `rocks` in
  `/stats/players`, kept in `stats.tsv` (an older file reads as none yet). The server
  runs none of it, so it reads what passes through it: a hit sent to another player's
  creature, tree or rock carries its attacker, and the fall -- a destroy, or a piece
  breaking off a rock -- follows within ten seconds. A hit on something the hitter's own
  game runs never reaches the server; then the owner is credited, with a creature whose
  own record lists them among its attackers, a tree within 16 m of them, a rock piece
  within 32 m. Tamed creatures, logs and stumps count for nothing, and a creature that
  despawns is nobody's kill.
* Metres at sea show at last: the tally had kept them all along as biome class 0, and the
  players' JSON skipped that class. `biomes` now carries an `Ocean` entry.

## 2.14.0

* Discord moves into the mod, and talks back. `discord_bot_token` (with `discord_guild`,
  `discord_log_channel`, `discord_chat_channel`) posts what admins do on the site --
  a name, a pin, a setting -- straight from the mod, so the Worker no longer relays it.
  `chat_relay` (on by default) carries in-game chat to `discord_chat_channel` as
  **name**: text, and reads it back every three seconds, speaking a human's line in
  game as `[Discord] name: text` (bots, webhooks and the mod's own posts never echo
  back; at most five a poll, so a flood in Discord cannot flood the server; a restart
  never replays history). Joins, leaves and deaths keep going out the existing
  `discord_webhook`. `settings.html` offers a picker for the guild and its channels
  (`GET /discord/guilds`, `GET /discord/channels?guild=`) once a token is set, and
  falls back to a plain field otherwise; the token itself stays a masked field.
* Admins, and settings from the site. A signed-in member who owns the Discord guild, holds
  a role with its administrator permission, or holds the role the deployment names is an
  admin: the bar shows a gear, and `settings.html` lists the mod's settings with what each
  does and whether it applies live or at the next restart. The mod keeps what admins set in
  `settings.tsv` beside the map data and lays it over the BepInEx config, which the host
  rewrites on restart. `GET`/`POST /settings` behind the shared token and an `X-Admin` the
  Worker sets after checking the member's roles again.
* One face of the site's own: Marcellus SC, served with the pages (and by the mod: its
  static files know `woff2` now), for the brand, a page's title and the map's capitals --
  continents, islands, ranges. One weight, so the map's tiers rank by size and spacing.
  Everything else keeps the system sans. The scene repaints once the face has loaded.
* A hub opens close up on the portals page: click its name on its card or on its panel in
  the diagram and an overlay shows the render with the base's floor plan over it, each
  gate drawn where a player walks to it (linked bright, tagged-and-waiting amber, free
  dim), house pins naming the buildings, and a strip of the gates -- click one to light
  and centre it. Going through a gate from a card, a panel or a far place's node lands you
  in the far side's close up with the gate you arrive at lit, and a lit gate's mark takes
  you on through. Map and 3D from the header, Esc closes, `#in=x,z&at=<gate>` links in.
  The ⌖ at the end of a card's row still opens the map on that gate.
* `settings.html` reads in groups -- the map, players, chat and Discord, the 3D view, the
  mod -- and the chat relay keeps one poll on the wire at a time (a slow Discord call once
  let a line be spoken six times).
* On the first start after this update the mod exports every prefab the world holds for the
  3D view: a few minutes on the game thread's spare time, a burst of memory, and about
  70 MB beside the map data. `export_models = false` skips it, and the 3D view then has
  ground and nothing on it.
* The plan page draws the place names too, as a reference layer of its own.
* Every write from the site -- a name, a pin, a setting -- answers with the line it logged
  (superseded below: the mod itself posts that line to Discord now).
* The world in 3D, as a mode of the map entered the way Street View is: drag the figure
  from the map's buttons onto walked ground (on a phone, tap it and then the map), pick 3D
  under View in the Layers card, press **See in 3D** on a place or a pin, or follow a
  `#3d=x,z` link. You stand there at eye height looking north: drag to look, wheel or
  pinch to zoom the eye, WASD or the arrows to walk, a click on the ground to walk there,
  ⇅ for an orbit camera over the spot, ☼ for the light and what is shown. **Map** or Esc
  puts the map back where you stood. three.js and the renderer load on the first step
  into 3D and never with the map. Ported, with thanks, from
  [f00d4tehg0dz/valheim-webmap](https://github.com/f00d4tehg0dz/valheim-webmap) (MIT): the prefab exporter and glTF writer, the model
  library, the texture and mesh extractors that read the game's own asset files (the
  dedicated server cannot read its textures, nor most meshes), the per-chunk world objects,
  the instanced viewer with its canopy billboards, and the sky. three.js r180 is vendored
  under `web/vendor/three`.
* The ground under it is ours: `GET /height?cx=&cz=` answers a 256 m chunk
  (`cx = floor(x / 256)`) as 257 x 257 little-endian int16 decimetres of height, a metre
  apart, row 0 the south edge and column 0 the west, both edges included. It is built as
  the game builds its heightmap -- the generator's height for each 64 m zone's corner
  biomes, blended by smoothstep -- plus every zone's terraforming, decoded from its
  `_TerrainCompiler`; trees sampled against it stand within 4 cm of it.
* `GET /objects?cx=&cz=` answers a chunk's objects (OBJ1: prefab, position, rotation,
  scale, player-built), `GET /prefabs` the model library's index and `/models/<file>` a
  model or texture from it. `/objects` and `/height` answer 404 for a chunk nobody has
  walked, and nothing about one is even recorded. `/state` carries `rev.objects`,
  `rev.height` and `rev.models`, and `time` (the game day and the fraction of it the sun
  goes by). New config under `[Models]`: `export_models`, `extract_textures`,
  `extract_meshes`, `export_ms_per_frame`, `texture_max_size`, `object_categories`.
* The mod serves subfolders of `web/` now (the vendored three.js keeps its layout), and
  gzips text and the 3D chunks for a client that takes it.
* Mined rocks in 3D show only what is left of them. A rock mined in pieces (MineRock5, the
  `*_frac` rocks, and MineRock) keeps a health per hit area in its ZDO; `/objects` is now
  `OBJ2`, carrying a mask of the areas gone per rock, and `/prefabs` says which hit area
  each part of a rock's model belongs to (`rk`, `pa`; the library's index is format 6, so
  the first start re-exports every model, about a quarter of a minute). The rock the base
  was cut into at -296, 328 now stands as the two pieces the miners left.
* The overview flies: `W A S D` or the arrows carry the circled spot over the land the way
  the camera faces, `E` or `Space` raise it, `Q` or `Shift` alone lower it, and `Shift` with
  a key goes faster. Street view keeps its own keys.
* Whole continents in 3D. Pulled out, the view loads in rings from the camera -- everything
  close in, ground and the big things (pieces, boulders, trees as crowns or cones) out to a
  few kilometres, coarse ground to sixteen -- asks only for walked chunks, nearest first,
  and lets go of what no ring wants. `/height` takes `step` (1, 2, 4, 8 or 16 m between
  samples, each cached on its own), so the far rings cost a few hundred small requests. The
  haze and the camera reach kilometres, and the sea runs on to the horizon.

## 2.13.0

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
* What is here. `GET /at?x=&z=` answers for a spot in walked ground: the biome and
  height there, and the places it lies in -- lake or bay, range, biome region, landmass --
  each with how much of it has been walked, its elevation span, and the pieces and
  portals standing on it. Unwalked ground gets nothing. On the map a tap anywhere opens
  a place card from it, most particular place first with chips for the others; a tap on
  a name opens that place. Signed in, the name is editable in the card.
* Which names: the Names chip in the Layers card switches every label, and hovering it
  (or its caret, on a phone) opens the kinds -- continents and islands, holms, lakes and
  bays, rivers, mountain ranges, and each biome -- as switches of their own.
* The portals atlas for big hubs and several of them. A hub can be named on the page
  (a pencil beside its title, signed in; the name is kept by the server as a spot,
  `hub@x,z`, through `POST /names`, and a house pin still names a hub with no given
  name); the tags become a detail line. A big hub's panel shows the gates you can act
  on and folds the linked ones behind a count that opens on a tap; its card lists spokes
  by compass sector, nearest first, with a box to find a gate past ten. With more than
  one hub, chips at the top pick one: the view fits its reach and the rest fade. A
  tagged portal standing unlinked now draws one dashed line, to the only hub with a
  gate to spare, else the hub it last led to, else the nearest -- the mod remembers each
  portal's last twin (`portals.tsv`, `last` on the portal) across restarts.
* Stats that use distance as the yardstick, never time. Per player: metres walked and
  deaths by the biome underfoot, so the players page shows danger by biome as "1 death
  per N km"; close calls (health under a tenth and back over three tenths) with the
  lowest survived; the longest walk between deaths and the walk since the last one;
  corpse runs as recovered (back at the spot), failed (died within 200 m of it first),
  rescued (the grave emptied by someone else) and still out there, with the longest run.
  The world's boss keys become a board of who has fallen, in order. `/stats/players`
  carries `biomes`, `close_calls`, `lowest_hp`, `streak_m`, `runs`, `run_m` and `bosses`;
  `stats.tsv` keeps them. Each card links to that player's deaths on the map
  (`#details=deaths&who=<name>`), to the spot of the last one, and to the graves layer;
  the place card says how many died at a place.
* One view for every page. `MapCore.view` holds the window onto the map -- pan on a
  drag, wheel and pinch zoom, fits, one paint a frame, the middle kept when the sidebar
  folds, a tap told from a drag -- and the map, the atlas and the plan use it instead of
  three copies of the same code. The pages also share `plural` and `clamp` from the core.
* Every colour a page names is a token now: the map's hues (portal, trader, grave), the
  atlas's ground, the halos and shadows over the world, the modal, the scrollbar, a live
  card's edge, a nil figure. `site.css` documents the system; a restyle is an edit to
  `:root`.
* The bar offers a sign-in where the deployment's API has one (`/auth/me`); served by
  the mod alone it shows nothing.
* Pins from the site. `POST /pins` with `{"op":"add","x","z","type","text"}` places a pin
  and answers its id; `{"op":"edit","id"}` with any of `x`, `z`, `type`, `text` changes one;
  `{"op":"delete","id"}` takes it up. Every answer carries the pins as they now stand. Guarded
  like `/names`: a new pin's owner is the `X-User` who placed it, any signed-in member may
  change any pin, and the server log says who did what. The text is at most 60 characters
  and a pin only ever lands on walked ground. A site pin has `web` where a game pin has its
  placer's platform id, and a `w`-prefixed id, so the chat commands never reach it. On the
  map a pin opens its card on a tap -- type, label, who placed it -- and signed in it can be
  relabelled, retyped, moved (the next tap is the new spot) or deleted (confirmed in the
  card); the ground card offers **Pin here**. The chat commands now find and remove under
  the pin list's lock, and `pins.csv` is written from a snapshot, since the site writes
  from HTTP threads.
* Tests, where there were none. `dotnet test WebMap.Tests` runs the mod's logic from the
  built DLL on .NET 10 -- the geography and its names, hub names, the stats walk and its
  save, portal memory, pin writes from the site -- and `node --test` runs `map-core.js`'s
  pure functions; CI runs both on every push and pull request. `tools/check.mjs` replaces
  `shoot.mjs`: every page in a headless Chrome over a live server, one line per check and
  a failing exit when one fails. `TESTING.md` ranks what is covered and what is not.

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
