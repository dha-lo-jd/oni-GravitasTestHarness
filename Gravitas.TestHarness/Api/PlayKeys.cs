using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Gravitas.TestHarness;

// Keyboard, mouse wheel and hover input of Play (see Play.cs for the mouse clicks and drags).
public static partial class Play
{
    private static readonly MethodInfo GenerateFlagTable = typeof(KInputController).GetMethod("GenerateActionFlagTable", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo KeyDefsField = typeof(KInputController).GetField("mKeyDefs", BindingFlags.Instance | BindingFlags.NonPublic);

    private static KInputController Controller
    {
        get
        {
            var manager = Global.GetInputManager();
            var controller = manager?.GetDefaultController();
            if (controller == null) throw new System.InvalidOperationException("the game's input manager is not ready");
            return controller;
        }
    }

    private static List<KInputController.KeyDef> KeyDefs()
    {
        var controller = Controller;
        // Bindings are turned into key definitions on the next input update after they change. That update does not run
        // while the game window is without focus (an automated run), so build them here when they are still missing.
        var defs = KeyDefsField?.GetValue(controller) as KInputController.KeyDef[];
        if (defs == null || defs.Length == 0)
        {
            GenerateFlagTable?.Invoke(controller, null);
            defs = KeyDefsField?.GetValue(controller) as KInputController.KeyDef[];
        }

        if (defs == null || defs.Length == 0)
            throw new System.InvalidOperationException("the game has no key definitions yet (not in a running game?)");
        return defs.ToList();
    }

    private static KInputController.KeyDef FindKeyDef(KKeyCode key, Modifier modifier)
    {
        var def = KeyDefs().FirstOrDefault(d => d.mKeyCode == key && d.mModifier == modifier);
        if (def == null)
            throw new System.InvalidOperationException(
                "no action is bound to " + (modifier == Modifier.None ? "" : modifier + "+") + key + ": the game ignores such a key press");
        return def;
    }

    /// <summary>The key a player would press for <paramref name="action"/> (the first key definition that carries it, without modifiers preferred).</summary>
    public static (KKeyCode key, Modifier modifier) KeyFor(Action action)
    {
        var defs = KeyDefs().Where(d => d.mActionFlags[(int)action] && d.mKeyCode < KKeyCode.KleiKeys).ToList();
        var def = defs.FirstOrDefault(d => d.mModifier == Modifier.None) ?? defs.FirstOrDefault();
        if (def == null) throw new System.InvalidOperationException("no keyboard key is bound to " + action);
        return (def.mKeyCode, def.mModifier);
    }

    // Queues the events like the game's input update does for a real key and runs the game's own dispatch at once, so the
    // handlers (KInputHandler, the screens, the tools, mod patches on them) have run when the call returns. An exception in a
    // handler comes out of here, so the test fails on it.
    private static void Send(KInputController.KeyDef def, params bool[] downs)
    {
        var controller = Controller;
        // After an error is logged the game puts up its crash dialog and stops delivering keys to its screens; a player could not type either.
        if (typeof(KScreenManager).GetField("inputDisabled", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true)
            throw new System.InvalidOperationException(
                "the game has its screen input disabled (it does that after an error was logged: the crash dialog); " +
                "the key press would be ignored. Re-enable with KScreenManager.Instance.DisableInput(false) if the test logged the error on purpose");
        WithFocus(() =>
        {
            foreach (var down in downs) controller.QueueButtonEvent(def, down);
            controller.Dispatch();
        });
    }

    /// <summary>Presses and releases a key (with the modifier keys held) through the game's input path. Throws when no action is bound to it.</summary>
    public static void Press(KKeyCode key, Modifier modifier = Modifier.None) => Send(FindKeyDef(key, modifier), true, false);

    /// <summary>Presses the key a player would use for <paramref name="action"/> (see <see cref="KeyFor"/>).</summary>
    public static void Press(Action action)
    {
        var (key, modifier) = KeyFor(action);
        Press(key, modifier);
    }

    /// <summary>Key down only (to hold a key; pair it with <see cref="KeyUp"/>).</summary>
    public static void KeyDown(KKeyCode key, Modifier modifier = Modifier.None) => Send(FindKeyDef(key, modifier), true);

    public static void KeyUp(KKeyCode key, Modifier modifier = Modifier.None) => Send(FindKeyDef(key, modifier), false);

    /// <summary>One notch of the mouse wheel per count: positive up (zoom in), negative down.</summary>
    public static void Scroll(int notches)
    {
        var def = FindKeyDef(notches > 0 ? KKeyCode.MouseScrollUp : KKeyCode.MouseScrollDown, Modifier.None);
        for (var i = 0; i < System.Math.Abs(notches); i++) Send(def, true);
    }

    /// <summary>
    /// Puts the mouse pointer over a cell and keeps it there (the game reads the pointer from <c>KInputManager.GetMousePos()</c>)
    /// until <see cref="ReleaseMouse"/>: the hover card and the active tool's hover follow in the next frames, so wait a frame
    /// (<c>yield return null</c>) before checking. The camera is moved to show the cell.
    /// </summary>
    public static void Hover(int cell)
    {
        FrameCells(cell);
        var pos = WorldPos(cell);
        KInputManager.lockedMousePos = Camera.main.WorldToScreenPoint(pos);
        KInputManager.isMousePosLocked = true;
        var tool = ActiveTool;
        if (tool == null) return;
        // A tool only follows the pointer while the application has focus (Unity's OnApplicationFocus sets it); an automated run often has none.
        if (AppFocusField != null)
        {
            if (!heldTools.ContainsKey(tool)) heldTools[tool] = (bool)AppFocusField.GetValue(tool);
            AppFocusField.SetValue(tool, true);
        }
        WithFocus(() => { tool.OnFocus(true); tool.OnMouseMove(pos); });
    }

    private static readonly FieldInfo AppFocusField = typeof(InterfaceTool).GetField("isAppFocused", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Dictionary<InterfaceTool, bool> heldTools = new Dictionary<InterfaceTool, bool>();

    /// <summary>Gives the pointer back to the real mouse after <see cref="Hover"/>.</summary>
    public static void ReleaseMouse()
    {
        KInputManager.isMousePosLocked = false;
        foreach (var pair in heldTools) AppFocusField?.SetValue(pair.Key, pair.Value);
        heldTools.Clear();
    }

    /// <summary>The pointer enters a UI object (tooltips and hover effects), like a mouse moving over it.</summary>
    public static void HoverUi(GameObject go)
    {
        if (go == null) throw new System.InvalidOperationException("no UI object to hover");
        var data = new PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = go.transform.position };
        WithFocus(() => ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerEnterHandler));
    }

    /// <summary>The pointer leaves a UI object (undoes <see cref="HoverUi"/>).</summary>
    public static void UnhoverUi(GameObject go)
    {
        if (go == null) return;
        var data = new PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = go.transform.position };
        WithFocus(() => ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerExitHandler));
    }
}
