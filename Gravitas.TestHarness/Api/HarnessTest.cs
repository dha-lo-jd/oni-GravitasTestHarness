using System;

namespace Gravitas.TestHarness;

/// <summary>
/// Marks a static, parameterless method as an in-game test. The method returns <c>void</c> (synchronous) or
/// <c>IEnumerator</c> (coroutine; <c>yield return Wait.Ticks(10)</c> advances game time, <c>yield return null</c> waits a frame).
/// A test passes when it returns normally; use <see cref="Check"/> (or throw) to fail.
///
/// The harness finds the attribute by its simple name, so a mod may also declare its own
/// <c>HarnessTestAttribute</c> with the same property names instead of referencing this assembly
/// (only <c>Wait</c> needs the reference).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HarnessTestAttribute : Attribute
{
    /// <summary>Id shown in results. Default: <c>Namespace.Type.Method</c>.</summary>
    public string Name { get; set; }

    /// <summary>Comma-separated tags. Tests tagged <c>manual</c> run only when selected by name or tag.</summary>
    public string Tags { get; set; }

    /// <summary>Real-time limit in seconds (default 120). Advancing game time counts as real time.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Default true: the test is skipped unless a save is loaded and ready.</summary>
    public bool RequiresGame { get; set; } = true;
}

public sealed class HarnessAssertionException : Exception
{
    public HarnessAssertionException(string message) : base(message) { }
}

/// <summary>Assertions. They throw <see cref="HarnessAssertionException"/>, which the runner reports as "failed".</summary>
public static class Check
{
    public static void Fail(string message) => throw new HarnessAssertionException(message);

    public static void True(bool condition, string message = null)
    {
        if (!condition) Fail(message ?? "expected true");
    }

    public static void False(bool condition, string message = null)
    {
        if (condition) Fail(message ?? "expected false");
    }

    public static void NotNull(object value, string message = null)
    {
        if (value == null) Fail(message ?? "expected non-null");
    }

    public static void Equal(object expected, object actual, string message = null)
    {
        if (!Equals(expected, actual))
            Fail((message != null ? message + ": " : "") + "expected <" + expected + "> but was <" + actual + ">");
    }

    /// <summary>Float comparison with a tolerance (never compare simulation floats for equality).</summary>
    public static void Near(double expected, double actual, double tolerance, string message = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            Fail((message != null ? message + ": " : "") + "expected " + expected + " ± " + tolerance + " but was " + actual);
    }

    public static void AtLeast(double minimum, double actual, string message = null)
    {
        if (!(actual >= minimum))
            Fail((message != null ? message + ": " : "") + "expected >= " + minimum + " but was " + actual);
    }

    /// <summary>Passes when <paramref name="action"/> throws <typeparamref name="T"/>.</summary>
    public static void Throws<T>(System.Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        catch (Exception e) { Fail("expected " + typeof(T).Name + " but got " + e.GetType().Name); }
        Fail("expected " + typeof(T).Name + " but nothing was thrown");
    }
}

/// <summary>Request yielded from a coroutine test: run the game for this long, then pause again.</summary>
public sealed class AdvanceRequest
{
    public float Seconds;
    public int? Speed;
}

/// <summary>Things a coroutine test can <c>yield return</c>.</summary>
public static class Wait
{
    public static AdvanceRequest Seconds(float seconds, int? speed = null) => new AdvanceRequest { Seconds = seconds, Speed = speed };
    public static AdvanceRequest Ticks(int ticks, int? speed = null) => Seconds(ticks * Core.TimeControl.SecondsPerTick, speed);
    public static AdvanceRequest Cycles(float cycles, int? speed = null) => Seconds(cycles * Core.TimeControl.SecondsPerCycle, speed);
}
