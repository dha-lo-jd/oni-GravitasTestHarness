using UnityEngine;

namespace Gravitas.TestHarness.Http;

internal static class Endpoints
{
    public static void Register(HarnessServer server)
    {
        server.Map("GET", "/ping", _ => new
        {
            pong = true,
            frame = Time.frameCount,
            realtime = Time.realtimeSinceStartup,
            cycle = GameClock.Instance != null ? GameClock.Instance.GetCycle() : (int?)null,
        });

        server.Map("GET", "/version", _ => new
        {
            version = TestHarnessMod.Version,
            protocol = TestHarnessMod.ProtocolVersion,
        });

        // Responds first, then quits on the next main-thread tick.
        server.Map("POST", "/quit", _ => new { quitting = true }, Application.Quit);
    }
}
