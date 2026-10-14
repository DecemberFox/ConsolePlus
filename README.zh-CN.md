<div align="right">

[English](README.md) |
简体中文

</div>

# 控制台拓展 Mod
## 描述
**控制台拓展** 是一款为《未知伤亡》开发的 BepInEx 插件，为游戏添加更多开发者控制台命令，方便实时查看和改动正在运行的世界。

## 安装
1. 构建：`dotnet build ConsoleExtension.slnx`（或用 Visual Studio 构建 `ConsoleExtension/ConsoleExtension.csproj`）。
2. 把 `ConsoleExtension/bin/Debug/ConsoleExtension.dll` 复制到 `<游戏目录>/BepInEx/plugins/`。
3. 启动游戏，在开发者控制台里输入命令。

## 输出与日志
控制台里的文字没法选中复制，所以每条命令的输出都会同时写进 `BepInEx/LogOutput.log`，直接从日志里复制即可。注意 `BepInEx/config/BepInEx.cfg` 的 `[Logging.Disk] AppendLog` 决定这个文件是否在每次启动时被覆盖；`dump` 还会额外在导出目录里写一份不会被覆盖的 `dump.log`。

`position` 参数接受 `cursor`（鼠标位置，省略时即默认值）、`player`、`random`，或者 **世界坐标** `x,y` —— `0,0` 是地图中心，两轴范围都是 ±512。

## 功能
新增 9 个命令：

- `time <子命令> [数值]`
  - 控制游戏时间尺度：暂停、放慢或加快游戏时间。
  - 子命令：`force`（开关强制覆盖，无需数值）、`query`、`freeze`、`unfreeze`、`rate <浮点>`、`step <浮点秒>`。
  - **注意**：`force` 打开时，本命令会覆盖其他所有时间尺度设置，包括游戏自身的暂停。
- `setallfield <字段> <值>`
  - 一次性把玩家某条肢体字段设到全部 15 条肢体上（补全会列出 `Limb` 的基础类型字段）。
- `setblock [方块id] [位置] [是否更新]`
  - 替换一个方块；方块 id 默认 0（空气），位置默认光标处，是否更新默认 true（传 `false` 只写数据、不刷新区块）。
- `placestructure <预制体> [位置] [方块概率]`
  - 放置结构预制体（补全里有常用清单），方块概率默认 1。
  - 方块层按游戏自己的规则盖：根节点的 Tilemap，否则第 0 个子节点。属于"背景物体"的 Tilemap（带 `BuildingEntity`，或名字是 `BACKGROUND` / `Back`）不会被写进 `worldBlocks`，而是实例化成对象挂到 `worldGrid` 下 —— 所以复用方块 tile 的背景层不会再覆盖地形。
  - 带 `Item` / `BuildingEntity` / `DOSPAWN` 的子物体按游戏生成结构时的同一套规则生成。
- `update [位置]`
  - 更新指定位置的区块；省略位置则更新整个世界。
- `getblock [位置]`
  - 打印该位置的方块 id（默认光标处）。
- `setxp <str|res|int> <数值> [point|level]`
  - 设置经验，默认按点数，`level` 按等级（上限 40），并刷新等级边界。
- `destroy <名字> [序号]`
  - 销毁名字包含该字符串的第一个物体（可用序号选其他，序号会被夹到有效范围内）。
  - **注意**：销毁物体有可能让游戏崩溃。
- `dump <block|fluid|object|all> [目录名]`
  - 把当前地图导出到 `BepInEx/plugins/ConsoleExtension/Dump/<种子输入>_L<层号>/`（第二个参数可自定义目录名），子命令省略时等于 `all`。
  - `dump block` → `blocks.bin`（ushort 小端，x 外层 y 内层）+ `meta.json` + `tiles.json` + `blocks-histogram.csv`
  - `dump fluid` → `fluid.bin`（每格 1 字节，索引与 `FluidManager.GetLiquid` 一致）+ `fluid-meta.json`（尺寸、液体 id↔名字、颜色、数量）
  - `dump object` → `objects.jsonl`（每行一个 JSON：类别、组件类型、英文 id、显示名、世界坐标与方块坐标、层号、是否激活）
  - 每次导出还会在同一目录写一份 `dump.log`。

## 工具
`DumpReader`（`DumpReader/DumpReader.csproj`）可以在不启动游戏的情况下解析这些导出文件：

```
dotnet run --project DumpReader -- blocks  <dump目录> [--png out.png] [--csv out.csv] [--palette block-colors.json]
dotnet run --project DumpReader -- fluid   <dump目录> [--png out.png] [--csv out.csv]
dotnet run --project DumpReader -- objects <dump目录> [--csv out.csv]
```

不给 `--png` / `--csv` 时只打印统计（方块直方图、液体数量、对象按类别与类型的汇总）。`blocks` 默认用游戏贴图的平均色出图（`DumpReader/block-colors.json`），可以用 `--palette` 换一份配色表。
