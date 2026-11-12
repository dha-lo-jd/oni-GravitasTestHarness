using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Gravitas.TestHarness;

/// <summary>
/// Puts the game into the state a test needs, the way the in-game sandbox tools do (instant build, spawn), so a test does not
/// depend on a hand-made save. All of it runs on the main thread (tests do). A setup that cannot be done throws
/// <see cref="InvalidOperationException"/> (the test is reported as "error", not "failed").
/// Changes to the grid (<see cref="SetElement"/>) reach the game on the next simulation step: <c>yield return Wait.Ticks(1)</c>.
/// A new building/duplicant/item is registered with the game's lists (<see cref="Find"/>, <c>Components.*</c>) one frame later:
/// in a coroutine test, <c>yield return null</c> after creating it.
/// </summary>
public static class Arrange
{
    /// <summary>Cell index of the world coordinates (x, y).</summary>
    public static int Cell(int x, int y)
    {
        var cell = Grid.XYToCell(x, y);
        if (!Grid.IsValidCell(cell)) throw new InvalidOperationException("(" + x + ", " + y + ") is outside the map");
        return cell;
    }

    /// <summary>
    /// A cell in the starting area to build around: the printing pod's, else the first duplicant's, else the middle of the
    /// active world (then <see cref="FindBuildableCell"/> needs a large radius: the middle can be solid rock).
    /// </summary>
    public static int HomeCell()
    {
        var pod = Components.Telepads.Items.FirstOrDefault(t => t != null);
        if (pod != null) return Grid.PosToCell(pod);
        var dupe = Components.LiveMinionIdentities.Items.FirstOrDefault(m => m != null);
        if (dupe != null) return Grid.PosToCell(dupe);
        var world = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorld : null;
        if (world == null) throw new InvalidOperationException("no active world to anchor on");
        return Cell(world.WorldOffset.x + world.Width / 2, world.WorldOffset.y + world.Height / 2);
    }

    /// <summary>Cell the object stands in.</summary>
    public static int CellOf(GameObject go) => Grid.PosToCell(go);

    /// <summary>World coordinates of a cell.</summary>
    public static (int x, int y) XY(int cell)
    {
        Grid.CellToXY(cell, out var x, out var y);
        return (x, y);
    }

    /// <summary>
    /// A cell near <paramref name="near"/> (searching outwards up to <paramref name="radius"/>) where the building can be placed
    /// right now, or -1 if there is none. Use it instead of hard-coding coordinates that depend on the save.
    /// </summary>
    public static int FindBuildableCell(string prefabId, int near, int radius = 40, Orientation orientation = Orientation.Neutral)
    {
        var def = Def(prefabId);
        Grid.CellToXY(near, out var cx, out var cy);
        for (var r = 0; r <= radius; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var cell = Grid.XYToCell(cx + dx, cy + dy);
                    if (Grid.IsValidCell(cell) && CanPlace(def, cell, orientation, out _)) return cell;
                }
        return -1;
    }

    /// <summary>Instantly places a completed building (like the sandbox "instant build"). Materials default to the building's defaults.</summary>
    public static GameObject Build(string prefabId, int cell, Orientation orientation = Orientation.Neutral, IList<string> elements = null)
    {
        var def = Def(prefabId);
        if (!CanPlace(def, cell, orientation, out var reason))
            throw new InvalidOperationException("cannot place " + prefabId + " at cell " + cell + " " + XY(cell) + ": " + reason);
        var tags = elements != null ? elements.Select(e => new Tag(e)).ToList() : def.DefaultElements();
        var temperature = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(tags) - 10f);
        var go = def.Build(cell, orientation, null, tags, temperature, playsound: false, timeBuilt: GameClock.Instance.GetTime());
        if (go == null) throw new InvalidOperationException("the game did not build " + prefabId);
        return go;
    }

    /// <summary>Spawns <paramref name="mass"/> kg of an element as a chunk lying in the cell.</summary>
    public static GameObject SpawnElement(SimHashes element, int cell, float mass = 100f, float temperature = -1f)
    {
        var e = ElementLoader.FindElementByHash(element) ?? throw new InvalidOperationException("unknown element " + element);
        if (e.substance == null) throw new InvalidOperationException(element + " cannot be spawned as an item");
        var pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.Ore);
        var go = e.substance.SpawnResource(pos, mass, temperature >= 0 ? temperature : e.defaultValues.temperature, byte.MaxValue, 0, false, temperature >= 0);
        if (go == null) throw new InvalidOperationException("the game did not spawn " + element);
        return go;
    }

    /// <summary>Spawns an item or creature prefab (for example <c>"Egg_Hatch"</c>, <c>"Hatch"</c>) in the cell.</summary>
    public static GameObject Spawn(string prefabId, int cell)
    {
        var prefab = Assets.GetPrefab(new Tag(prefabId)) ?? throw new InvalidOperationException("no prefab '" + prefabId + "'");
        var anim = prefab.GetComponent<KBatchedAnimController>();
        var layer = anim == null ? Grid.SceneLayer.Creatures : anim.sceneLayer;
        var go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, layer), layer);
        go.SetActive(true);
        return go;
    }

    /// <summary>Spawns a random duplicant in the cell (the sandbox "spawn duplicant").</summary>
    public static GameObject SpawnDuplicant(int cell)
    {
        var prefab = Assets.GetPrefab(new Tag(MinionConfig.ID)) ?? throw new InvalidOperationException("no duplicant prefab");
        var go = Util.KInstantiate(prefab);
        go.name = prefab.name;
        Immigration.Instance.ApplyDefaultPersonalPriorities(go);
        go.transform.SetLocalPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.Move));
        go.SetActive(true);
        new MinionStartingStats(MinionConfig.MODEL, false).Apply(go);
        go.GetMyWorld().SetDupeVisited();
        return go;
    }

    /// <summary>Replaces the element in a cell (sandbox "flood"): takes effect on the next simulation step.</summary>
    public static void SetElement(int cell, SimHashes element, float mass, float temperature)
    {
        if (!Grid.IsValidCell(cell)) throw new InvalidOperationException("invalid cell " + cell);
        SimMessages.ReplaceElement(cell, element, CellEventLogger.Instance.SandBoxTool, mass, temperature);
    }

    /// <summary>Removes a building, item or creature.</summary>
    public static void Destroy(GameObject go)
    {
        if (go != null) Util.KDestroyGameObject(go);
    }

    private static BuildingDef Def(string prefabId) =>
        Assets.GetBuildingDef(prefabId) ?? throw new InvalidOperationException("no building '" + prefabId + "'");

    private static bool CanPlace(BuildingDef def, int cell, Orientation orientation, out string reason)
    {
        var pos = Grid.CellToPosCBC(cell, def.SceneLayer);
        if (!def.IsValidBuildLocation(null, pos, orientation, out reason)) return false;
        return def.IsValidPlaceLocation(null, cell, orientation, false, out reason);
    }
}

/// <summary>Look things up in the running game.</summary>
public static class Find
{
    /// <summary>Every completed building with this prefab id (for example <c>"Generator"</c>).</summary>
    public static List<GameObject> Buildings(string prefabId) =>
        Components.BuildingCompletes.Items
            .Where(b => b != null && b.GetComponent<KPrefabID>() != null && b.GetComponent<KPrefabID>().PrefabTag.Name == prefabId)
            .Select(b => b.gameObject).ToList();

    /// <summary>Every living duplicant.</summary>
    public static List<MinionIdentity> Duplicants() => Components.LiveMinionIdentities.Items.Where(m => m != null).ToList();

    public static SimHashes ElementAt(int cell) => Grid.Element[cell].id;
    public static float TemperatureAt(int cell) => Grid.Temperature[cell];
    public static float MassAt(int cell) => Grid.Mass[cell];
}
