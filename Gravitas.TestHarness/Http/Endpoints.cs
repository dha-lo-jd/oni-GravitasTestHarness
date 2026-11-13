using System.Linq;
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

        // Body: {"name": "optional-file-name"}. Written to -harness-out DIR; replies with the path.
        server.Map("POST", "/screenshot", body => Screenshot.Capture((string)body?["name"]));

        server.Map("GET", "/tests", TestRunner.List);
        server.Map("POST", "/tests/run", TestRunner.Run);
        server.Map("POST", "/invoke", Invoke.Run);

        // Player input (see Api/Play.cs). A cell is a number (the cell index) or {"x": X, "y": Y}.
        // Body: {"tool": "DigTool"} activates a tool by its type name; {"button": "DigTool"} clicks its toolbar button instead.
        server.Map("POST", "/input/tool", body =>
        {
            if (body?["button"] != null) Play.ClickToolButton((string)body["button"]);
            else Play.Activate(Play.FindTool((string)Require(body, "tool")));
            return new { active_tool = Play.ActiveTool != null ? Play.ActiveTool.GetType().Name : null };
        });
        // Body: {"from": cell, "to": cell, "tool": "optional type name"}. Press at from, release at to, with the active (or named) tool.
        server.Map("POST", "/input/drag", body =>
        {
            if (body?["tool"] != null) Play.Activate(Play.FindTool((string)body["tool"]));
            Play.Drag(ToCell(Require(body, "from")), ToCell(Require(body, "to")));
            return new { active_tool = Play.ActiveTool != null ? Play.ActiveTool.GetType().Name : null };
        });
        // Body: {"cell": cell, "tool": "optional type name"}.
        server.Map("POST", "/input/click", body =>
        {
            if (body?["tool"] != null) Play.Activate(Play.FindTool((string)body["tool"]));
            Play.Click(ToCell(Require(body, "cell")));
            return new { active_tool = Play.ActiveTool != null ? Play.ActiveTool.GetType().Name : null };
        });
        // Body: {"name": "<UI object name>"}: lists the active UI objects with that exact name (path and screen state).
        server.Map("POST", "/ui/find", body => new
        {
            matches = Play.FindUi((string)Require(body, "name")).Select(g => Play.PathOf(g)).ToList(),
        });
        // Body: {"name": "<UI object name>"}: left-clicks the one active UI object with that name (zero or several: error).
        server.Map("POST", "/ui/click", body =>
        {
            Play.ClickUi((string)Require(body, "name"));
            return new { clicked = (string)body["name"] };
        });

        // Responds first, then quits on the next main-thread tick.
        server.Map("POST", "/quit", _ => new { quitting = true }, Application.Quit);
    }

    private static int ToCell(JToken spec)
    {
        if (spec.Type == JTokenType.Integer) return (int)spec;
        if (spec is JObject o && o["x"] != null && o["y"] != null) return Arrange.Cell((int)o["x"], (int)o["y"]);
        throw new System.ArgumentException("a cell is a number or {\"x\": X, \"y\": Y}");
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
