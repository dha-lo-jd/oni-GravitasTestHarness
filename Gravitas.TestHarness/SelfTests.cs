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

    [HarnessTest(Name = "harness.log_errors", Tags = "selftest", RequiresGame = false)]
    public static void LogErrors()
    {
        Check.NoLogErrors();
        UnityEngine.Debug.LogError("harness-selftest: intentional error log");
        Check.Throws<HarnessAssertionException>(() => Check.NoLogErrors());
        Check.ExpectLogError("intentional error log");
        Check.NoLogErrors(); // consumed, so it no longer counts
        Check.Throws<HarnessAssertionException>(() => Check.ExpectLogError("never logged"));
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
