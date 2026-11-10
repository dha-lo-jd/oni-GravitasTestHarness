"""Gravitas.TestProbe as an MCP server (stdio, JSON-RPC 2.0, no dependencies).

A thin wrapper over probe.py: every tool returns the same JSON the command line prints.
Register it, e.g.:   claude mcp add gravitas-probe -- python <repo>/Gravitas.TestProbe/mcp_server.py

Tools
  harness_run       build -> mods.json -> launch ONI -> load save -> steps/tests -> quit. Takes minutes.
  harness_http      call an endpoint of an already running game (after harness_run with keep=true)
  harness_fix_mods  check/repair mods.json (game must not be running)

harness_run starts the game and rewrites the real mods.json: call it only when the user wants an in-game run.
"""
import json
import sys
import traceback
import urllib.error

import probe

PROTOCOL_FALLBACK = "2024-11-05"
SERVER_INFO = {"name": "gravitas-probe", "version": "0.1.0"}

TOOLS = [
    {
        "name": "harness_run",
        "description": (
            "Run one ONI verification cycle: build, repair mods.json, launch the game through Steam, load a save, run steps/tests, quit. "
            "Takes about 1-5 minutes and starts the real game (rewrites the user's mods.json), so use it only when an in-game run is wanted. "
            "Returns the probe result JSON: ok, stage, error, tests, steps, log_exceptions, crash, run_dir."),
        "inputSchema": {
            "type": "object",
            "properties": {
                "project": {"type": "string", "description": "Path of the mod repo (or its harness.json) under test. Omit to test the harness itself."},
                "save": {"type": "string", "description": "Save file name (looked up in the project's saves_dir, then the harness saves/) or absolute path."},
                "tests": {"description": "Run in-game [HarnessTest] tests after the steps. A glob/substring pattern string, or an object {names, pattern, tags, timeout_s}. Use {} for all non-manual tests. Omit to run none."},
                "steps": {"type": "array", "description": "HTTP calls run in order once the save is playing: [[method, path, body?], ...], e.g. [[\"POST\",\"/time/advance\",{\"ticks\":50}]]",
                          "items": {"type": "array"}},
                "keep": {"type": "boolean", "description": "Leave the game running afterwards (then use harness_http)."},
            },
        },
    },
    {
        "name": "harness_http",
        "description": "Call an endpoint of the harness in an already running game (see protocol.md): GET /status, POST /query, POST /invoke, POST /screenshot, POST /time/advance, POST /tests/run, POST /quit, ...",
        "inputSchema": {
            "type": "object",
            "properties": {
                "method": {"type": "string", "enum": ["GET", "POST"]},
                "path": {"type": "string", "description": "e.g. /status"},
                "body": {"type": "object"},
                "project": {"type": "string", "description": "Only needed if the mod's harness.json sets a non-default port."},
            },
            "required": ["method", "path"],
        },
    },
    {
        "name": "harness_fix_mods",
        "description": "Check and repair mods.json (re-enable the harness and required mods after a crash). The game must not be running.",
        "inputSchema": {"type": "object", "properties": {"project": {"type": "string"}}},
    },
]


def call_tool(name, args):
    try:
        cfg = probe.load_config(args.get("project"))
        if name == "harness_run":
            tests = args.get("tests")
            if isinstance(tests, str):
                tests = {"pattern": tests}
            return probe.run_cycle(cfg, bool(args.get("keep")), args.get("save"), args.get("steps"), tests)
        if name == "harness_http":
            return probe.http(cfg, args["method"].upper(), args["path"], args.get("body"), timeout=cfg["step_timeout_s"])
        if name == "harness_fix_mods":
            return probe.mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], fix=True)
        return {"ok": False, "error": "unknown tool " + name}
    except probe.ProbeError as e:
        return {"ok": False, "stage": e.stage, "error": e.message}
    except (urllib.error.URLError, OSError) as e:
        return {"ok": False, "stage": "http", "error": f"cannot reach the harness (is the game running?): {e}"}
    except Exception as e:  # never kill the server
        return {"ok": False, "error": f"{type(e).__name__}: {e}", "trace": traceback.format_exc()}


def handle(msg):
    method = msg.get("method")
    if method == "initialize":
        return {"protocolVersion": msg.get("params", {}).get("protocolVersion", PROTOCOL_FALLBACK),
                "capabilities": {"tools": {}}, "serverInfo": SERVER_INFO}
    if method == "ping":
        return {}
    if method == "tools/list":
        return {"tools": TOOLS}
    if method == "tools/call":
        params = msg.get("params", {})
        out = call_tool(params.get("name"), params.get("arguments") or {})
        return {"content": [{"type": "text", "text": json.dumps(out, indent=2, ensure_ascii=False)}],
                "isError": not (isinstance(out, dict) and out.get("ok", True))}
    raise KeyError(method)


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if "id" not in msg:  # notification (notifications/initialized, ...)
            continue
        try:
            reply = {"jsonrpc": "2.0", "id": msg["id"], "result": handle(msg)}
        except KeyError as e:
            reply = {"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": f"method not found: {e}"}}
        sys.stdout.write(json.dumps(reply, ensure_ascii=False) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
