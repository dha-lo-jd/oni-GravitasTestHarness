using System;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// Pause / resume / speed / advance. Main thread only.
/// Units: <c>seconds</c> are game seconds (GameClock time); a cycle is 600 s; a tick is 0.2 s (one 200 ms sim step).
/// </summary>
public static class TimeControl
{
    public const float SecondsPerCycle = 600f;
    public const float SecondsPerTick = 0.2f;

    private static Pending _active;
    private static float _target;
    private static float _start;
    private static float _requested;

    public static bool Advancing => _active != null;

    private static SpeedControlScreen Speed()
    {
        if (!GameState.Ready) throw new InvalidOperationException("No game is running (screen=" + GameState.Screen() + ")");
        return SpeedControlScreen.Instance;
    }

    public static void Pause()
    {
        var sp = Speed();
        if (!sp.IsPaused) sp.Pause(false);
    }

    public static void Resume()
    {
        var sp = Speed();
        if (_active != null) throw new InvalidOperationException("An advance is in progress");
        while (sp.IsPaused) sp.Unpause(false);
    }

    /// <summary>Game speed 1..3 (the in-game 1x/2x/3x buttons). Does not change the paused state.</summary>
    public static void SetSpeed(int speed)
    {
        if (speed < 1 || speed > 3) throw new ArgumentException("speed must be 1, 2 or 3");
        Speed().SetSpeed(speed - 1);
    }

    /// <summary>
    /// Debug speed: the game's "ridiculous speed" is the 3x button with <c>ultraSpeed</c> raised from 3 to 10; any factor works
    /// (<c>Time.timeScale</c> becomes <paramref name="factor"/> while running, speed 3 is selected). Pass 3 to go back to normal 3x.
    /// </summary>
    public static void SetMultiplier(float factor)
    {
        if (!(factor >= 3f) || factor > 100f) throw new ArgumentException("multiplier must be between 3 and 100");
        var sp = Speed();
        sp.ultraSpeed = factor;
        sp.SetSpeed(2);
    }

    /// <summary>Runs the game for <paramref name="seconds"/> game seconds, then pauses and completes the result.</summary>
    public static Pending Advance(float seconds, int? speed, int timeoutMs, float? multiplier = null)
    {
        var sp = Speed();
        if (_active != null) throw new InvalidOperationException("An advance is already in progress");
        if (!(seconds > 0f)) throw new ArgumentException("amount to advance must be > 0");
        if (speed.HasValue) SetSpeed(speed.Value);
        if (multiplier.HasValue) SetMultiplier(multiplier.Value);

        _start = GameClock.Instance.GetTime();
        _target = _start + seconds;
        _requested = seconds;
        var pending = new Pending { TimeoutMs = timeoutMs, OnTimeout = Cancel };
        _active = pending;
        while (sp.IsPaused) sp.Unpause(false);
        return pending;
    }

    /// <summary>
    /// Called every frame (MainThreadDispatcher.Update, which runs even while the game is paused). While an advance is in progress the
    /// game must stay unpaused: on U59 the game re-pauses itself once shortly after the first unpause following a save load, and
    /// OnSimStep is not called while paused, so without this the advance would wait forever.
    /// </summary>
    internal static void KeepRunning()
    {
        if (_active == null) return;
        var sp = SpeedControlScreen.Instance;
        if (sp != null && sp.IsPaused) sp.Unpause(false);
    }

    /// <summary>Called after every 33 ms sim step (GameClock.Sim33ms).</summary>
    internal static void OnSimStep()
    {
        if (_active == null) return;
        var clock = GameClock.Instance;
        if (clock == null || clock.GetTime() < _target) return;

        var pending = _active;
        _active = null;
        var sp = SpeedControlScreen.Instance;
        if (sp != null && !sp.IsPaused) sp.Pause(false);
        var elapsed = clock.GetTime() - _start;
        pending.Complete(new
        {
            requested_seconds = _requested,
            elapsed_seconds = elapsed,
            elapsed_ticks = elapsed / SecondsPerTick,
            cycle = clock.GetCycle(),
            time_in_cycle = clock.GetTimeSinceStartOfCycle(),
            time = clock.GetTime(),
        });
    }

    internal static void Cancel()
    {
        _active = null;
        var sp = SpeedControlScreen.Instance;
        if (sp != null && !sp.IsPaused) sp.Pause(false);
    }

    internal static void Reset()
    {
        _active = null;
    }
}
