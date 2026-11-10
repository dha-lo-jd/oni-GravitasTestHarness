using Gravitas.TestHarness.Core;
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

        server.Map("GET", "/status", _ => Status());

        // Body: {"path": "<absolute or relative path to a .sav>"}. Returns at once; poll /status for ready.
        server.Map("POST", "/load", body =>
        {
            GameState.LoadSave((string)body?["path"]);
            return Status();
        });

        // Responds first, then quits on the next main-thread tick.
        server.Map("POST", "/quit", _ => new { quitting = true }, Application.Quit);
    }

    private static object Status()
    {
        var clock = GameClock.Instance;
        var speed = SpeedControlScreen.Instance;
        return new
        {
            screen = GameState.Screen(),
            ready = GameState.Ready,
            loading_save = GameState.LoadRequested,
            paused = speed != null ? speed.IsPaused : (bool?)null,
            speed = speed != null ? speed.GetSpeed() : (int?)null,
            cycle = clock != null ? clock.GetCycle() : (int?)null,
            time_in_cycle = clock != null ? clock.GetTimeSinceStartOfCycle() : (float?)null,
            time = clock != null ? clock.GetTime() : (float?)null,
            frame = Time.frameCount,
        };
    }
}
