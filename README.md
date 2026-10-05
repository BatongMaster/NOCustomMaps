# NOCustomMaps

A BepInEx 5 plugin that loads custom maps into Nuclear Option. Maps are `.nomap` files built with
Unity. The plugin registers each one with the game, so it can be picked for missions and
multiplayer like the built-in maps.

## Installing

### NOMM

1. Search on NOMM for "Swiss Alps" and download it (it will take a while don't worry, it's a 1.3 GB download)
2. Start the game. 'BepInEx/LogOutput.log' should show 'v1.3.0 ready.' and a line like
'swissalps-0.3.0.nomap -> cm.swissalps.aeb19792 ("Swiss Alps", 199680x199680 m)'.

###  How to install manually (if you don't use NOMM)

1. Download `swissalps-0.3.0.nomap` from this repository's releases. It is about 1.3 GB.
2. Create a folder named `NOCustomMaps` in `BepInEx/plugins/`
3. Copy the map (.nomap) into `BepInEx/plugins/NOCustomMaps/maps/` under your Nuclear Option installation.
   (`CustomMaps/` under the game's persistent data path works too; the plugin looks in both.)
4. Start the game once and look in `BepInEx/LogOutput.log` for the line the plugin prints when it
   registers the map:

   ```
   swissalps-0.3.0.nomap -> cm.swissalps.<hash8> ("Swiss Alps", 199680x199680 m)
   ```

   The `cm.swissalps.<hash8>` name is how missions refer to the map. The eight hex digits are the
   start of the bundle's SHA-256, so they identify this exact build.
5. In a mission, set `MapKey.Path` to that name and `MapKey.Type` to `GameWorldPrefab`. Missions
   live in `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions\<name>\<name>.json`.

**Multiplayer:** the server and every client need the identical `.nomap` file. The hash in the
registered name is the version handshake - a client with a different build of the map fails to
match and is told so at join time, instead of silently disagreeing about where the ground is.

Maps installed by the NOMM mod manager (in `addons/`) are found too. Keep only one build of each map
installed: Unity refuses a second one, and the log says so.

## Making maps

The Unity tools and a guide to making maps are in a separate repository,
[NOCustomMapsCreator](https://github.com/BatongMaster/NOCustomMapsCreator).

## Multiplayer

The server and every player need the same `.nomap` file and should run the same release of this
plugin. A map registers as `cm.<mapId>.<hash>`, where the hash comes from the file, so a player with a
different build is turned away when joining. Rebuilding a map changes that name, and missions that use
it must be pointed at the new one (`MapKey.Path` in the mission file).

## Airbases

Airfields drawn in Unity become airbases with their runways, runway paint, AI taxi roads and capture
zone. They come without buildings: place hangars, revetments, shelters or helipads in the mission
editor and add them to the airbase's **Buildings** list. A mission can also move an airbase's flag,
change its capture range and edit its AI roads in the mission editor; **Remove overrides** hands them
back to the map.

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
