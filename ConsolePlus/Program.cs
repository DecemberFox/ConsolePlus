// ConsoleCommands.cs
// Console Extension —— 给《Casualties: Unknown》的开发者控制台加命令。
//
// 结构：Start → WaitForGameLoaded →（打 Harmony 补丁 / 绑定控制台私有方法 / 注册命令）
// 每个命令一个 RegisterXxx 方法，命令体里尽量早返回、少嵌套。

using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnityEngine;

[BepInPlugin("com.decemberfox.consolePlus", "Console Plus", "1.0")]
public class ConsolePlus : BaseUnityPlugin
{
    /// <summary>setxp 里经验等级的上限（与原实现一致）。</summary>
    private const int MaxSkillLevel = 40;

    private static readonly string[] LimbNames =
    {
        "Head",
        "UpTorso", "DownTorso",
        "UpArmF", "DownArmF", "HandF",
        "UpArmB", "DownArmB", "HandB",
        "ThighF", "CrusF", "FootF",
        "ThighB", "CrusB", "FootB"
    };

    private static readonly string[] StructureNames =
    {
        "LifepodStart",
        "Structures/MedicalBuilding",
        "Structures/BrickLoot",
        "Structures/CratePod",
        "Structures/MiniPod",
        "Structures/SteelThing",
        "Structures/WoodCross",
        "Structures/WoodHorizontal",
        "BioContainer",
        "Structures/SteelBridge",
        "Structures/LongCorridor",
        "Structures/CrystalSpawnPlatform",
        "Special/TutorialStructure",
        "LifepodCollapsed",
        "Lifepod"
    };

    private Harmony harmony;

    // ConsoleScript 的私有方法，反射拿到后缓存成委托
    private Action<string> LogToConsole;
    private Func<string, Vector2> ParsePosition;
    private Func<string, int> ParseInt;
    private Func<string, float> ParseFloat;

    public static class TimeScale
    {
        public static bool force = false;
        public static bool pause = false;
        public static float rate = 1f;
        public static float time = 0f;
    }

    // ------------------------------------------------------------ 生命周期

    private void Start()
    {
        StartCoroutine(WaitForGameLoaded());
    }

    private void Update()
    {
        if (TimeScale.pause && TimeScale.time > 0f)
        {
            if (TimeScale.time < Time.deltaTime)
            {
                TimeScale.time -= Time.deltaTime;
                Time.timeScale = 0f;
            }
            else TimeScale.time -= Time.deltaTime;
        }
    }

    private IEnumerator WaitForGameLoaded()
    {
        while (ConsoleScript.instance == null) yield return new WaitForSeconds(0.2f);
        while (WorldGeneration.world == null) yield return new WaitForSeconds(0.2f);

        Logger.LogInfo("Initializing Console Extension...");

        PatchTimeScale();
        BindConsoleHelpers();
        RegisterCommands();

        Logger.LogInfo("Console Extension initialized successfully.");
    }

    /// <summary>让 Time.timeScale 能被 time 命令接管（原实现用同一个 Harmony id）。</summary>
    private void PatchTimeScale()
    {
        harmony = new Harmony("manageTimeScale");

        MethodInfo setter = AccessTools.Property(typeof(Time), "timeScale")?.GetSetMethod();
        if (setter == null)
        {
            Logger.LogError("Failed to find Time.timeScale setter method for patching! The command 'time' will not work!");
            return;
        }

        harmony.Patch(setter, prefix: new HarmonyMethod(typeof(TimeScaleManager), nameof(TimeScaleManager.Prefix)));
        Logger.LogInfo("Successfully patched Time.timeScale setter.");
    }

    /// <summary>把 ConsoleScript 的几个私有方法绑成委托，省掉每处反射样板。</summary>
    private void BindConsoleHelpers()
    {
        ConsoleScript console = ConsoleScript.instance;

        MethodInfo logMethod = typeof(ConsoleScript).GetMethod("LogToConsole", BindingFlags.Instance | BindingFlags.NonPublic);
        LogToConsole = msg => logMethod.Invoke(console, new object[] { msg });

        MethodInfo parsePositionMethod = typeof(ConsoleScript).GetMethod("ParsePosition", BindingFlags.Instance | BindingFlags.NonPublic);
        ParsePosition = text => (Vector2)parsePositionMethod.Invoke(console, new object[] { text });

        MethodInfo parseIntMethod = typeof(ConsoleScript).GetMethod("ParseInt", BindingFlags.Instance | BindingFlags.NonPublic);
        ParseInt = text => (int)parseIntMethod.Invoke(console, new object[] { text });

        MethodInfo parseFloatMethod = typeof(ConsoleScript).GetMethod("ParseFloat", BindingFlags.Instance | BindingFlags.NonPublic);
        ParseFloat = text => (float)parseFloatMethod.Invoke(console, new object[] { text });
    }

    // ------------------------------------------------------------ 命令注册

    private void RegisterCommands()
    {
        RegisterSetAllField();
        RegisterSetBlock();
        RegisterPlaceStructure();
        RegisterUpdate();
        RegisterGetBlock();
        RegisterTime();
        RegisterSetXp();
        RegisterDestroy();
        RegisterDump();
    }

    private void AddCommand(
        string name,
        string description,
        Command.Action action,
        Dictionary<int, List<string>> autofill = null,
        params (string Name, string Help)[] arguments)
    {
        ConsoleScript.Commands.Add(new Command(name, description, action, autofill, arguments));
        Logger.LogInfo($"Successfully added the '{name}' command.");
    }

    private void RegisterSetAllField()
    {
        AddCommand("setallfield", "Sets a variable in all of the player's limbs.",
            delegate (string[] args)
            {
                RequireWorld();
                RequireArgs(args, 2);

                string fieldName = args[1];
                FieldInfo field = typeof(Limb).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    throw new Exception($"Limb has no field named \"{fieldName}\".");

                object value = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(args[2]);
                Body body = PlayerCamera.main.body;

                foreach (string limbName in LimbNames)
                    field.SetValue(body.LimbByName(limbName), value);

                Log($"Set all field \"{fieldName}\" to \"{value}\".");
            },
            new Dictionary<int, List<string>> { { 0, LimbPrimitiveFieldNames() } },
            ("string field", "The field you want to set"),
            ("object value", "Value to set the field to"));
    }

    private static List<string> LimbPrimitiveFieldNames()
    {
        return typeof(Limb).GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(f => f.FieldType.IsPrimitive)
            .Select(f => f.Name)
            .ToList();
    }

    private void RegisterSetBlock()
    {
        AddCommand("setblock", "Replace a block in the world.",
            delegate (string[] args)
            {
                RequireWorld();
                WorldGeneration world = WorldGeneration.world;

                byte blockId = args.Length > 1 ? (byte)ParseInt(args[1]) : (byte)0;
                Vector2 position = args.Length > 2 ? ParsePosition(args[2]) : MouseWorldPosition();
                bool update = args.Length <= 3 || !bool.TryParse(args[3], out bool parsed) || parsed;

                if (update) world.SetBlock(world.WorldToBlockPos(position), blockId);
                else world.SetBlockNoUpdate(world.WorldToBlockPos(position), blockId);

                Log($"Set block {blockId} at {position} with{(update ? "" : "out")} update.");
            },
            arguments: new[]
            {
                ("byte blockId", "The number id of the block you want to set"),
                ("position", "Where to replace"),
                ("bool update", "Whether to update")
            });
    }

    private void RegisterPlaceStructure()
    {
        AddCommand("placestructure", "Place a structure in the world.",
            delegate (string[] args)
            {
                RequireWorld();
                RequireArgs(args, 1);

                string structureName = args[1];
                Vector2 position = args.Length > 2 ? ParsePosition(args[2]) : MouseWorldPosition();
                float blockChance = args.Length > 3 ? Mathf.Clamp01(ParseFloat(args[3])) : 1f;

                GameObject prefab = Resources.Load<GameObject>(structureName);
                if (prefab == null)
                    throw new Exception($"Structure \"{structureName}\" not found.");

                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(position);
                StructurePlacer.Place(prefab, structureName, blockPos, blockChance, Log);
            },
            new Dictionary<int, List<string>> { { 0, StructureNames.ToList() } },
            ("string structureName", "The name of the structure to place"),
            ("position", "Where to place the structure"),
            ("float blockChance", "Chance to place blocks (0-1)"));
    }

    private void RegisterUpdate()
    {
        AddCommand("update", "Update the selected area.",
            delegate (string[] args)
            {
                RequireWorld();
                WorldGeneration world = WorldGeneration.world;

                if (args.Length > 1)
                {
                    Vector2Int blockPos = world.WorldToBlockPos(ParsePosition(args[1]));
                    world.UpdateChunkClosest(blockPos);
                    Log($"Updated chunk at {blockPos}.");
                }
                else
                {
                    world.UpdateWorld();
                    Log("Updated the entire world.");
                }
            },
            arguments: new[]
            {
                ("position", "Where to update (optional). If not specified, updates the entire world")
            });
    }

    private void RegisterGetBlock()
    {
        AddCommand("getblock", "Get the block ID at a position.",
            delegate (string[] args)
            {
                RequireWorld();
                WorldGeneration world = WorldGeneration.world;

                Vector2 position = args.Length > 1 ? ParsePosition(args[1]) : MouseWorldPosition();
                Vector2Int blockPos = world.WorldToBlockPos(position);

                Log($"Block ID at {blockPos} is {world.GetBlock(blockPos)}.");
            },
            arguments: new[] { ("position", "Where to get") });
    }

    private void RegisterTime()
    {
        AddCommand("time", "Manage the in-game time.",
            delegate (string[] args)
            {
                RequireWorld();

                if (args.Length < 2)
                {
                    Log("Lack of subcommand. Please check the syntax.");
                    return;
                }

                switch (args[1])
                {
                    case "force":
                        TimeScale.force = !TimeScale.force;
                        Log($"Time controller status: {TimeScale.time}.");
                        break;

                    case "query":
                        Log($"Force setter: {TimeScale.force}" +
                            $"Rate(Current/Target): {Time.timeScale}/{TimeScale.rate}; " +
                            $"Step: {TimeScale.time}; " +
                            $"Status: {(TimeScale.pause ? "paused" : "running")}.");
                        break;

                    case "freeze":
                        TimeScale.pause = true;
                        Time.timeScale = 0f;
                        Log("Time frozen.");
                        break;

                    case "unfreeze":
                        TimeScale.pause = false;
                        Time.timeScale = TimeScale.rate;
                        Log("Time unfrozen.");
                        break;

                    case "rate":
                        {
                            float rate = ParseFloat(RequireArg(args, 2, "rate"));
                            TimeScale.rate = rate;
                            Time.timeScale = rate;
                            Log($"Time rate set to {rate}.");
                            break;
                        }

                    case "step":
                        {
                            float time = ParseFloat(RequireArg(args, 2, "step"));
                            TimeScale.time = time;
                            Time.timeScale = TimeScale.rate;
                            Log($"Time stepped {time}.");
                            break;
                        }

                    default:
                        throw new Exception("Invalid subcommand.");
                }
            },
            new Dictionary<int, List<string>>
            {
                { 0, new List<string> { "force", "query", "freeze", "unfreeze", "rate", "step" } }
            },
            ("string subcommand", "'force', 'query', 'freeze', 'unfreeze', 'rate' or 'step'"),
            ("float rate/timer", "The time rate/timer to set (only for 'rate' or 'step')"));
    }

    private void RegisterSetXp()
    {
        AddCommand("setxp", "Set the character's experience in a selected skill.",
            delegate (string[] args)
            {
                RequireWorld();
                RequireArgs(args, 2);

                string skill = args[1];
                string type = args.Length > 3 ? args[3] : "point";
                if (type != "point" && type != "level")
                    throw new Exception($"Invalid type '{type}', must be 'point' or 'level'.");

                float amount = Mathf.Max(ParseFloat(args[2]), 0f);
                int level = 0;

                if (type == "level")
                {
                    level = (int)Mathf.Min(amount, MaxSkillLevel);
                    amount = GetExperienceForLevel(level);
                }

                Skills skills = PlayerCamera.main.body.skills;
                string label;
                switch (skill)
                {
                    case "str":
                        skills.expSTR = amount;
                        skills.STR = level;
                        label = "strength";
                        break;
                    case "res":
                        skills.expRES = amount;
                        skills.RES = level;
                        label = "resistance";
                        break;
                    case "int":
                        skills.expINT = amount;
                        skills.INT = level;
                        label = "intelligence";
                        break;
                    default:
                        throw new Exception($"\"{skill}\" is not a valid skill type!");
                }

                skills.UpdateExpBoundaries();
                Log($"Set {label} experience at {(type == "point" ? Mathf.RoundToInt(amount).ToString() : level.ToString())} {type}(s).");
            },
            new Dictionary<int, List<string>>
            {
                { 0, new List<string> { "str", "res", "int" } },
                { 3, new List<string> { "point", "level" } }
            },
            ("type", "The skill to affect"),
            ("float experience", "How much experience to give"),
            ("string type", "Points or levels (default: points)"));
    }

    private void RegisterDestroy()
    {
        AddCommand("destroy", "Destroy an object. Warning: This action would lead to crash!",
            delegate (string[] args)
            {
                RequireArgs(args, 1);
                RequireWorld();

                string objectName = args[1];
                List<GameObject> found = ConsoleScript.FindObjectsWithName(objectName);
                if (found.Count == 0)
                {
                    Log($"No object found with name \"{objectName}\".");
                    return;
                }

                int index = Mathf.Clamp(args.Length > 2 ? ParseInt(args[2]) : 0, 0, found.Count - 1);
                GameObject target = found[index];
                if (target == null)
                    return;

                UnityEngine.Object.Destroy(target);
                Log($"Destroyed object \"{target.name}\" at index {index}.");
            },
            arguments: new[]
            {
                ("object", "The object's name to destroy"),
                ("int index", "The index of the object to destroy (default: 0)")
            });
    }

    private void RegisterDump()
    {
        AddCommand("dump", "Dumps the current world (blocks / fluids / objects) into BepInEx/plugins/ConsoleExtension/Dump.",
            delegate (string[] args)
            {
                DumpCommand.Run(args, Log);
            },
            new Dictionary<int, List<string>>
            {
                { 0, new List<string> { "block", "fluid", "object", "all" } }
            },
            ("string subcommand", "'block', 'fluid', 'object' or 'all' (default: all)"),
            ("string name", "Optional sub folder name; defaults to <seed>_L<layer>"));
    }

    // ------------------------------------------------------------ 工具

    private void Log(string message)
    {
        // 控制台里的内容没法复制，所以同一条也写进 BepInEx 的 LogOutput.log
        LogToConsole(message);
        Logger.LogInfo(message);
    }

    private static Vector2 MouseWorldPosition()
    {
        return Camera.main.ScreenToWorldPoint(Input.mousePosition);
    }

    private void RequireWorld()
    {
        if (!PlayerCamera.main)
            throw new Exception("No world is loaded. Try starting a game?");
    }

    private void RequireArgs(string[] args, int minimum)
    {
        if (args.Length < minimum)
            throw new Exception($"Expected at least {minimum} argument{(minimum > 1 ? "s" : "")}, but got {args.Length - 1}.");
    }

    private string RequireArg(string[] args, int index, string name)
    {
        if (args.Length <= index)
            throw new Exception($"Missing argument '{name}'.");

        return args[index];
    }

    public static int GetExperienceForLevel(int targetLevel)
    {
        if (targetLevel <= 0)
            return 0;

        float total = 0f;
        float step = 60f;

        for (int level = 1; level <= targetLevel; level++)
        {
            if (level < 10)
            {
                total += 60f;
            }
            else if (level == 10)
            {
                step = 100f;
                total += step;
            }
            else
            {
                step *= 1.18f;
                if (level > 20)
                {
                    step *= 1.15f;
                    if (level >= 30)
                        step *= 2f;
                }
                total += step;
            }
        }

        return Mathf.RoundToInt(total);
    }
}

public static class TimeScaleManager
{
    public static bool Prefix(ref float __0)
    {
        if (!ConsolePlus.TimeScale.force)
            return true;

        if (ConsolePlus.TimeScale.pause)
        {
            __0 = ConsolePlus.TimeScale.time > 0f ? ConsolePlus.TimeScale.rate : 0f;
            return true;
        }

        __0 = ConsolePlus.TimeScale.rate;
        return true;
    }
}
