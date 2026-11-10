using System;

namespace Gravitas.TestHarness.Core;

/// <summary>Harness settings. Environment variables override defaults.</summary>
internal static class HarnessConfig
{
    public const int DefaultPort = 18080;

    public static int Port
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("GRAVITAS_HARNESS_PORT");
            return int.TryParse(s, out var p) && p > 0 && p < 65536 ? p : DefaultPort;
        }
    }
}
