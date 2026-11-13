using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Gravitas.TestHarness;

/// <summary>
/// Plays the player's mouse input from a test: drag a tool over a range of cells, click a cell, click a UI element or a
/// toolbar button. It calls the same entry points the game calls for a real mouse (<c>InterfaceTool.OnLeftClickDown/Up</c>,
/// <c>OnMouseMove</c>, the UI <c>IPointerClickHandler</c>), so a mod's patches on those run as they would for the player.
/// For the map it moves the camera over the cells and locks the game's mouse position
/// (<c>KInputManager.GetMousePos()</c>) on the cell, so "the object under the cursor" is what a player would get. What it does NOT do:
/// it does not move the OS cursor, so anything that reads <c>Input.mousePosition</c> or <c>Input.GetKey</c>
/// directly (key-modified drags such as straight-line drag) sees the real, idle input. Keys, the wheel and hover are in PlayKeys.cs.
/// <para>
/// The game ignores clicks while its window has no focus (<c>KInputManager.isFocused</c>); an automated run often has none, so
/// every call here lets the game believe it has focus while the call runs. Failures to find a tool, button or cell throw
/// <see cref="InvalidOperationException"/> (the test is reported as "error", not "failed").
/// </para>
/// </summary>
public static partial class Play
{
    private static readonly FieldInfo HasFocusField = typeof(KInputManager).GetField("hasFocus", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>Runs <paramref name="action"/> with the game's focus flag raised, then puts the flag back.</summary>
    private static void WithFocus(System.Action action)
    {
        var old = HasFocusField != null && HasFocusField.GetValue(null) is bool b ? b : true;
        HasFocusField?.SetValue(null, true);
        try { action(); }
        finally { HasFocusField?.SetValue(null, old); }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the game's mouse position locked on <paramref name="world"/> (as seen by the camera),
    /// so what the game reads from <c>KInputManager.GetMousePos()</c> (the object under the cursor, the cursor cell) is that point.
    /// </summary>
    private static void MouseAt(Vector3 world, System.Action action)
    {
        var oldLocked = KInputManager.isMousePosLocked;
        var oldPos = KInputManager.lockedMousePos;
        KInputManager.lockedMousePos = Camera.main.WorldToScreenPoint(world);
        KInputManager.isMousePosLocked = true;
        try { action(); }
        finally
        {
            KInputManager.isMousePosLocked = oldLocked;
            KInputManager.lockedMousePos = oldPos;
        }
    }

    /// <summary>
    /// Moves the camera so that all <paramref name="cells"/> are on screen (the game only picks what is under a visible cursor).
    /// Throws when they are further apart than the camera can show.
    /// </summary>
    public static void FrameCells(params int[] cells)
    {
        if (CameraController.Instance == null || Camera.main == null) throw new InvalidOperationException("no camera (no game loaded?)");
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var cell in cells)
        {
            var p = WorldPos(cell);
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        var aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        var size = Mathf.Max(10f, (maxY - minY) / 2f + 4f, ((maxX - minX) / 2f + 4f) / aspect);
        if (size > 20f) throw new InvalidOperationException("the cells are too far apart to be on screen together");
        CameraController.Instance.SnapTo(new Vector3((minX + maxX) / 2f, (minY + maxY) / 2f, 0f), size);
    }

    /// <summary>World position of the middle of <paramref name="cell"/>, the point a cursor over that cell would report.</summary>
    public static Vector3 WorldPos(int cell)
    {
        if (!Grid.IsValidCell(cell)) throw new InvalidOperationException("cell " + cell + " is not on the map");
        return Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
    }

    // ---- tools ----

    /// <summary>The tool the player has active (null before a game is loaded).</summary>
    public static InterfaceTool ActiveTool => PlayerController.Instance != null ? PlayerController.Instance.ActiveTool : null;

    /// <summary>
    /// A tool of the game's tool list by its C# type name (<c>"HarvestTool"</c>, a mod's <c>"CensusTool"</c>) or full name.
    /// </summary>
    public static InterfaceTool FindTool(string typeName)
    {
        var tools = PlayerController.Instance != null ? PlayerController.Instance.tools : null;
        var tool = tools?.FirstOrDefault(t => t != null && (t.GetType().Name == typeName || t.GetType().FullName == typeName));
        if (tool == null)
            throw new InvalidOperationException("no tool '" + typeName + "' in PlayerController.tools (" +
                string.Join(", ", (tools ?? new InterfaceTool[0]).Where(t => t != null).Select(t => t.GetType().Name)) + ")");
        return tool;
    }

    /// <summary>Makes <paramref name="tool"/> the active tool, the way picking it from the toolbar does (without the button).</summary>
    public static void Activate(InterfaceTool tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        WithFocus(() => PlayerController.Instance.ActivateTool(tool));
    }

    /// <summary>
    /// Leaves tool mode the way pressing Esc does: the toolbar forgets its selection and the select tool is active again.
    /// Use it to clean up after a test that picked a tool (<see cref="Activate"/> alone leaves the toolbar still "selected").
    /// </summary>
    public static void ClearTool()
    {
        WithFocus(() =>
        {
            ToolMenu.Instance?.ClearSelection();
            if (SelectTool.Instance != null) PlayerController.Instance.ActivateTool(SelectTool.Instance);
        });
    }

    /// <summary>
    /// Makes a tool active by clicking its toolbar button (the entry whose <c>ToolInfo.toolName</c> is <paramref name="toolName"/>),
    /// the way a player does: most tools sit in a collection that is folded away until its own button is clicked (which also selects
    /// the collection's first tool), so that button is clicked first when the tool's own button is not on screen. A button click
    /// toggles in the game (clicking the active tool's button deselects it), so nothing is clicked when the tool is already active.
    /// </summary>
    public static void ClickToolButton(string toolName)
    {
        var menu = ToolMenu.Instance;
        if (menu == null) throw new InvalidOperationException("the tool menu is not there (no game loaded?)");
        var info = menu.basicTools.Concat(menu.sandboxTools).SelectMany(c => c.tools).FirstOrDefault(t => t.toolName == toolName);
        if (info == null)
            throw new InvalidOperationException("no toolbar entry with toolName '" + toolName + "' (" + string.Join(", ",
                menu.basicTools.SelectMany(c => c.tools).Select(t => t.toolName)) + ")");
        if (IsActive(toolName)) return;
        // The menu keeps its own idea of the selected tool; after Activate() or a script switched tools behind its back it would
        // read the click as "deselect". Bring it back in line first.
        if (menu.currentlySelectedTool != null && !IsActive(menu.currentlySelectedTool.toolName)) menu.ClearSelection();
        if (info.toggle != null && info.toggle.gameObject.activeInHierarchy)
        {
            ClickUi(info.toggle.gameObject);
            return;
        }
        var collection = info.collection?.toggle;
        if (collection == null) throw new InvalidOperationException("the toolbar entry '" + toolName + "' has no button on screen and no collection button");
        ClickUi(collection);
        if (!IsActive(toolName) && info.toggle != null && info.toggle.gameObject.activeInHierarchy) ClickUi(info.toggle.gameObject);
    }

    private static bool IsActive(string toolName) => ActiveTool != null && ActiveTool.name == toolName;

    // ---- mouse on the map ----

    /// <summary>
    /// Drags the active tool from the middle of <paramref name="fromCell"/> to the middle of <paramref name="toCell"/>
    /// and releases there: move, press, move, release.
    /// </summary>
    public static void Drag(int fromCell, int toCell)
    {
        var tool = ActiveTool ?? throw new InvalidOperationException("no active tool");
        var from = WorldPos(fromCell);
        var to = WorldPos(toCell);
        FrameCells(fromCell, toCell);
        WithFocus(() =>
        {
            MouseAt(from, () => { tool.OnMouseMove(from); tool.OnLeftClickDown(from); });
            MouseAt(to, () => { tool.OnMouseMove(to); tool.OnLeftClickUp(to); });
        });
    }

    /// <summary>Activates <paramref name="tool"/>, then <see cref="Drag(int, int)"/>.</summary>
    public static void Drag(InterfaceTool tool, int fromCell, int toCell)
    {
        Activate(tool);
        Drag(fromCell, toCell);
    }

    /// <summary>Clicks (press and release) the middle of <paramref name="cell"/> with the active tool.</summary>
    public static void Click(int cell)
    {
        var tool = ActiveTool ?? throw new InvalidOperationException("no active tool");
        var pos = WorldPos(cell);
        FrameCells(cell);
        WithFocus(() => MouseAt(pos, () =>
        {
            tool.OnMouseMove(pos);
            tool.OnLeftClickDown(pos);
            tool.OnLeftClickUp(pos);
        }));
    }

    // ---- UI ----

    /// <summary>
    /// Every active UI object named <paramref name="name"/> (exact match), under <paramref name="root"/> when given, else anywhere.
    /// The game's own UI reuses common names ("Row", "Button"): scope a search to your own screen.
    /// </summary>
    public static List<GameObject> FindUi(string name, Component root = null)
    {
        var all = root != null ? root.GetComponentsInChildren<RectTransform>(false) : UnityEngine.Object.FindObjectsOfType<RectTransform>();
        return all.Where(r => r != null && r.gameObject.activeInHierarchy && r.name == name).Select(r => r.gameObject).ToList();
    }

    /// <summary>The hierarchy path of a UI object ("Canvas/Panel/Button").</summary>
    public static string PathOf(GameObject go)
    {
        var names = new List<string>();
        for (var tr = go.transform; tr != null; tr = tr.parent) names.Add(tr.name);
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Clicks a UI object the way a left click would: a pointer click on it, handed up to the first parent that handles it
    /// (so clicking a button's label clicks the button).
    /// </summary>
    public static void ClickUi(GameObject go)
    {
        if (go == null) throw new ArgumentNullException(nameof(go));
        var system = UnityEngine.EventSystems.EventSystem.current ?? throw new InvalidOperationException("no EventSystem");
        var rect = go.GetComponent<RectTransform>();
        var screen = rect != null ? (Vector2)RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)) : Vector2.zero;
        var data = new PointerEventData(system)
        {
            button = PointerEventData.InputButton.Left,
            clickCount = 1,
            position = screen,
            pressPosition = screen,
        };
        var handled = false;
        WithFocus(() => handled = ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerClickHandler) != null);
        if (!handled)
        {
            var parts = new List<string>();
            for (var tr = go.transform; tr != null && parts.Count < 4; tr = tr.parent)
                parts.Add(tr.name + "[active=" + tr.gameObject.activeInHierarchy + ": " + string.Join(",", tr.GetComponents<Behaviour>()
                    .Select(c => c == null ? "null" : c.GetType().Name + (c.isActiveAndEnabled ? "" : "(off)"))) + "]");
            throw new InvalidOperationException("'" + go.name + "' and its parents do not handle a pointer click: " + string.Join(" < ", parts));
        }
    }

    /// <summary>
    /// Clicks the one active UI object named <paramref name="name"/>. Zero or several matches throw: be specific
    /// (<see cref="FindUi"/> then <see cref="ClickUi(GameObject)"/>) when a name is not unique.
    /// </summary>
    public static void ClickUi(string name, Component root = null)
    {
        var found = FindUi(name, root);
        if (found.Count != 1) throw new InvalidOperationException(found.Count + " active UI objects are named '" + name + "', expected exactly 1");
        ClickUi(found[0]);
    }
}
