// StructurePlacer.cs
// placestructure 的实现。
//
// 如何识别背景层：
//   Prefab 里的"背景"是挂在 worldGrid 下的**实体**（所在物体带 BuildingEntity，游戏里会 Backgroundify
//   把 sortingOrder 设成 -999），它的 Tilemap 常常复用方块 tile —— 所以不能只看"tile 能不能在
//   world.tiles 里找到"，否则背景会被当方块盖进 worldBlocks。判定顺序：
//     (1) 背景：Tilemap 所在物体（或它在预制体内部的祖先）带 BuildingEntity，或名字含 "background"/叫 "Back"；
//     (2) 方块层：在剩下的里面按游戏自己的规则挑——根节点的 Tilemap，否则第 0 个子节点的 Tilemap
//         （源码里 BioContainer / LifepodStart 就是 transform.GetChild(0)），再退到"有 TilemapCollider2D 的"、
//         "tile 最多的"；
//     (3) 其余一律按背景处理（实例化成对象）。
//
// 实体/背景的放置与游戏 GenerateEntityAtPos 一致：
//   Item / BuildingEntity / DOSPAWN 的子物体实例化，
//   世界坐标 = BlockToWorldPos(方块) - 0.5 + 预制体内偏移；带 Tilemap 的挂到 worldGrid 下。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

internal static class StructurePlacer
{
    private sealed class TilemapEntry
    {
        public Tilemap Map;
        public Vector2 Offset;          // 相对预制体根的位置（方块单位）
        public int TileCount;
        public string BackgroundReason; // 非空 = 背景层，值是判定依据

        public string Path()
        {
            var names = new List<string>();
            for (Transform t = Map.transform; t != null; t = t.parent)
                names.Insert(0, t.name);
            return string.Join("/", names);
        }
    }

    private sealed class EntityEntry
    {
        public GameObject GameObject;
        public Vector2 Offset;
        public string Reason;
        public string Path;
        public bool IsBackgroundLike;
    }

    internal static void Place(
        GameObject prefab, string structureName, Vector2Int blockPos, float blockChance, Action<string> log)
    {
        WorldGeneration world = WorldGeneration.world;

        List<TilemapEntry> tilemaps = CollectTilemaps(prefab);
        TilemapEntry blockLayer = ChooseBlockLayer(tilemaps, prefab, log);
        IEnumerable<TilemapEntry> backgroundLayers = tilemaps.Where(t => t != blockLayer);

        List<EntityEntry> entities = new List<EntityEntry>();
        CollectEntities(prefab.transform, Vector2.zero, "", entities);

        // 1) 方块层：盖进 worldBlocks（genMode = true，与游戏生成期的调用一致）
        if (blockLayer != null)
        {
            world.GenerateObjectAtPos(blockPos, blockLayer.Map, blockChance, true);
            log($"  block layer '{blockLayer.Path()}': stomped {blockLayer.TileCount} tiles (chance {blockChance:F2})");
        }
        else
        {
            log("  warning: no block layer found, nothing stamped into worldBlocks.");
        }

        // 2) 背景层：作为对象实例化（带 BuildingEntity 的那些由下面实体阶段生成，这里不重复）
        int backgroundCount = 0;
        foreach (TilemapEntry layer in backgroundLayers)
        {
            if (EntityReason(layer.Map.gameObject) != null)
                continue;

            Spawn(world, layer.Map.gameObject, blockPos, layer.Offset);
            backgroundCount++;
            log($"  background '{layer.Path()}': spawned as object ({layer.TileCount} tiles)");
        }

        // 3) 实体（Item / BuildingEntity / DOSPAWN），其中带 Tilemap 的算背景件
        int entityCount = 0;
        foreach (EntityEntry entity in entities)
        {
            Spawn(world, entity.GameObject, blockPos, entity.Offset);

            if (entity.IsBackgroundLike)
            {
                backgroundCount++;
                log($"  background '{entity.Path}' ({entity.Reason}): spawned as object");
            }
            else
            {
                entityCount++;
                log($"  entity '{entity.Path}' ({entity.Reason})");
            }
        }

        world.UpdateWorld();
        log($"Placed '{structureName}' at {blockPos}: "
            + $"{(blockLayer != null ? 1 : 0)} block layer, {backgroundCount} background, {entityCount} entity(ies).");
    }

    // ------------------------------------------------------------ 判定

    private static List<TilemapEntry> CollectTilemaps(GameObject prefab)
    {
        var entries = new List<TilemapEntry>();

        foreach (Tilemap map in prefab.GetComponentsInChildren<Tilemap>(true))
        {
            var entry = new TilemapEntry
            {
                Map = map,
                Offset = RelativeOffset(map.transform, prefab.transform)
            };

            BoundsInt bounds = map.cellBounds;
            for (int x = bounds.xMin; x < bounds.xMax; x++)
            {
                for (int y = bounds.yMin; y < bounds.yMax; y++)
                {
                    if (map.GetTile(new Vector3Int(x, y)) != null)
                        entry.TileCount++;
                }
            }

            entry.BackgroundReason = BackgroundReason(map, prefab.transform);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 方块层的挑法（返回 null = 这个预制体没有可盖的方块层）：
    /// 先排除背景层，再按游戏规则挑：根节点 → 第 0 个子节点 → 有 TilemapCollider2D 的 → tile 最多的。
    /// </summary>
    private static TilemapEntry ChooseBlockLayer(List<TilemapEntry> tilemaps, GameObject prefab, Action<string> log)
    {
        List<TilemapEntry> candidates = tilemaps.Where(t => t.BackgroundReason == null).ToList();
        if (candidates.Count == 0)
            return null;

        Tilemap preferred = prefab.GetComponent<Tilemap>();
        if (preferred == null && prefab.transform.childCount > 0)
            preferred = prefab.transform.GetChild(0).GetComponent<Tilemap>();

        TilemapEntry chosen = preferred != null ? candidates.FirstOrDefault(t => t.Map == preferred) : null;
        if (chosen == null)
            chosen = candidates.FirstOrDefault(t => t.Map.GetComponent<TilemapCollider2D>() != null);
        if (chosen == null)
            chosen = candidates.OrderByDescending(t => t.TileCount).First();

        // 只在"不是按游戏规则直接命中"时提示一句，方便以后遇到怪预制体
        if (preferred == null || chosen.Map != preferred)
            log($"  note: block layer picked by fallback: '{chosen.Path()}'");

        return chosen;
    }

    /// <summary>
    /// 背景层的判定依据（返回 null 表示"看着像方块层"）：
    /// 所在物体带 BuildingEntity（游戏里背景件就是这么挂的），或名字含 background / 叫 back。
    /// 只往预制体内部找，不含预制体根节点本身。
    /// </summary>
    private static string BackgroundReason(Tilemap map, Transform root)
    {
        for (Transform t = map.transform; t != null && t != root; t = t.parent)
        {
            if (t.GetComponent<BuildingEntity>() != null)
                return "BuildingEntity";

            if (t.name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0)
                return "name '" + t.name + "'";

            if (string.Equals(t.name, "back", StringComparison.OrdinalIgnoreCase))
                return "name '" + t.name + "'";
        }

        return null;
    }

    private static void CollectEntities(Transform node, Vector2 offset, string path, List<EntityEntry> result)
    {
        for (int i = 0; i < node.childCount; i++)
        {
            Transform child = node.GetChild(i);
            Vector2 childOffset = offset + (Vector2)child.localPosition;
            string childPath = path.Length == 0 ? child.name : path + "/" + child.name;

            string reason = EntityReason(child.gameObject);
            if (reason != null)
            {
                // 命中就不再往里找，避免父子都被当成实体重复生成
                result.Add(new EntityEntry
                {
                    GameObject = child.gameObject,
                    Offset = childOffset,
                    Reason = reason,
                    Path = childPath,
                    IsBackgroundLike = IsBackgroundLike(child.gameObject)
                });
                continue;
            }

            CollectEntities(child, childOffset, childPath, result);
        }
    }

    /// <summary>与游戏的 GenerateEntityAtPos 同一套筛选：Item / BuildingEntity / DOSPAWN。</summary>
    private static string EntityReason(GameObject go)
    {
        if (go.GetComponent<Item>() != null)
            return "Item";
        if (go.GetComponent<BuildingEntity>() != null)
            return "BuildingEntity";
        if (go.name.StartsWith("DOSPAWN", StringComparison.Ordinal))
            return "DOSPAWN";

        return null;
    }

    /// <summary>带 Tilemap 的实体就是背景件（不写方块、只实例化）。</summary>
    private static bool IsBackgroundLike(GameObject go)
    {
        return go.GetComponent<Tilemap>() != null
            || go.name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(go.name, "back", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 放置

    /// <summary>
    /// 与游戏 GenerateEntityAtPos 相同的定位方式：
    /// 世界坐标 = 方块中心 - 0.5（方块左下角）+ 该物体在 Prefab 内的偏移；
    /// 带 Tilemap 的物体挂到 worldGrid 下，便于按区块显隐。
    /// </summary>
    private static GameObject Spawn(WorldGeneration world, GameObject part, Vector2Int blockPos, Vector2 offset)
    {
        Vector2 position = world.BlockToWorldPos(blockPos) - Vector2.one * 0.5f + offset;
        GameObject clone = UnityEngine.Object.Instantiate(part, position, part.transform.localRotation);

        if (clone.GetComponent<Tilemap>())
            clone.transform.SetParent(world.worldGrid.transform, true);

        return clone;
    }

    private static Vector2 RelativeOffset(Transform target, Transform root)
    {
        return (Vector2)(target.position - root.position);
    }
}
