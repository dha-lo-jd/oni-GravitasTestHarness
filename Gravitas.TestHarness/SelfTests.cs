using System;
using System.Collections;
using Gravitas.TestHarness.Core;

namespace Gravitas.TestHarness;

/// <summary>Tests of the harness itself. They double as examples of the test conventions (see protocol.md).</summary>
internal static class SelfTests
{
    [HarnessTest(Name = "harness.game_ready", Tags = "selftest")]
    public static void GameReady()
    {
        Check.True(GameState.Ready, "game should be ready");
        Check.NotNull(GameClock.Instance);
    }

    [HarnessTest(Name = "harness.advance_ticks", Tags = "selftest")]
    public static IEnumerator AdvanceTicks()
    {
        var before = GameClock.Instance.GetTime();
        yield return Wait.Ticks(10);
        var elapsed = GameClock.Instance.GetTime() - before;
        Check.AtLeast(2.0, elapsed, "10 ticks = 2 game seconds");
        Check.Near(2.0, elapsed, 0.5, "should stop within a sim step or two");
    }

    [HarnessTest(Name = "harness.check_throws", Tags = "selftest", RequiresGame = false)]
    public static void CheckThrows()
    {
        Check.Throws<HarnessAssertionException>(() => Check.Equal(1, 2));
    }

    [HarnessTest(Name = "harness.arrange_build", Tags = "selftest")]
    public static IEnumerator ArrangeBuild()
    {
        var cell = Arrange.FindBuildableCell("Ladder", Arrange.HomeCell());
        Check.True(cell >= 0, "a free cell for a ladder near the start");
        var before = Find.Buildings("Ladder").Count;
        var ladder = Arrange.Build("Ladder", cell);
        Check.NotNull(ladder);
        Check.Equal(cell, Arrange.CellOf(ladder), "built where asked");
        yield return null; // the game registers a new object (OnSpawn) one frame later
        Check.Equal(before + 1, Find.Buildings("Ladder").Count, "found among completed buildings");
        yield return Wait.Ticks(2);
        Arrange.Destroy(ladder);
        yield return Wait.Ticks(2);
        Check.Equal(before, Find.Buildings("Ladder").Count, "removed again");
        Check.Throws<InvalidOperationException>(() => Arrange.Build("NoSuchBuilding", cell));
    }

    [HarnessTest(Name = "harness.arrange_spawn", Tags = "selftest")]
    public static IEnumerator ArrangeSpawn()
    {
        var first = Find.Duplicants();
        var home = Arrange.HomeCell();

        var dupe = Arrange.SpawnDuplicant(home);
        yield return Wait.Ticks(2);
        Check.Equal(first.Count + 1, Find.Duplicants().Count, "one more duplicant");
        Arrange.Destroy(dupe);

        var chunk = Arrange.SpawnElement(SimHashes.Copper, home, 123f);
        yield return Wait.Ticks(1);
        Check.Near(123.0, chunk.GetComponent<PrimaryElement>().Mass, 1.0, "chunk mass");
        Check.Equal(SimHashes.Copper, chunk.GetComponent<PrimaryElement>().ElementID);
        Arrange.Destroy(chunk);
    }

    [HarnessTest(Name = "harness.arrange_element", Tags = "selftest")]
    public static IEnumerator ArrangeElement()
    {
        var cell = Arrange.FindBuildableCell("Ladder", Arrange.HomeCell());
        Check.True(cell >= 0);
        Arrange.SetElement(cell, SimHashes.Granite, 500f, 300f); // a solid stays put (a liquid would flow away)
        yield return Wait.Ticks(2);
        Check.Equal(SimHashes.Granite, Find.ElementAt(cell), "the cell holds granite now");
        Check.AtLeast(1.0, Find.MassAt(cell), "mass is reported");
        Arrange.SetElement(cell, SimHashes.Vacuum, 0f, 0f);
        yield return Wait.Ticks(2);
    }

    [HarnessTest(Name = "harness.sim_toolbar_button", Tags = "selftest")]
    public static void SimToolbarButton()
    {
        Play.ClickToolButton("DigTool");
        Check.Equal("DigTool", Play.ActiveTool.GetType().Name, "clicking the dig button makes the dig tool active");
        Play.ClearTool();
        Check.Equal("SelectTool", Play.ActiveTool.GetType().Name, "back to the select tool");
        Play.ClickToolButton("DigTool"); // the toolbar must be usable again after ClearTool
        Check.Equal("DigTool", Play.ActiveTool.GetType().Name, "the dig tool again");
        Play.ClearTool();
        Check.Throws<InvalidOperationException>(() => Play.ClickToolButton("NoSuchTool"));
    }

    [HarnessTest(Name = "harness.sim_drag", Tags = "selftest")]
    public static IEnumerator SimDrag()
    {
        // The sandbox save has no rock: make a granite cell (a free, visible one) and dig it by dragging the dig tool over it.
        var cell = Arrange.FindBuildableCell("Ladder", Arrange.HomeCell());
        Check.True(cell >= 0 && Grid.IsVisible(cell), "a free, visible cell near the start");
        Arrange.SetElement(cell, SimHashes.Granite, 500f, 300f);
        yield return Wait.Ticks(2);
        Check.True(Grid.Solid[cell], "the cell is solid now");
        Check.True(Grid.Objects[cell, (int)ObjectLayer.DigPlacer] == null, "no dig order yet");
        Play.Drag(Play.FindTool("DigTool"), cell, cell);
        yield return null;
        var order = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
        Check.NotNull(order, "dragging the dig tool over the cell places a dig order");
        UnityEngine.Object.Destroy(order);
        Play.Activate(SelectTool.Instance);
        Arrange.SetElement(cell, SimHashes.Vacuum, 0f, 0f);
        yield return Wait.Ticks(2);
    }

    [HarnessTest(Name = "harness.sim_click_selects", Tags = "selftest")]
    public static IEnumerator SimClickSelects()
    {
        var cell = Arrange.FindBuildableCell("Ladder", Arrange.HomeCell());
        Check.True(cell >= 0);
        var ladder = Arrange.Build("Ladder", cell);
        yield return Wait.Ticks(2);
        Play.Activate(SelectTool.Instance);
        SelectTool.Instance.Select(null);
        Play.Click(cell);
        yield return null;
        Check.NotNull(SelectTool.Instance.selected, "a click on the ladder selects something");
        Check.Equal(cell, Grid.PosToCell(SelectTool.Instance.selected.transform.GetPosition()), "what is selected is at the click (" + SelectTool.Instance.selected.name + ")");
        SelectTool.Instance.Select(null);
        Arrange.Destroy(ladder);
        yield return Wait.Ticks(2);
    }

    [HarnessTest(Name = "harness.sim_key_escape", Tags = "selftest")]
    public static IEnumerator SimKeyEscape()
    {
        yield return Wait.Ticks(2);
        Play.ClearTool();
        Play.Activate(Play.FindTool("DigTool"));
        Check.Equal("DigTool", Play.ActiveTool.GetType().Name);
        Play.Press(KKeyCode.Escape); // ToolMenu.OnKeyDown: back to the select tool
        Check.Equal("SelectTool", Play.ActiveTool.GetType().Name, "Esc leaves the tool");
        Check.Throws<System.InvalidOperationException>(() => Play.Press(KKeyCode.F15)); // an unbound key is refused
    }

    [HarnessTest(Name = "harness.sim_key_action", Tags = "selftest")]
    public static IEnumerator SimKeyAction()
    {
        yield return Wait.Ticks(2);
        Play.ClearTool();
        Play.Press(Action.Dig); // the dig hotkey picks the dig tool through the toolbar
        Check.Equal("DigTool", Play.ActiveTool.GetType().Name, "the dig hotkey activates the dig tool");
        Play.ClearTool();
        Check.Equal("SelectTool", Play.ActiveTool.GetType().Name);
    }

    [HarnessTest(Name = "harness.sim_scroll", Tags = "selftest")]
    public static IEnumerator SimScroll()
    {
        var camera = CameraController.Instance;
        var before = camera.targetOrthographicSize;
        Play.Scroll(-3);
        Check.True(camera.targetOrthographicSize > before, "scrolling down zooms out (" + before + " -> " + camera.targetOrthographicSize + ")");
        var mid = camera.targetOrthographicSize;
        Play.Scroll(3);
        Check.True(camera.targetOrthographicSize < mid, "scrolling up zooms in");
        yield return null;
    }

    [HarnessTest(Name = "harness.sim_hover", Tags = "selftest")]
    public static IEnumerator SimHover()
    {
        var cell = Arrange.FindBuildableCell("Ladder", Arrange.HomeCell());
        Check.True(cell >= 0);
        var ladder = Arrange.Build("Ladder", cell);
        yield return Wait.Ticks(2);
        Play.ClearTool();
        try
        {
            Play.Hover(cell);
            yield return null;
            yield return null;
            Check.True(KInputManager.isMousePosLocked, "the pointer is held on the cell");
            Check.NotNull(SelectTool.Instance.hover, "the select tool hovers something");
            var hovered = SelectTool.Instance.hover.gameObject;
            Check.Equal(cell, Grid.PosToCell(hovered.transform.GetPosition()), "what is hovered is at the pointer (" + hovered.name + ")");
        }
        finally
        {
            Play.ReleaseMouse();
            Arrange.Destroy(ladder);
        }
        Check.False(KInputManager.isMousePosLocked, "the pointer is released");
        yield return Wait.Ticks(2);
    }

    // Named to run after the others (tests run in name order): an error log puts the game into its crash-dialog state, in which it
    // ignores keys and the pointer, which would break the input tests that run after it.
    [HarnessTest(Name = "harness.zz_log_errors", Tags = "selftest", RequiresGame = false)]
    public static IEnumerator LogErrors()
    {
        Check.NoLogErrors();
        UnityEngine.Debug.LogError("harness-selftest: intentional error log");
        Check.Throws<HarnessAssertionException>(() => Check.NoLogErrors());
        Check.ExpectLogError("intentional error log");
        Check.NoLogErrors(); // consumed, so it no longer counts
        Check.Throws<HarnessAssertionException>(() => Check.ExpectLogError("never logged"));
        // Logging an error makes the game disable its screen input (its crash dialog, a frame or two later); give it back for the tests that follow.
        yield return null;
        yield return null;
        yield return null;
        KScreenManager.Instance?.DisableInput(false);
    }

    // Passes by itself; with "fail_on_log_errors": true in the run it is reported as failed.
    [HarnessTest(Name = "harness.demo_log_error", Tags = "manual,demo", RequiresGame = false)]
    public static void DemoLogError() => UnityEngine.Debug.LogError("harness-demo: error log without an assertion");

    // Tag "manual": never runs by default, only to see how failures are reported.
    [HarnessTest(Name = "harness.demo_fail", Tags = "manual,demo", RequiresGame = false)]
    public static void DemoFail() => Check.Equal(1, 2, "demo");

    [HarnessTest(Name = "harness.demo_error", Tags = "manual,demo", RequiresGame = false)]
    public static void DemoError() => throw new System.InvalidOperationException("demo error");
}
