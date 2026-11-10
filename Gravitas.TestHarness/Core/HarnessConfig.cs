using System;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// Harness settings. Launch arguments win over environment variables, which win over defaults.
/// (A game started through Steam does not inherit the caller's environment, hence the arguments.)
/// </summary>
internal static class HarnessConfig
{
    public const int DefaultPort = 18080;

    /// <summary><c>-harness-port N</c> / env GRAVITAS_HARNESS_PORT.</summary>
    public static int Port
    {
        get
        {
            var s = Get("-harness-port", "GRAVITAS_HARNESS_PORT");
            return int.TryParse(s, out var p) && p > 0 && p < 65536 ? p : DefaultPort;
        }
    }

    /// <summary><c>-harness-load PATH</c> / env GRAVITAS_HARNESS_LOAD: save to load automatically at the main menu.</summary>
    public static string AutoLoadPath
    {
        get
        {
            var s = Get("-harness-load", "GRAVITAS_HARNESS_LOAD");
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }

    /// <summary>
    /// <c>-harness-allow-gameover</c> / env GRAVITAS_HARNESS_ALLOW_GAMEOVER=1. By default the game-over check is disabled,
    /// because a save without duplicants would otherwise show the game-over screen, which pauses the game.
    /// </summary>
    public static bool AllowGameOver =>
        Array.IndexOf(Environment.GetCommandLineArgs(), "-harness-allow-gameover") >= 0
        || Environment.GetEnvironmentVariable("GRAVITAS_HARNESS_ALLOW_GAMEOVER") == "1";

    private static string Get(string arg, string env)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == arg)
                return args[i + 1];
        return Environment.GetEnvironmentVariable(env);
    }
}
