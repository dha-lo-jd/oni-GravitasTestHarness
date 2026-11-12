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
