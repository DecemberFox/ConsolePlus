// DumpCommand.cs
// `dump` 命令的实现：把当前地图的方块 / 液体 / 场景对象导出到
// BepInEx/plugins/ConsoleExtension/Dump/<名字>/ 下。
//
// 用法：
//   dump block  [名字]   方块：blocks.bin（二进制，ushort 小端，x 外层 y 内层）+ meta.json + tiles.json
//   dump fluid  [名字]   液体：fluid.bin（二进制，每格 1 字节）+ fluid-meta.json
//   dump object [名字]   对象：objects.jsonl（每行一个 JSON，带类别/英文 id/坐标）
//   dump all    [名字]   以上三个都导
//
// [名字] 省略时用 <种子输入>_L<层号>，与 SeedMap.Dump 的命名一致；
// blocks.bin 的布局与 SeedMap.Dump 完全相同，因此 SeedMapViewer 的渲染器可以直接读。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

internal static class DumpCommand
{
    /// <summary>对象类别白名单：组件类型名 → 类别。不在表里的组件不会被导出（例如 ExplosionParticle 这类特效）。</summary>
    private static readonly Dictionary<string, string> CategoryOfType = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 掉落物 / 手上的物品
        { "Item", "item" },
        { "CustomItemBehaviour", "item" },
        { "BatteryItem", "item" },
        { "LightItem", "item" },
        { "WaterContainerItem", "item" },
        { "BoughtItem", "item" },

        // 实体建筑（不是由物块拼出来的结构）
        { "BuildingEntity", "building" },
        { "TraderScript", "building" },
        { "BatteryRecharger", "building" },
        { "Climbable", "building" },
        { "ItemLootPool", "building" },

        // 陷阱 / 危险物
        { "BearTrap", "trap" },
        { "DamagingCrate", "trap" },
        { "StalactiteDropper", "trap" },
        { "TurretScript", "trap" },
        { "GrabberPlant", "trap" },
        { "BananaPlantSlip", "trap" },

        // 敌对生物
        { "CrystalEnemy", "enemy" },
        { "SpiderHandler", "enemy" },
        { "DroneScript", "enemy" }
    };

    internal static void Run(string[] args, Action<string> log)
    {
        string sub = (args.Length > 1 ? args[1] : "all").ToLowerInvariant();
        string name = args.Length > 2 ? args[2] : null;

        if (WorldGeneration.world == null)
            throw new Exception("No world is loaded. Try starting a game?");

        if (sub != "block" && sub != "fluid" && sub != "object" && sub != "all")
            throw new Exception($"Unknown subcommand '{sub}'. Use 'block', 'fluid', 'object' or 'all'.");

        string dir = ResolveOutputDir(name);
        Directory.CreateDirectory(dir);

        // 除了控制台（和插件日志），同一条也存一份到 dump 目录里，方便直接复制
        var lines = new List<string>();
        Action<string> tee = message =>
        {
            log(message);
            lines.Add(message);
        };

        tee($"Dumping to {dir}");

        if (sub == "block" || sub == "all")
            DumpBlocks(dir, tee);
        if (sub == "fluid" || sub == "all")
            DumpFluid(dir, tee);
        if (sub == "object" || sub == "all")
            DumpObjects(dir, tee);

        string logPath = Path.Combine(dir, "dump.log");
        tee($"Dump finished. Log -> {logPath}");
        File.WriteAllLines(logPath, lines, new UTF8Encoding(false));
    }

    // ---------------------------------------------------------------- 目录

    private static string ResolveOutputDir(string name)
    {
        string plugins = null;
        try { plugins = BepInEx.Paths.PluginPath; } catch { }

        if (string.IsNullOrEmpty(plugins))
        {
            // 退路：本插件 DLL 所在目录（通常是 plugins\ConsoleExtension）
            plugins = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        string baseDir = string.Equals(Path.GetFileName(plugins.TrimEnd('\\', '/')), "ConsoleExtension", StringComparison.OrdinalIgnoreCase)
            ? plugins
            : Path.Combine(plugins, "ConsoleExtension");

        return Path.Combine(baseDir, "Dump", name ?? DefaultName());
    }

    private static string DefaultName()
    {
        string seedInput = ReadSeedInput();
        int layer = ReadWorldInt("biomeDepth");
        return SanitizeFileName(string.IsNullOrEmpty(seedInput) ? "seed" : seedInput) + "_L" + layer.ToString(CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- block

    private static void DumpBlocks(string dir, Action<string> log)
    {
        object world = WorldGeneration.world;
        Type worldType = world.GetType();

        var blocks = AccessTools.Field(worldType, "worldBlocks")?.GetValue(world) as ushort[,];
        if (blocks == null)
            throw new Exception("worldBlocks is not generated yet (the world is still generating?)");

        int width = blocks.GetLength(0);
        int height = blocks.GetLength(1);

        // 与 SeedMap.Dump 完全相同的布局：x 外层、y 内层、ushort 小端
        using (var stream = new FileStream(Path.Combine(dir, "blocks.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new BinaryWriter(stream))
        {
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    writer.Write(blocks[x, y]);
        }

        string[] tileNames = ReadTileNames(world, worldType);
        WriteMeta(dir, world, worldType, width, height, tileNames.Length);
        WriteTiles(dir, tileNames);

        var counts = new Dictionary<ushort, int>();
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                ushort id = blocks[x, y];
                counts.TryGetValue(id, out int c);
                counts[id] = c + 1;
            }
        }

        WriteBlockHistogram(dir, counts, tileNames);

        log($"block: blocks.bin {width}x{height} ({width * height * 2 / 1024 / 1024} MB), "
            + $"{counts.Count} distinct ids, " + DescribeTop(counts, tileNames, 4));
    }

    private static void WriteMeta(string dir, object world, Type worldType, int width, int height, int tileCount)
    {
        int currentSeed = ReadSeedValue();
        int layer = ReadWorldInt("biomeDepth");
        int totalTraveled = ReadWorldInt("totalTraveled");
        int chunkWidth = ReadWorldInt("chunkWidth");
        int chunkHeight = ReadWorldInt("chunkHeight");
        int layerSeed = unchecked(currentSeed + totalTraveled * 265443576);

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"plugin\": \"ConsoleExtension " + PluginVersion() + "\",");
        sb.AppendLine("  \"dumpedAtUtc\": \"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",");
        sb.AppendLine("  \"seedInput\": \"" + Escape(ReadSeedInput()) + "\",");
        sb.AppendLine("  \"currentSeed\": " + currentSeed.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"biomeDepth\": " + layer.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"totalTraveled\": " + totalTraveled.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"layerSeed\": " + layerSeed.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"width\": " + width.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"height\": " + height.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"chunkWidth\": " + chunkWidth.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"chunkHeight\": " + chunkHeight.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"tilesCount\": " + tileCount.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"blocksBin\": \"ushort little-endian, x outer / y inner, row = x * height + y\"");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(dir, "meta.json"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string[] ReadTileNames(object world, Type worldType)
    {
        var tiles = AccessTools.Field(worldType, "tiles")?.GetValue(world) as Array;
        if (tiles == null)
            return new string[0];

        var names = new string[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
            names[i] = (tiles.GetValue(i) as UnityEngine.Object)?.name ?? "";

        return names;
    }

    private static void WriteTiles(string dir, string[] names)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < names.Length; i++)
        {
            sb.Append("  { \"index\": ").Append(i.ToString(CultureInfo.InvariantCulture))
              .Append(", \"name\": \"").Append(Escape(names[i])).Append("\" }");
            sb.AppendLine(i == names.Length - 1 ? "" : ",");
        }
        sb.AppendLine("]");
        File.WriteAllText(Path.Combine(dir, "tiles.json"), sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteBlockHistogram(string dir, Dictionary<ushort, int> counts, string[] tileNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,name,count");
        foreach (KeyValuePair<ushort, int> pair in counts.OrderByDescending(p => p.Value))
        {
            string name = pair.Key < tileNames.Length ? tileNames[pair.Key] : "";
            sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(name.Replace(",", " ")).Append(',')
              .Append(pair.Value.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }

        File.WriteAllText(Path.Combine(dir, "blocks-histogram.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string DescribeTop(Dictionary<ushort, int> counts, string[] tileNames, int take)
    {
        return string.Join(", ", counts.OrderByDescending(p => p.Value).Take(take)
            .Select(p => (p.Key < tileNames.Length && tileNames[p.Key].Length > 0 ? tileNames[p.Key] : "#" + p.Key) + "=" + p.Value));
    }

    // ---------------------------------------------------------------- fluid

    private static void DumpFluid(string dir, Action<string> log)
    {
        FluidManager manager = FluidManager.main;
        if (manager == null)
            throw new Exception("FluidManager.main is null (no world loaded?)");

        byte[,] fluid = manager.fluid;
        if (fluid == null)
            throw new Exception("FluidManager.main.fluid is null");

        int width = fluid.GetLength(0);
        int height = fluid.GetLength(1);

        // 每格 1 字节，x 外层、y 内层（与 FluidManager.GetLiquid(x, y) 的索引一致）
        using (var stream = new FileStream(Path.Combine(dir, "fluid.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[width];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                    buffer[y] = fluid[x, y];
                stream.Write(buffer, 0, height);
            }
        }

        var counts = new Dictionary<byte, int>();
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                byte id = fluid[x, y];
                counts.TryGetValue(id, out int c);
                counts[id] = c + 1;
            }
        }

        WriteFluidMeta(dir, manager, width, height, counts);

        log($"fluid: fluid.bin {width}x{height}, "
            + string.Join(", ", counts.OrderByDescending(p => p.Value).Take(5)
                .Select(p => FluidName(p.Key) + "=" + p.Value)));
    }

    private static void WriteFluidMeta(string dir, FluidManager manager, int width, int height, Dictionary<byte, int> counts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"dumpedAtUtc\": \"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",");
        sb.AppendLine("  \"width\": " + width.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"height\": " + height.ToString(CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"fluidBin\": \"byte per cell, x outer / y inner, row = x * height + y (same indexing as FluidManager.GetLiquid)\",");
        sb.AppendLine("  \"idToName\": {");

        var map = FluidManager.WorldFluidToLiquidID;
        if (map != null)
        {
            var keys = map.Keys.ToList();
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                int id = keys[i];
                sb.Append("    \"").Append(id.ToString(CultureInfo.InvariantCulture)).Append("\": \"")
                  .Append(Escape(map[id])).Append("\"")
                  .AppendLine(i == keys.Count - 1 ? "" : ",");
            }
        }

        sb.AppendLine("  },");
        sb.AppendLine("  \"counts\": {");

        var sorted = counts.OrderBy(p => p.Key).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            sb.Append("    \"").Append(sorted[i].Key.ToString(CultureInfo.InvariantCulture)).Append("\": ")
              .Append(sorted[i].Value.ToString(CultureInfo.InvariantCulture))
              .AppendLine(i == sorted.Count - 1 ? "" : ",");
        }

        sb.AppendLine("  },");
        sb.AppendLine("  \"colors\": {");

        Color[] colors = manager.liquidColors;
        if (colors != null)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                sb.Append("    \"").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\": \"#")
                  .Append(ToHex(colors[i])).Append("\"")
                  .AppendLine(i == colors.Length - 1 ? "" : ",");
            }
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(dir, "fluid-meta.json"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string FluidName(byte id)
    {
        var map = FluidManager.WorldFluidToLiquidID;
        string name;
        return map != null && map.TryGetValue(id, out name) ? name : "#" + id;
    }

    // ---------------------------------------------------------------- object

    private static void DumpObjects(string dir, Action<string> log)
    {
        GameObject[] all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
        var rows = new List<ObjectRow>();

        foreach (GameObject go in all)
        {
            if (go == null)
                continue;

            Component[] components = go.GetComponents<Component>();
            string category = null;
            Component matched = null;

            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                string typeName = component.GetType().Name;
                string candidate;
                if (!CategoryOfType.TryGetValue(typeName, out candidate))
                    continue;

                // 同一物体有多个命中组件时：只要类别优先级不同就取表里靠前的？这里按"组件顺序"取第一个命中
                category = candidate;
                matched = component;
                break;
            }

            if (matched == null)
                continue;

            Vector2 position = go.transform.position;
            Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(position);

            rows.Add(new ObjectRow
            {
                Category = category,
                Type = matched.GetType().Name,
                Name = CleanName(go.name),
                Id = ReadId(matched),
                DisplayName = ReadDisplayName(matched),
                X = position.x,
                Y = position.y,
                BlockX = blockPos.x,
                BlockY = blockPos.y,
                Layer = ReadWorldInt("biomeDepth"),
                Active = go.activeInHierarchy,
            });
        }

        rows.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Category, b.Category);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Type, b.Type);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0) return c;
            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        });

        var sb = new StringBuilder();
        foreach (ObjectRow row in rows)
            sb.AppendLine(row.ToJson());

        File.WriteAllText(Path.Combine(dir, "objects.jsonl"), sb.ToString(), new UTF8Encoding(false));

        var byCategory = rows.GroupBy(r => r.Category).OrderByDescending(g => g.Count()).ToList();
        log("object: objects.jsonl, " + rows.Count + " objects (" + string.Join(", ", byCategory.Select(g => g.Key + "=" + g.Count())) + ")");

        foreach (IGrouping<string, ObjectRow> group in byCategory)
        {
            IEnumerable<string> types = group.GroupBy(r => r.Type).OrderByDescending(g => g.Count())
                .Select(g => g.Key + "=" + g.Count());
            log("  " + group.Key + ": " + string.Join(", ", types));
        }
    }

    private sealed class ObjectRow
    {
        public string Category;
        public string Type;
        public string Name;
        public string Id;
        public string DisplayName;
        public float X, Y;
        public int BlockX, BlockY;
        public int Layer;
        public bool Active;

        internal string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"category\":\"").Append(Escape(Category))
              .Append("\",\"type\":\"").Append(Escape(Type))
              .Append("\",\"name\":\"").Append(Escape(Name))
              .Append("\",\"id\":").Append(Id == null ? "null" : "\"" + Escape(Id) + "\"")
              .Append(",\"displayName\":").Append(DisplayName == null ? "null" : "\"" + Escape(DisplayName) + "\"")
              .Append(",\"x\":").Append(X.ToString("F3", CultureInfo.InvariantCulture))
              .Append(",\"y\":").Append(Y.ToString("F3", CultureInfo.InvariantCulture))
              .Append(",\"blockX\":").Append(BlockX.ToString(CultureInfo.InvariantCulture))
              .Append(",\"blockY\":").Append(BlockY.ToString(CultureInfo.InvariantCulture))
              .Append(",\"layer\":").Append(Layer.ToString(CultureInfo.InvariantCulture))
              .Append(",\"active\":").Append(Active ? "true" : "false")
              .Append('}');
            return sb.ToString();
        }
    }

    /// <summary>Unity 会给运行时实例加 "(Clone)" 后缀，输出时去掉，方便当英文 id 用。</summary>
    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return name.EndsWith("(Clone)", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "(Clone)".Length).TrimEnd()
            : name;
    }

    private static string ReadId(Component component)
    {
        // Item 与 BuildingEntity 都有 string id 字段
        return ReadString(component, "id");
    }

    private static string ReadDisplayName(Component component)
    {
        // Item.Stats.fullName / BuildingEntity.fullName
        object stats = ReadMember(component, "Stats");
        string fromStats = stats == null ? null : ReadString(stats, "fullName");
        return fromStats ?? ReadString(component, "fullName");
    }

    private static string ReadString(object target, string member)
    {
        object value = ReadMember(target, member);
        return value as string;
    }

    /// <summary>按名字读字段或属性（含私有、含基类），读不到就返回 null —— 用来兼容不同游戏版本。</summary>
    private static object ReadMember(object target, string member)
    {
        if (target == null || string.IsNullOrEmpty(member))
            return null;

        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
                return field.GetValue(target);

            PropertyInfo property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(target, null);
        }

        return null;
    }

    // ---------------------------------------------------------------- 公共小工具

    private static int ReadWorldInt(string fieldName)
    {
        object world = WorldGeneration.world;
        if (world == null)
            return 0;

        object value = AccessTools.Field(world.GetType(), fieldName)?.GetValue(world);
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>本插件自己的版本号（读 [BepInPlugin] 特性，避免和插件类耦合）。</summary>
    private static string PluginVersion()
    {
        object[] attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(BepInEx.BepInPlugin), false);
        return attributes.Length > 0 ? ((BepInEx.BepInPlugin)attributes[0]).Version.ToString() : "unknown";
    }

    /// <summary>通过反射读 QoL 模组的 SeedManager（不硬依赖模组）。</summary>
    private static int ReadSeedValue()
    {
        Type manager = AccessTools.TypeByName("QoL_Unknown.SeedManager");
        if (manager == null)
            return 0;

        object seed = AccessTools.Field(manager, "CurrentSeed")?.GetValue(null);
        return seed == null ? 0 : Convert.ToInt32(seed, CultureInfo.InvariantCulture);
    }

    private static string ReadSeedInput()
    {
        Type manager = AccessTools.TypeByName("QoL_Unknown.SeedManager");
        object input = manager == null ? null : AccessTools.Field(manager, "InputString")?.GetValue(null);
        return input as string ?? "";
    }

    private static string ToHex(Color color)
    {
        return ((int)Mathf.Clamp(Mathf.Round(color.r * 255f), 0f, 255f)).ToString("X2", CultureInfo.InvariantCulture)
             + ((int)Mathf.Clamp(Mathf.Round(color.g * 255f), 0f, 255f)).ToString("X2", CultureInfo.InvariantCulture)
             + ((int)Mathf.Clamp(Mathf.Round(color.b * 255f), 0f, 255f)).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
