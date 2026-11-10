using Gravitas.TestHarness.Core;
using Newtonsoft.Json.Linq;
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

        server.Map("POST", "/time/pause", _ => { TimeControl.Pause(); return Status(); });
        server.Map("POST", "/time/resume", _ => { TimeControl.Resume(); return Status(); });
        server.Map("POST", "/time/speed", body =>
        {
            TimeControl.SetSpeed((int)Require(body, "speed"));
            return Status();
        });

        // Body: exactly one of {"ticks": N} (0.2 s each), {"seconds": S}, {"cycles": C}; optional "speed" (1-3)
        // and "timeout_s" (real seconds, default 120). Responds when the game has run that long and paused again.
        server.Map("POST", "/time/advance", body =>
        {
            float seconds;
            if (body?["ticks"] != null) seconds = (float)body["ticks"] * TimeControl.SecondsPerTick;
            else if (body?["seconds"] != null) seconds = (float)body["seconds"];
            else if (body?["cycles"] != null) seconds = (float)body["cycles"] * TimeControl.SecondsPerCycle;
            else throw new System.ArgumentException("one of ticks, seconds, cycles is required");
            var timeout = body["timeout_s"] != null ? (int)((float)body["timeout_s"] * 1000) : 120000;
            return TimeControl.Advance(seconds, body["speed"] != null ? (int)body["speed"] : (int?)null, timeout);
        });

        server.Map("POST", "/query", Query.Run);

        // Responds first, then quits on the next main-thread tick.
        server.Map("POST", "/quit", _ => new { quitting = true }, Application.Quit);
    }

    private static JToken Require(JObject body, string name)
    {
        var t = body?[name];
        if (t == null) throw new System.ArgumentException("'" + name + "' is required");
        return t;
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
            speed = speed != null ? speed.GetSpeed() + 1 : (int?)null,
            advancing = TimeControl.Advancing,
            game_over = GameFlowManager.Instance != null ? GameFlowManager.Instance.IsGameOver() : (bool?)null,
            suppressed_dialogs = GameState.SuppressedDialogs,
            cycle = clock != null ? clock.GetCycle() : (int?)null,
            time_in_cycle = clock != null ? clock.GetTimeSinceStartOfCycle() : (float?)null,
            time = clock != null ? clock.GetTime() : (float?)null,
            frame = Time.frameCount,
        };
    }
}
