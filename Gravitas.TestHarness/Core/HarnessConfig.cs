using System;

namespace Gravitas.TestHarness.Core;

/// <summary>Harness settings. Environment variables override defaults.</summary>
internal static class HarnessConfig
{
    public const int DefaultPort = 18080;

    /// <summary>Port: <c>-harness-port N</c> launch argument, else env GRAVITAS_HARNESS_PORT, else the default.
    /// (A game started through Steam does not inherit the caller's environment, hence the argument.)</summary>
    public static int Port
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-harness-port" && TryPort(args[i + 1], out var fromArg))
                    return fromArg;
            return TryPort(Environment.GetEnvironmentVariable("GRAVITAS_HARNESS_PORT"), out var p) ? p : DefaultPort;
        }
    }

    private static bool TryPort(string s, out int port)
    {
        return int.TryParse(s, out port) && port > 0 && port < 65536;
    }
}
