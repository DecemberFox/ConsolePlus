// DumpReader —— 解析 ConsoleExtension `dump` 命令写出的二进制/文本文件。
//
// 用法：
//   DumpReader blocks  <dump目录> [--png out.png] [--csv out.csv] [--palette block-colors.json]
//   DumpReader fluid   <dump目录> [--png out.png] [--csv out.csv]
//   DumpReader objects <dump目录> [--csv out.csv]
//   DumpReader all     <dump目录> [--png out.png] [--csv out.csv]
//
// 读的文件：blocks.bin + meta.json + tiles.json / fluid.bin + fluid-meta.json / objects.jsonl
// 不写任何文件除非显式给了 --png 或 --csv。

using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;

internal static class DumpReader
{
    /// <summary>默认配色：取自游戏贴图（textures/blocks）的平均色；可用 --palette 覆盖。</summary>
    private static readonly string[] DefaultPalette =
    {
        "121218", "242424", "2D3131", "6B6B6B", "7C7C7C", "39393A", "C4C4C4", "C0E9EE",
        "161717", "505050", "A19E96", "B08C68", "DDC8B3", "C7955F", "5B333A", "565C61",
        "42413D", "A8AAA9", "B1B7C2", "ABBABC", "BB7D3D", "5E5642", "855579", "7B8F71",
        "624930", "54633E", "D5D5D5", "2B8FA2", "89C6DA", "D3D3D3", "2A2921", "4F213E",
        "D3D3D3", "C4A9A4", "2A2F27", "0F3F5E"
    };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：DumpReader <blocks|fluid|objects|all> <dump目录> [--png out.png] [--csv out.csv] [--palette block-colors.json]");
            return 2;
        }

        string sub = args[0].ToLowerInvariant();
        string dir = args[1];
        string? png = ArgValue(args, "--png");
        string? csv = ArgValue(args, "--csv");
        string? palettePath = ArgValue(args, "--palette");

        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"目录不存在：{dir}");
            return 2;
        }

        int exit = 0;
        if (sub is "blocks" or "all")
            exit |= ReadBlocks(dir, png, csv, palettePath);
        if (sub is "fluid" or "all")
            exit |= ReadFluid(dir, png, csv);
        if (sub is "objects" or "all")
            exit |= ReadObjects(dir, csv);

        if (sub is not ("blocks" or "fluid" or "objects" or "all"))
        {
            Console.WriteLine($"未知子命令：{sub}");
            return 2;
        }

        return exit;
    }

    // ------------------------------------------------------------- blocks

    private static int ReadBlocks(string dir, string? png, string? csv, string? palettePath)
    {
        string binPath = Path.Combine(dir, "blocks.bin");
        string metaPath = Path.Combine(dir, "meta.json");
        if (!File.Exists(binPath) || !File.Exists(metaPath))
        {
            Console.WriteLine("[blocks] 缺 blocks.bin 或 meta.json，跳过。");
            return 1;
        }

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        int width = GetInt(meta.RootElement, "width");
        int height = GetInt(meta.RootElement, "height");
        string seedInput = GetString(meta.RootElement, "seedInput") ?? "";
        int layer = GetInt(meta.RootElement, "biomeDepth");

        byte[] raw = File.ReadAllBytes(binPath);
        if (raw.Length != width * height * 2)
        {
            Console.WriteLine($"[blocks] blocks.bin 大小不符：{raw.Length} 字节，期望 {width * height * 2}（meta 说 {width}x{height}）");
            return 1;
        }

        string[] names = ReadTileNames(dir);
        var counts = new Dictionary<int, long>();
        var blocks = new ushort[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);

        foreach (ushort id in blocks)
        {
            counts.TryGetValue(id, out long c);
            counts[id] = c + 1;
        }

        Console.WriteLine($"[blocks] {Path.GetFileName(dir)}  种子 {seedInput}  层 {layer}  {width}x{height}  共 {blocks.Length} 格");
        Console.WriteLine("  id  名称                  数量        占比");
        foreach (KeyValuePair<int, long> pair in counts.OrderByDescending(p => p.Value))
        {
            string name = pair.Key >= 0 && pair.Key < names.Length ? names[pair.Key] : "";
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,3} {1,-20} {2,10}  {3,7:F3}%", pair.Key, name,
                pair.Value, 100.0 * pair.Value / blocks.Length));
        }

        if (csv != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,name,count,percent");
            foreach (KeyValuePair<int, long> pair in counts.OrderByDescending(p => p.Value))
            {
                string name = pair.Key >= 0 && pair.Key < names.Length ? names[pair.Key] : "";
                sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(name.Replace(',', ' ')).Append(',')
                  .Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append((100.0 * pair.Value / blocks.Length).ToString("F4", CultureInfo.InvariantCulture)).AppendLine();
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"  -> {csv}");
        }

        if (png != null)
        {
            SKColor[] palette = LoadPalette(palettePath);
            var pixels = new SKColor[width * height];
            for (int x = 0; x < width; x++)
            {
                int row = (height - 1) * width + x;      // y=0 在世界底部，图片要上下翻转
                for (int y = 0; y < height; y++)
                {
                    ushort id = blocks[x * height + y];
                    pixels[row - y * width] = id < palette.Length ? palette[id] : new SKColor(255, 0, 255);
                }
            }

            SavePng(png, width, height, pixels);
            Console.WriteLine($"  -> {png}");
        }

        return 0;
    }

    private static string[] ReadTileNames(string dir)
    {
        string path = Path.Combine(dir, "tiles.json");
        if (!File.Exists(path))
            return Array.Empty<string>();

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<string>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            int index = GetInt(item, "index");
            while (list.Count <= index)
                list.Add("");
            list[index] = GetString(item, "name") ?? "";
        }

        return list.ToArray();
    }

    // -------------------------------------------------------------- fluid

    private static int ReadFluid(string dir, string? png, string? csv)
    {
        string binPath = Path.Combine(dir, "fluid.bin");
        string metaPath = Path.Combine(dir, "fluid-meta.json");
        if (!File.Exists(binPath) || !File.Exists(metaPath))
        {
            Console.WriteLine("[fluid] 缺 fluid.bin 或 fluid-meta.json，跳过。");
            return 1;
        }

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        int width = GetInt(meta.RootElement, "width");
        int height = GetInt(meta.RootElement, "height");

        var names = new Dictionary<int, string>();
        if (meta.RootElement.TryGetProperty("idToName", out JsonElement nameMap))
            foreach (JsonProperty property in nameMap.EnumerateObject())
                names[int.Parse(property.Name, CultureInfo.InvariantCulture)] = property.Value.GetString() ?? "";

        var colors = new Dictionary<int, SKColor>();
        if (meta.RootElement.TryGetProperty("colors", out JsonElement colorMap))
            foreach (JsonProperty property in colorMap.EnumerateObject())
                if (TryParseHex(property.Value.GetString(), out SKColor color))
                    colors[int.Parse(property.Name, CultureInfo.InvariantCulture)] = color;

        byte[] fluid = File.ReadAllBytes(binPath);
        if (fluid.Length != width * height)
        {
            Console.WriteLine($"[fluid] fluid.bin 大小不符：{fluid.Length}，期望 {width * height}");
            return 1;
        }

        var counts = new Dictionary<int, long>();
        foreach (byte id in fluid)
        {
            counts.TryGetValue(id, out long c);
            counts[id] = c + 1;
        }

        Console.WriteLine($"[fluid] {Path.GetFileName(dir)}  {width}x{height}");
        foreach (KeyValuePair<int, long> pair in counts.OrderByDescending(p => p.Value))
        {
            string name = names.TryGetValue(pair.Key, out string? n) ? n : "";
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,3} {1,-14} {2,10}", pair.Key, name, pair.Value));
        }

        if (csv != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("x,y,id,name");
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte id = fluid[x * height + y];
                    if (id == 0)
                        continue;

                    sb.Append(x.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(y.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(id.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append((names.TryGetValue(id, out string? n) ? n : "").Replace(',', ' ')).AppendLine();
                }
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"  -> {csv}");
        }

        if (png != null)
        {
            // 空格子填纯黑（PNG 只作预览；"哪些格子有液体"以 CSV / fluid.bin 为准）
            var empty = new SKColor(0, 0, 0, 255);
            var pixels = new SKColor[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = empty;

            for (int x = 0; x < width; x++)
            {
                int row = (height - 1) * width + x;
                for (int y = 0; y < height; y++)
                {
                    byte id = fluid[x * height + y];
                    if (id == 0)
                        continue;

                    pixels[row - y * width] = colors.TryGetValue(id, out SKColor color)
                        ? color.WithAlpha(255)
                        : new SKColor((byte)(40 * id % 255), (byte)(90 * id % 255), (byte)(160 * id % 255));
                }
            }

            SavePng(png, width, height, pixels);
            Console.WriteLine($"  -> {png}");
        }

        return 0;
    }

    // ------------------------------------------------------------ objects

    private static int ReadObjects(string dir, string? csv)
    {
        string path = Path.Combine(dir, "objects.jsonl");
        if (!File.Exists(path))
        {
            Console.WriteLine("[objects] 缺 objects.jsonl，跳过。");
            return 1;
        }

        var rows = new List<(string Category, string Type, string Name, string? Id, string? Display, double X, double Y, int Bx, int By, int Layer, bool Active)>();

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            rows.Add((
                GetString(e, "category") ?? "",
                GetString(e, "type") ?? "",
                GetString(e, "name") ?? "",
                GetString(e, "id"),
                GetString(e, "displayName"),
                GetDouble(e, "x"), GetDouble(e, "y"),
                GetInt(e, "blockX"), GetInt(e, "blockY"),
                GetInt(e, "layer"),
                e.TryGetProperty("active", out JsonElement active) && active.GetBoolean()));
        }

        Console.WriteLine($"[objects] {Path.GetFileName(dir)}  共 {rows.Count} 个");
        foreach (IGrouping<string, (string Category, string Type, string Name, string? Id, string? Display, double X, double Y, int Bx, int By, int Layer, bool Active)> group in rows.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()})：");
            foreach (IGrouping<string, (string Category, string Type, string Name, string? Id, string? Display, double X, double Y, int Bx, int By, int Layer, bool Active)> byType in group.GroupBy(r => r.Type).OrderByDescending(g => g.Count()))
            {
                string top = string.Join(", ", byType.GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Take(5)
                    .Select(g => g.Key + "×" + g.Count()));
                Console.WriteLine($"    {byType.Key,-20} {byType.Count(),5}   {top}");
            }
        }

        if (csv != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("category,type,name,id,displayName,x,y,blockX,blockY,layer,active");
            foreach (var row in rows)
            {
                sb.Append(row.Category).Append(',').Append(row.Type).Append(',').Append(Csv(row.Name)).Append(',')
                  .Append(Csv(row.Id)).Append(',').Append(Csv(row.Display)).Append(',')
                  .Append(row.X.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(row.Y.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(row.Bx.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(row.By.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(row.Layer.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(row.Active ? "true" : "false").AppendLine();
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"  -> {csv}");
        }

        return 0;
    }

    // ------------------------------------------------------------ 公用

    private static SKColor[] LoadPalette(string? path)
    {
        var palette = new SKColor[Math.Max(64, DefaultPalette.Length)];
        for (int i = 0; i < DefaultPalette.Length; i++)
            palette[i] = ParseHex(DefaultPalette[i]);

        string? file = path;
        if (file == null)
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "block-colors.json");
            if (File.Exists(candidate))
                file = candidate;
        }

        if (file == null || !File.Exists(file))
            return palette;

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("blocks", out JsonElement blocks))
            return palette;

        foreach (JsonElement block in blocks.EnumerateArray())
        {
            int id = GetInt(block, "id");
            string? hex = GetString(block, "hex");
            if (id >= 0 && id < palette.Length && TryParseHex(hex, out SKColor color))
                palette[id] = color;
        }

        return palette;
    }

    private static void SavePng(string path, int width, int height, SKColor[] pixels)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Pixels = pixels;

        using var image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];

        return null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static double GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value.Replace(',', ' ');
    }

    private static bool TryParseHex(string? hex, out SKColor color)
    {
        color = new SKColor(255, 0, 255);
        if (string.IsNullOrEmpty(hex))
            return false;

        string value = hex.TrimStart('#');
        if (value.Length != 6)
            return false;

        color = ParseHex(value);
        return true;
    }

    private static SKColor ParseHex(string hex)
    {
        return new SKColor(
            byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
