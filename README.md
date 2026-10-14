<div align="right">

English |
[简体中文](README.zh-CN.md)

</div>

# Console Extension
## Description
**Console Extension** is a BepInEx plugin for *Casualties: Unknown* that adds developer console commands, so you can inspect and tweak the running world in real time.

## Installation
1. Build it: `dotnet build ConsoleExtension.slnx` (or build `ConsoleExtension/ConsoleExtension.csproj` in Visual Studio).
2. Copy `ConsoleExtension/bin/Debug/ConsoleExtension.dll` into `<game>/BepInEx/plugins/`.
3. Start the game and type the commands into the developer console.

## Output and logs
Console text cannot be selected, so every command writes the same lines into `BepInEx/LogOutput.log` — copy the output from there. Note that `BepInEx/config/BepInEx.cfg` → `[Logging.Disk] AppendLog` decides whether that file is overwritten on each game start. `dump` additionally writes a persistent `<dump folder>/dump.log`.

`position` arguments accept `cursor` (mouse, and the default when the argument is omitted), `player`, `random`, or **world** coordinates `x,y` — `0,0` is the center of the map, which spans ±512 on both axes.

## Features
9 new commands were added:

- `time <subcommand> [value]`
  - Control the game's time scale: pause, slow down or speed up in-game time.
  - Subcommands: `force` (toggle the override, no value), `query`, `freeze`, `unfreeze`, `rate <float>`, `step <float> seconds`.
  - **Warning**: while `force` is on, the command overrides every other time scale assignment, including the game's own pauses.
- `setallfield <field> <value>`
  - Sets one field of the player's limbs on all 15 limbs at once (tab-completion lists the primitive fields of `Limb`).
- `setblock [blockId] [position] [update]`
  - Replaces a block; `blockId` defaults to 0 (air), `position` to the cursor, `update` to true (`update=false` writes without refreshing the chunk).
- `placestructure <prefab> [position] [blockChance]`
  - Places a structure prefab (see the tab-completion list), `blockChance` defaults to 1.
  - Block tiles are stamped the way the game does it: the block layer is the root tilemap, else child 0. Tilemaps belonging to background objects (they carry `BuildingEntity`, or are named `BACKGROUND` / `Back`) are never written into `worldBlocks`, and are instead instantiated as objects under `worldGrid` — so a background layer that reuses block tiles cannot overwrite the terrain.
  - Children carrying `Item` / `BuildingEntity` / `DOSPAWN` are spawned exactly like the game does when it generates structures.
- `update [position]`
  - Updates the chunk at `position`, or the whole world when the position is omitted.
- `getblock [position]`
  - Prints the block id at `position` (default: cursor).
- `setxp <str|res|int> <amount> [point|level]`
  - Sets experience in points (default) or levels (capped at 40) and refreshes the level boundaries.
- `destroy <name> [index]`
  - Destroys the first object whose name contains `name` (or another one via `index`, clamped to the list).
  - **Warning**: destroying objects can crash the game.
- `dump <block|fluid|object|all> [folder]`
  - Dumps the current world into `BepInEx/plugins/ConsoleExtension/Dump/<seed input>_L<layer>/` (`folder` overrides that name). The subcommand defaults to `all`.
  - `dump block` → `blocks.bin` (ushort little-endian, x outer / y inner) + `meta.json` + `tiles.json` + `blocks-histogram.csv`
  - `dump fluid` → `fluid.bin` (1 byte per cell, same indexing as `FluidManager.GetLiquid`) + `fluid-meta.json` (dimensions, liquid id-to-name map, colors, counts)
  - `dump object` → `objects.jsonl` (one JSON per line: category, component type, English id, display name, world and block coordinates, layer, active)
  - Every run also writes `dump.log` in the same folder.

## Tools
`DumpReader` (`DumpReader/DumpReader.csproj`) parses the dumped files without the game:

```
dotnet run --project DumpReader -- blocks  <dump folder> [--png out.png] [--csv out.csv] [--palette block-colors.json]
dotnet run --project DumpReader -- fluid   <dump folder> [--png out.png] [--csv out.csv]
dotnet run --project DumpReader -- objects <dump folder> [--csv out.csv]
```

Without `--png` / `--csv` it only prints a summary (block histogram, liquid counts, objects grouped by category and type). `blocks` renders with the average colors of the game's block textures (`DumpReader/block-colors.json`); override it with `--palette`.
