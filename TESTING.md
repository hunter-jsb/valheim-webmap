# Testing

| Layer | Runs | Reaches |
|---|---|---|
| `dotnet test WebMap.Tests` | CI, every push and PR | the mod's logic, from the built `WebMap.dll` on .NET 10 |
| `node --test` | CI, every push and PR | `map-core.js`'s pure functions |
| `node tools/check.mjs <proxy> <devtools port>` | by hand, over a live server | every page in a headless Chrome |

The .NET layer runs the net48 DLL on CoreCLR. Plain IL runs; anything that reaches the
engine throws, so the tests switch Unity's logger off and feed the mod through one marked
block per class (`Features.ResetForTests`/`AnalyseForTests`, `Portals.ResetForTests`/
`ObserveForTests`, the `MapDataServer(bool)` constructor). A test names the failure it
guards against; there is one per failure, not one per branch.

## What a regression would do on the live server, worst first

1. **The Harmony patches on the game's network path** -- `ZRoutedRpcRoutePatch.Prefix`
   and `DeedsRoutePatch.Prefix` (every RPC the server forwards), `DeedsGonePatch.Prefix`
   (every destroy), `ZRoutedRpcPatch`, `DeathWatch.Postfix`, the `ZNet` join and leave
   patches. A slow path lags everyone, a throw drops chat or deaths, and the
   fake server player the README warns about stops anyone joining. **Nothing covers
   them**; only a running server does (`./testserver.sh`).
2. **The sweep's game-thread walk** (`StructureMap`): too many ZDOs a frame hitches the
   game, a yield inside a sector list skips ZDOs. **Nothing**; `/structures/stats` measures
   it live.
3. **The write routes' token gate** -- `SiteWrite` and `/announce` in
   `ProcessSpecialRoutes`. A regression opens pin, name and announcement writes to anyone.
   **Nothing**: the constructor starts the broadcast timer at once, and its first tick
   throws before the server listens, which kills a test host. Starting the timer in
   `ListenAsync` would make the routes testable over real HTTP.
4. **Pins.** The chat commands (`!pin`, `!undoPin`, `!deletePin` in
   `ZRoutedRpcPatch.Observe`, whose per-player trim once deleted other players' pins):
   **nothing**, they read a `ZPackage`. Site writes (`MapDataServer.WritePin`): **dotnet
   test** -- add, edit, delete; every refusal leaves the list and `pins.csv` untouched; ids
   never collide; commas in `X-User`; unwalked ground.
5. **The viewer.** A broken page is seen by everyone at once. **check.mjs** loads every
   page and checks markers, names at a mid zoom, a tapped name opening its place card, the
   names flyout, hubs and one dial line per tagged unlinked portal, a card per player and a
   player's rig standing on their page, the plan, and the tour: a flight resized under it, a stopped tour's last stop. **node --test** covers the core: pins, pieces, the view's geometry, name hits,
   escaping, ages, vehicles. Not reached: anything signed in (renaming, the pin card, hub
   names), the plan's tools and shared links, sorting and the boss board, and drawing
   itself beyond what it leaves behind. Live players in 3D: which part a look hangs on
   which joint and the skin a part's glTF carries, **dotnet test**; when a player is dressed
   again, **node --test**; a rig built on a player's page, **check.mjs**; the standing pose,
   the export and how the rig looks, **nothing**: a running server (`./testserver.sh`, where
   nobody can log in) and a player's look fed to the view by hand.
6. **Fog reveal and save** (`UpdateFogTextureLoop`, `SaveFogTexture`): the map stops
   revealing, or forgets on a restart. **Nothing**.
7. **Spoilers.** What stands in unwalked ground must not be reported. Portals, `/at` and
   the 3D view's chunks (`/objects`, `/height`, gated on `MapFog.ChunkExplored`):
   **dotnet test**. Graves, vehicles and traders (and Hildir's camps by name):
   **nothing**, they need ZDOs or `ZNetScene`.
   The 3D ground's terraforming (`TerrainPatches.Decode`), a location's marker sent as the
   location and what its model leaves out: **dotnet test**; its generator
   heights and the model export need the engine: **a running server** (`./testserver.sh`
   on a copied world, then the map's 3D mode in a headless Chrome, `tools/check.mjs`).
8. **Stats** (`Seen`, `Death`, `PublishSweep`, `ObserveKeys`, `stats.tsv`): **dotnet test**
   -- distance against a hop, close calls, the four ends of a corpse run, bosses, metres at
   sea, the save and a file from before kills. **Deeds**' crediting (`Hit`, `AreaBroken`,
   `Gone`): **dotnet test** -- by kind, the window, the last hitter, the owner standing in.
   Reading hits off the wire and classifying what fell: **nothing**; only a running server.
   **Gear**: an item's data to a hand family and a chest class, a second's tally, the worn
   set, hits by kind and backstab, the save and an older file: **dotnet test**. Reading the
   equipment off a player's ZDO and the prefab behind a hash: **nothing**. The look last
   seen: its save, an older file, and the same look kept without a rebuild, **dotnet test**.
   Two faults found:
   - Mistlands is biome class 10, one past `Stats.Biomes = 10`, so metres and deaths there
     are dropped. `AWalkInTheMistlandsCounts` is written and skipped; `Biomes = 11` passes it.
   - A death during a sweep's walk can lose its corpse run: `PublishSweep` drops an open
     spot no grave was seen at, and a walk that passed the spot before the tombstone
     arrived saw none. Untested; a spot newer than the sweep's start should wait for the next.
9. **Portal memory and geography.** `Portals.Finish`, `portals.tsv` across a restart, the
   hub names in `/portals`; the landmasses, lakes, bays, ranges and names `Features`
   finds, given names, hub names by nearness, `ClassAt`, the body reader: **dotnet test**
   on synthetic grids. The generator sampling (`Build`) and `names.tsv` loading (`Load`
   starts a coroutine) are not reached.
10. **The cosmetic layers** -- `Pieces`, `Trails`, `ForestMap`, the structures raster,
    `Chart`: **nothing**. A regression is a wrong picture, seen at once.
11. **Discord.** The REST client's retry-once-on-429 and the message parser (display
    name priority, bot/webhook detection): **dotnet test**, against a faked
    `Discord.Transport`. The relay's skip and its five-a-poll cap: **dotnet test**, on
    `ChatRelay.LinesToSpeak` directly. The `Timer` loop itself, the Harmony hookup that
    calls `Discord.PostChat`, and the settings picker's two routes: **nothing**; those
    need a running server and a real bot token.
