# NOCustomMaps

A BepInEx 5 plugin that loads custom maps into Nuclear Option. Maps are `.nomap` files built with
Unity. The plugin registers each one with the game, so it can be picked for missions and
multiplayer like the built-in maps.

## Installing

1. Install BepInEx 5 (x64, 5.4.23 or later) into the game folder and start the game once.
2. Put `NOCustomMaps.dll` in `BepInEx/plugins/NOCustomMaps/`.
3. Put your `.nomap` files in `BepInEx/plugins/NOCustomMaps/maps/`. The plugin creates this folder
   the first time the game starts with it installed.
4. Start the game. `BepInEx/LogOutput.log` lists each map it loaded, with the name missions use for it.

```
Nuclear Option/
└── BepInEx/
    └── plugins/
        └── NOCustomMaps/
            ├── NOCustomMaps.dll
            └── maps/
                └── yourmap.nomap
```

Maps are also read from a `CustomMaps` folder in the game's persistent data path, and from
`BepInEx/plugins/NOCustomMaps/addons/`, where the NOMM mod manager installs maps listed as add-ons of
this plugin.

## Multiplayer

The server and every player need the same `.nomap` file. A map registers as
`cm.<mapId>.<hash>`, where the hash comes from the file, so a player with a different build of the map
is turned away when joining. Rebuilding a map changes that name, and missions that use the map must be
pointed at the new one (`MapKey.Path` in the mission file).

## Airbases

Airfields drawn in Unity become airbases built into the map, with their runways, taxi exits and
capture zone. They come without buildings. To give an airbase spawns, place hangars, revetments,
shelters or helipads in the mission editor and add them to the airbase's **Buildings** list.

## Configuration

`BepInEx/config/com.javoski.custommaps.cfg`:

| Key | Default | Meaning |
|---|---|---|
| `MapDirectories` | empty | Extra folders to search for maps, separated by `;` |
| `DebugLogging` | `false` | More detail in the log |
| `ValidateOnLoad` | `true` | Skip a map that fails its checks |
| `DonorMap` | `Terrain1` | Built-in map whose terrain materials and music are used |

Delete the file to get new defaults after an update.

## Building

Requires the .NET SDK and the game installed in the default Steam folder.

```bash
dotnet test tests/CustomMaps.Tests/CustomMaps.Tests.csproj -c Release
```

```bash
dotnet build CustomMaps.csproj -c Release
```

The DLL is written to `bin/Release/netstandard2.1/NOCustomMaps.dll` and copied into the game's
`BepInEx/plugins/NOCustomMaps/`. Close the game first, since it locks the DLL. Set a different
destination with `-p:DeployDir=<path>`, or turn the copy off with `-p:DeployDir=`. If the game is
installed elsewhere, set `ManagedDir` and `BepInExDir` the same way.

## Source layout

| Path | Contents |
|---|---|
| `Plugin.cs`, `Patches/` | Entry point, settings and Harmony patches |
| `BundleLoader.cs`, `MapRegistrar.cs` | Finding, opening and registering maps |
| `MapFixups.cs` | Preparing each map: materials, grass and trees, lakes, decals |
| `AirbaseBuilder.cs` | Airbases and runways |
| `CityBuilder.cs`, `CityTileGate.cs`, `RoadNetworkFixup.cs` | Buildings and the road network |
| `MapDiagnostics.cs` | The check report written to the log for each map |
| `Core/` | File formats and logic shared with Unity tools, with no game or Unity code |
| `tests/` | Tests for `Core/` |

## Disclaimer

> This project is an unofficial community modification and is not affiliated with, sponsored by, or endorsed by Shockfront Studios Pty Ltd. Original Nuclear Option assets, vehicle designs, audio, and code are Copyright (c) 2026 Shockfront Studios Pty Ltd. All rights reserved. Nuclear Option and Shockfront Studios are trademarks or registered trademarks of Shockfront Studios Pty Ltd. Original mod content and all other trademarks belong to their respective owners.

## License

MIT. See `LICENSE`.
