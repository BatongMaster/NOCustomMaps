# NOCustomMaps

A BepInEx 5 plugin that loads custom maps into Nuclear Option. Maps are `.nomap` files built with
Unity, or `.nomapz` files: the same map compressed to less than half the size, which needs
NOCustomMaps 1.4 or later. The plugin registers each one with the game, so it can be picked for
missions and multiplayer like the built-in maps.

## Installing

### NOMM

1. Search on NOMM for "Swiss Alps" and download it (it will take a while don't worry, it's a 730 MB download)
2. Start the game. 'BepInEx/LogOutput.log' should show 'v1.4.0 ready.' and a line like
'swissalps-<version>.nomapz -> cm.swissalps.<hash8> ("Swiss Alps", 199680x199680 m)'.

###  How to install manually (if you don't use NOMM)

1. Download `swissalps-<version>.zip` from this repository's releases.
2. Create a folder named `NOCustomMaps` in `BepInEx/plugins/`
3. Copy the map (.nomap or .nomapz) into `BepInEx/plugins/NOCustomMaps/maps/` under your Nuclear Option
   installation. (`CustomMaps/` under the game's persistent data path works too; the plugin looks in both.)

   Maps released as `.nomapz` (Swiss Alps from 0.4.0 on) need NOCustomMaps 1.4 or later. Older versions
   do not see them, and the game then only says the map is not installed.
4. Start the game once and look in `BepInEx/LogOutput.log` for the line the plugin prints when it
   registers the map:

   ```
   swissalps-<version>.nomapz -> cm.swissalps.<hash8> ("Swiss Alps", 199680x199680 m)
   ```

   The `cm.swissalps.<hash8>` name is how missions refer to the map. The eight hex digits are the
   start of the bundle's SHA-256, so they identify this exact build.
5. Open the Mission Editor and pick the map's tile under **New**, after Heartland and Ignus Archipelago,
   then **Create New** or **Create Default** (see [Mission editor](#mission-editor)).

**Multiplayer:** the server and every client need the same build of the map. A `.nomap` and the
`.nomapz` made from it register under the same name, so either file of one build will do. The hash in
the registered name is the version handshake; a client with a different build of the map fails to
match and is told so at join time.

Maps installed by the NOMM mod manager (in `addons/`) are found too.

## Mission editor

The **New** tab (from the main menu's Mission Editor, or File > New in the editor) shows every installed
custom map as a tile after Heartland and Ignus Archipelago, sorted by name, in a list that scrolls. Pick
one, then **Create New** or **Create Default**: the mission is made on that map, and its file names it as
`cm.<mapId>.<hash8>`. Two maps with the same name show their version (or map id) on the tile. A map that
failed to load this session is greyed out and marked "(failed to load)".

## Multiplayer

The server and every player need the same build of the map, as its `.nomap` or its `.nomapz`, and
should run the same release of this plugin. A map registers as `cm.<mapId>.<hash>`, where the hash comes
from the `.nomap` (a `.nomapz` carries the hash of the `.nomap` it was made from), so a player with a
different build is turned away when joining. Rebuilding a map changes that name, and missions that use
it must be pointed at the new one (`MapKey.Path` in the mission file).

## The map screen

On a custom map wider than about 75 km, the map screen (**M**, and the mission editor's Map tab)
zooms out until the whole map fits (Swiss Alps: about 2.6 times as far out as the game allows), and a
map following your aircraft keeps following it. The built-in maps zoom as before.

## AI pilots in the mountains

On a custom map, AI aircraft and helicopters look along their whole path for the ground ahead and
climb in time to clear it, in transit, in combat and when turning in to land; an aeroplane lands from
the other end of a runway when mountains crowd its approach. Tested with the "Escalating the Alps" mission
and I haven't found any issues with the AI crashing into the terrain.

## Building

Requires the .NET SDK and the game installed in the default Steam folder.

```bash
dotnet test tests/CustomMaps.Tests/CustomMaps.Tests.csproj -c Release
```

```bash
dotnet build CustomMaps.csproj -c Release
```

The DLL is copied into the game's `BepInEx/plugins/NOCustomMaps/`; close the game first. Turn the copy
off with `-p:DeployDir=`, and if the game is installed elsewhere set `ManagedDir` and `BepInExDir`.

## Disclaimer

> This project is an unofficial community modification and is not affiliated with, sponsored by, or endorsed by Shockfront Studios Pty Ltd. Original Nuclear Option assets, vehicle designs, audio, and code are Copyright (c) 2026 Shockfront Studios Pty Ltd. All rights reserved. Nuclear Option and Shockfront Studios are trademarks or registered trademarks of Shockfront Studios Pty Ltd. Original mod content and all other trademarks belong to their respective owners.

## License

MIT. See `LICENSE`.
