"""Gravitas.TestProbe: drive one ONI verification cycle from the shell and print a JSON result.

    python probe.py run            build -> fix mods.json -> launch -> wait ready -> check version -> /quit
    python probe.py run --save "name.sav"   also auto-load a save from saves/ and wait until it is playing
    python probe.py run --save X.sav --steps '[["POST","/time/advance",{"ticks":50}],["POST","/query",{"path":"GameClock.Instance.GetCycle"}]]'
    python probe.py run --project ../../MyMod --tests   test another mod: its harness.json gives build/required_mods/save
    python probe.py run --save X.sav --soak '{"max_cycles":10,"max_minutes":30}'   timed run with FPS/memory/exception sampling
    python probe.py run --keep    same, but leave the game running (for manual poking)
    python probe.py run --setup    first-time registration of a new mod: build -> launch (mods.json untouched) -> wait until
                                   ONI has registered the mods in mods.json -> close. Run once before the first normal run
    python probe.py fix-mods       only check/repair mods.json
    python probe.py restore-mods [--discard]   put back the original mods.json after a run that died or used --keep
    python probe.py http GET /ping call an endpoint of an already running game

Settings come from probe.config.json (git-ignored; see probe.config.example.json).
Each run writes artifacts to runs/<timestamp>/ (result.json, Player.log, build.log).
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import mods_json
import gamelock
import winpos

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
DEFAULT_PLAYER_LOG = Path.home() / "AppData/LocalLow/Klei/Oxygen Not Included/Player.log"


class ProbeError(Exception):
    def __init__(self, stage, message):
        super().__init__(message)
        self.stage, self.message = stage, message


def load_config(project=None):
    cfg = {"port": 18080, "required_mods": [], "build": [], "launch_args": [],
           "ready_timeout_s": 240, "quit_timeout_s": 60, "launch_timeout_s": 90, "load_timeout_s": 240, "step_timeout_s": 300, "save": None, "player_log": None,
           "steam_exe": "C:/Program Files (x86)/Steam/steam.exe", "app_id": 457140, "window": "front"}
    path = HERE / "probe.config.json"
    if not path.exists():
        raise ProbeError("config", f"{path} not found (copy probe.config.example.json)")
    cfg.update(json.loads(path.read_text(encoding="utf-8")))
    for key in ("game_dir", "mods_dir"):
        if not cfg.get(key):
            raise ProbeError("config", f"'{key}' is required in probe.config.json")
    cfg["runs_dir"] = HERE / "runs"
    cfg["saves_dirs"] = [REPO / "saves"]
    cfg["project_build"] = []
    cfg["project_dir"] = None
    cfg_set = set()
    projects = [project] if isinstance(project, str) else list(project or [])
    for index, one in enumerate(projects):
        # Project file of the mod under test (conventionally <mod repo>/harness.json). Keys: build (commands run in
        # its folder), required_mods, exclusive (disable every other mod), save, saves_dir, port, build_harness (default true), runs_dir,
        # fail_on_log_errors / ignore_log_errors (defaults for the tests stage, see protocol.md).
        # Several projects (--project A --project B) are run together: builds and required_mods are combined, the first
        # project that names a save / port / runs_dir wins, exclusive is on if any project asks for it.
        ppath = Path(one).resolve()
        if ppath.is_dir():
            ppath = ppath / "harness.json"
        if not ppath.exists():
            raise ProbeError("config", f"project file not found: {ppath}")
        try:
            proj = json.loads(ppath.read_text(encoding="utf-8"))
        except ValueError as e:
            raise ProbeError("config", f"{ppath} is not valid JSON: {e}")
        pdir = ppath.parent
        if index == 0:
            cfg["project_dir"] = pdir
            cfg["runs_dir"] = (pdir / proj["runs_dir"]) if proj.get("runs_dir") else pdir / ".harness-runs"
        # build commands run in the folder of the project that lists them
        cfg["project_build"] += [(pdir, cmd) for cmd in proj.get("build", [])]
        if proj.get("build_harness", True) is False:
            cfg["build"] = []
        if "required_mods" in proj:
            cfg["required_mods"] = list(dict.fromkeys((cfg["required_mods"] if index else []) + proj["required_mods"]))
        for key in ("save", "port", "fail_on_log_errors", "ignore_log_errors"):
            if key in proj and key not in cfg_set:
                cfg[key] = proj[key]
                cfg_set.add(key)
        if proj.get("exclusive"):
            cfg["exclusive"] = True
        if proj.get("saves_dir"):
            cfg["saves_dirs"].insert(0, pdir / proj["saves_dir"])
    # GRAVITAS_EXCLUSIVE=1: disable every mod that is not required, whatever the project file says (tools/regress.py sets it, so
    # another own mod's tutorial or dialog cannot get in the way of the mod under test).
    if os.environ.get("GRAVITAS_EXCLUSIVE") == "1":
        cfg["exclusive"] = True
    return cfg


def expected_versions():
    src = (REPO / "Gravitas.TestHarness" / "TestHarnessMod.cs").read_text(encoding="utf-8")
    return (re.search(r'Version = "([^"]+)"', src).group(1),
            int(re.search(r"ProtocolVersion = (\d+)", src).group(1)))


def http(cfg, method, path, body=None, timeout=10):
    # Git Bash (MSYS) rewrites a leading "/ping" into "C:/Program Files/Git/ping"; undo that.
    path = re.sub(r"^[A-Za-z]:[/\\]Program Files[/\\]Git(?=/)", "", path.replace("\\", "/"))
    data = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
    req = urllib.request.Request(f"http://127.0.0.1:{cfg['port']}{path}", data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return json.loads(e.read().decode("utf-8"))


def game_lock():
    """Only one run uses the game at a time (see gamelock.py). A busy game ends the run with stage "busy"."""
    return gamelock.hold()


def target_mods_args(cfg):
    """The mods under test, for the harness's conformance tests (the harness itself is not one)."""
    ids = [m for m in cfg["required_mods"] if m != mods_json.HARNESS_ID]
    return ["-harness-mods", ",".join(ids)] if ids else []


def game_running():
    # Bytes, decoded by hand: tasklist writes the console code page (cp932 here), and under python -X utf8 text=True would fail on it.
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OxygenNotIncluded.exe", "/NH"], capture_output=True).stdout or b""
    return "OxygenNotIncluded.exe" in out.decode("cp932", "replace")


def build(cfg, run_dir):
    log = run_dir / "build.log"
    with log.open("w", encoding="utf-8") as f:
        steps = [(c, HERE) for c in cfg["build"]] + [(c, d) for d, c in cfg["project_build"]]
        for cmd, cwd in steps:
            f.write(f"> ({cwd}) {cmd}\n")
            f.flush()
            p = subprocess.run(cmd, shell=True, cwd=cwd, stdout=f, stderr=subprocess.STDOUT)
            if p.returncode != 0:
                tail = "\n".join(log.read_text(encoding="utf-8", errors="replace").splitlines()[-15:])
                raise ProbeError("build", f"'{cmd}' failed (exit {p.returncode}). See {log}\n{tail}")


STACK_RE = re.compile(r"^\s+at |^\(Filename:|^\s*--- End of")
EXC_START_RE = re.compile(r"\w*Exception|\[ERROR\]|Fatal")
TS_RE = re.compile(r"^\[\d\d:\d\d:\d\d\.\d+\] \[\d+\] ")


def extract_exceptions(log_path, limit=20, max_lines=25):
    """Exception / error blocks from Player.log: the first line plus its stack trace, de-duplicated."""
    if not log_path.exists():
        return []
    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    blocks, seen, i = [], {}, 0
    while i < len(lines):
        line = lines[i]
        if EXC_START_RE.search(line) and not STACK_RE.match(line):
            if any("Thread was being aborted" in l for l in lines[i:i + 4]):
                i += 1  # background loader thread cut short by a normal shutdown, not a fault
                continue
            block = [line]
            i += 1
            while i < len(lines) and len(block) < max_lines and (STACK_RE.match(lines[i]) or not TS_RE.match(lines[i]) and lines[i].strip() and EXC_START_RE.search(lines[i])):
                block.append(lines[i])
                i += 1
            key = TS_RE.sub("", block[0]).strip()
            if key in seen:
                blocks[seen[key]]["count"] += 1
            elif len(blocks) < limit:
                seen[key] = len(blocks)
                blocks.append({"count": 1, "text": "\n".join(block)})
            continue
        i += 1
    return blocks


SAFE_MODE_MARKER = "Entering mod safe mode"
SAFE_MODE_MESSAGE = ("ONI started in mod safe mode (no mod is loaded, so the harness never answers): the previous start died "
                     "while loading mods. The game is closed and the next start should be normal; run again. If it repeats, "
                     "run 'fix-mods' and check the KPlayerPrefs value DisableAutoModSafeMode.")


def in_safe_mode(player_log):
    try:
        return SAFE_MODE_MARKER in Path(player_log).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False


def crash_info(player_log, game_dir, since):
    """Evidence of a crash: new dump files, mod safe mode, last log lines."""
    info = {}
    dumps = [str(p) for p in Path(game_dir).glob("*.dmp") if p.stat().st_mtime >= since]
    if dumps:
        info["dump_files"] = dumps
    if player_log.exists():
        lines = player_log.read_text(encoding="utf-8", errors="replace").splitlines()
        info["safe_mode"] = any("Entering mod safe mode" in l for l in lines)
        info["log_tail"] = lines[-30:]
    return info


MB = 1024 * 1024


def _mb(value):
    return None if value is None else round(value / MB, 1)


def process_memory_mb():
    """Working set of OxygenNotIncluded.exe in MB, from tasklist (the game's Mono cannot report it); None if unavailable."""
    try:
        out = (subprocess.run(["tasklist", "/FI", "IMAGENAME eq OxygenNotIncluded.exe", "/FO", "CSV", "/NH"],
                              capture_output=True, timeout=10).stdout or b"").decode("cp932", "replace")
        mem = out.strip().splitlines()[0].strip('"').split('","')[4]  # e.g. 3,412,332 K (locale separators vary)
        return round(int(re.sub(r"\D", "", mem)) / 1024, 1)
    except (IndexError, ValueError, OSError, subprocess.SubprocessError):
        return None


def _count_new_exceptions(player_log, offset):
    """Exception-looking lines appended to Player.log since byte `offset`: (count, new offset)."""
    try:
        with open(player_log, "rb") as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            if size < offset:  # log was rotated / truncated
                offset = 0
            f.seek(offset)
            text = f.read().decode("utf-8", errors="replace")
    except OSError:
        return 0, offset
    count = sum(1 for line in text.splitlines()
                if EXC_START_RE.search(line) and not STACK_RE.match(line) and "Thread was being aborted" not in line)
    return count, size


def soak_stage(cfg, run_dir, opts, player_log, result):
    """Run the loaded save forward in chunks, sampling /metrics after each chunk into <run_dir>/metrics.jsonl.

    opts: max_cycles (10), max_minutes (30; real time; whichever limit is hit first ends the batch), speed (3),
    chunk_seconds (60 game seconds per sample). Returns a summary dict. A dead game or an unresponsive harness raises ProbeError.
    """
    max_cycles = float(opts.get("max_cycles", 10))
    max_minutes = float(opts.get("max_minutes", 30))
    speed = int(opts.get("speed", 3))
    multiplier = opts.get("multiplier")  # debug speed factor (3..100); replaces "speed" when given
    snapshots = bool(opts.get("snapshots", False))  # /snapshot at start and end (files) and a few of its numbers on every row
    chunk = float(opts.get("chunk_seconds", 60))
    target_game_s = max_cycles * 600.0
    advance_timeout_s = max(120.0, chunk * 20)  # real seconds allowed for one chunk (1x is 1 game s per real s)

    def metrics():
        reply = http(cfg, "GET", "/metrics", timeout=15)
        if not reply.get("ok"):
            raise ProbeError("soak", f"/metrics failed: {reply.get('error', {}).get('message')}")
        return reply["data"]

    def snapshot():
        reply = http(cfg, "GET", "/snapshot", timeout=60)
        if not reply.get("ok"):
            raise ProbeError("soak", f"/snapshot failed: {reply.get('error', {}).get('message')}")
        return reply["data"]

    rows_path = run_dir / "metrics.jsonl"
    rows = []
    m = metrics()
    t0_game, t0_real = m["time"], time.time()
    if snapshots:
        (run_dir / "snapshot_start.json").write_text(json.dumps(snapshot(), indent=1), encoding="utf-8")
    log_offset = player_log.stat().st_size if player_log.exists() else 0
    new_exceptions = 0
    stop_reason = None
    start_ws = process_memory_mb()
    summary = result["soak"] = {"options": {"max_cycles": max_cycles, "max_minutes": max_minutes, "speed": speed, "multiplier": multiplier, "chunk_seconds": chunk, "snapshots": snapshots},
               "start": {"cycle": m["cycle"], "duplicants": m.get("duplicants"), "working_set_mb": start_ws}}

    def finish(reason):
        if snapshots:
            try:
                (run_dir / "snapshot_end.json").write_text(json.dumps(snapshot(), indent=1), encoding="utf-8")
            except (ProbeError, urllib.error.URLError, OSError, ValueError):
                pass  # the game may be gone
        fps = sorted(r["fps"] for r in rows if r.get("fps") is not None)
        ws = [r["working_set_mb"] for r in rows if r.get("working_set_mb") is not None]
        elapsed_game = (rows[-1]["game_s"] if rows else 0.0)
        summary.update(
            stop_reason=reason, chunks=len(rows), game_seconds=round(elapsed_game, 1),
            cycles_advanced=round(elapsed_game / 600.0, 2), real_seconds=round(time.time() - t0_real, 1),
            new_log_exceptions=new_exceptions,
            end={"cycle": rows[-1]["cycle"] if rows else m["cycle"], "duplicants": rows[-1].get("duplicants") if rows else None,
                 "working_set_mb": ws[-1] if ws else None})
        if fps:
            summary["fps"] = {"min": fps[0], "p5": fps[max(0, int(len(fps) * 0.05) - 0)], "median": fps[len(fps) // 2], "max": fps[-1]}
        if ws:
            summary["working_set_mb_max"] = max(ws)
            if elapsed_game > 0:
                summary["working_set_growth_mb_per_cycle"] = round((ws[-1] - (start_ws if start_ws is not None else ws[0])) / (elapsed_game / 600.0), 1)
        if rows:
            summary["game_s_per_real_s"] = round(elapsed_game / max(1e-6, rows[-1]["real_s"]), 2)
        return summary

    with rows_path.open("w", encoding="utf-8") as out:
        while True:
            elapsed_game = m["time"] - t0_game
            if m.get("game_over"):
                stop_reason = "game_over"
            elif elapsed_game >= target_game_s - 0.5:
                stop_reason = "max_cycles"
            elif time.time() - t0_real >= max_minutes * 60:
                stop_reason = "max_minutes"
            if stop_reason:
                return finish(stop_reason)
            if not game_running():
                finish("game_exited")
                raise ProbeError("soak", f"game process exited during the soak at cycle {m['cycle']} "
                                         f"(after {len(rows)} chunks, {summary.get('game_seconds')} game s); see Player.log / crash")
            step = min(chunk, target_game_s - elapsed_game)
            before = m
            started = time.time()
            try:
                body = {"seconds": step, "timeout_s": advance_timeout_s}
                body.update({"multiplier": multiplier} if multiplier else {"speed": speed})
                reply = http(cfg, "POST", "/time/advance", body, timeout=advance_timeout_s + 30)
                if not reply.get("ok"):
                    raise ProbeError("soak", f"/time/advance failed: {reply.get('error', {}).get('message')}")
                m = metrics()
            except (urllib.error.URLError, OSError, ValueError) as e:
                finish("harness_unresponsive")
                alive = game_running()
                raise ProbeError("soak", f"harness did not answer ({type(e).__name__}: {e}); game process "
                                         f"{'still running (hang?)' if alive else 'exited (crash?)'} at cycle {before['cycle']}")
            real_dt = m["realtime"] - before["realtime"]
            frames = m["frame"] - before["frame"]
            n, log_offset = _count_new_exceptions(player_log, log_offset)
            new_exceptions += n
            row = {"t": time.strftime("%H:%M:%S"), "real_s": round(time.time() - t0_real, 1),
                   "game_s": round(m["time"] - t0_game, 1), "cycle": m["cycle"], "time_in_cycle": round(m["time"] % 600.0, 1),
                   "chunk_real_s": round(time.time() - started, 2),
                   "fps": round(frames / real_dt, 1) if real_dt > 0.01 else None,
                   "working_set_mb": process_memory_mb(),
                   "mono_heap_mb": _mb(m.get("mono_heap_bytes")), "unity_mb": _mb(m.get("unity_allocated_bytes")),
                   "advance_overshoot_s": round(reply["data"]["elapsed_seconds"] - step, 2),
                   "duplicants": m.get("duplicants"), "log_exceptions_new": n}
            if snapshots:
                snap = snapshot()
                row.update(grid_mass=snap["grid_mass"], grid_temp=snap["grid_mean_temperature"], pickupable_mass=snap["pickupable_mass"])
            rows.append(row)
            out.write(json.dumps(row) + "\n")
            out.flush()


def run_cycle(cfg, keep=False, save=None, steps=None, tests=None, soak=None):
    run_dir = cfg["runs_dir"] / time.strftime("%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True)
    result = {"ok": False, "run_dir": str(run_dir), "stage": "start"}
    player_log = Path(cfg["player_log"]) if cfg.get("player_log") else DEFAULT_PLAYER_LOG
    launched = False
    original_mods = None
    run_started = time.time()
    try:
        if game_running():
            raise ProbeError("precheck", "OxygenNotIncluded.exe is already running; close it first")
        left = mods_json.pending(cfg["mods_dir"])
        if left:
            raise ProbeError("stale-mods-json", f"an earlier run did not restore mods.json (original saved {left.get('saved_at')} in "
                                                f"{left['path']}). Run 'probe.py restore-mods' to put the original back, or "
                                                "'probe.py restore-mods --discard' to keep the current mods.json")
        # Keep only the latest few run folders.
        for old in sorted(cfg["runs_dir"].iterdir())[:-10]:
            shutil.rmtree(old, ignore_errors=True)

        result["stage"] = "build"
        build(cfg, run_dir)

        result["stage"] = "mods.json"
        # The run needs the harness and the mods under test enabled, but the user's own set must come back afterwards.
        original_mods = mods_json.snapshot(cfg["mods_dir"])
        mods_json.save_pending(cfg["mods_dir"], original_mods)  # on disk too, in case this process dies
        mods =mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], cfg.get("exclusive", False), fix=True, dlc_id=cfg.get("dlc_id"))
        result["mods_json"] = mods
        if not mods["ok"]:
            raise ProbeError("mods.json", "; ".join(mods["remaining"]))

        result["stage"] = "launch"
        if player_log.exists():
            player_log.unlink()
        # Launching the exe directly makes Steam show a confirmation prompt; go through Steam instead.
        # (Steam does not pass our environment on, so the port travels as a launch argument.)
        steam = Path(cfg["steam_exe"])
        if not steam.exists():
            raise ProbeError("launch", f"steam_exe not found: {steam}")
        launched = True
        save = save or cfg.get("save")
        save_path = None
        if save:
            if Path(save).is_absolute():
                save_path = Path(save)
            else:
                save_path = next((d / save for d in cfg["saves_dirs"] if (d / save).exists()), cfg["saves_dirs"][0] / save)
            if not save_path.exists():
                raise ProbeError("launch", f"save not found: {save_path}")
        extra = ["-harness-load", str(save_path)] if save_path else []
        winpos.remember_foreground()
        subprocess.Popen([str(steam), "-applaunch", str(cfg["app_id"]), *cfg["launch_args"],
                          "-harness-port", str(cfg["port"]), *target_mods_args(cfg),
                          "-harness-out", str(run_dir / "screenshots"), *extra],
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        started = time.time()
        while not game_running():
            if time.time() - started > cfg["launch_timeout_s"]:
                raise ProbeError("launch", f"OxygenNotIncluded.exe did not start within {cfg['launch_timeout_s']} s "
                                           "(Steam prompt? not logged in?)")
            time.sleep(0.5)
        winpos.place(cfg["window"])

        result["stage"] = "wait-ready"
        ping = None
        while time.time() - started < cfg["ready_timeout_s"]:
            if not game_running():
                raise ProbeError("wait-ready", "game process exited before the harness answered")
            try:
                ping = http(cfg, "GET", "/ping", timeout=3)
                if ping.get("ok"):
                    break
            except (urllib.error.URLError, OSError, ValueError):
                pass
            if in_safe_mode(player_log):
                result["safe_mode"] = True
                raise ProbeError("safe-mode", SAFE_MODE_MESSAGE)
            winpos.place(cfg["window"])
            time.sleep(1)
        else:
            raise ProbeError("wait-ready", f"no /ping response within {cfg['ready_timeout_s']} s")
        result["ready_after_s"] = round(time.time() - started, 1)
        result["ping"] = ping["data"]

        result["stage"] = "version"
        ver = http(cfg, "GET", "/version")["data"]
        exp_ver, exp_proto = expected_versions()
        result["version"] = ver
        if ver["version"] != exp_ver or ver["protocol"] != exp_proto:
            raise ProbeError("version", f"harness mismatch: game has {ver}, expected "
                                        f"version={exp_ver} protocol={exp_proto} (stale deployment?)")

        if save_path:
            result["stage"] = "load-save"
            load_started = time.time()
            status = None
            while time.time() - load_started < cfg["load_timeout_s"]:
                if not game_running():
                    raise ProbeError("load-save", "game process exited while loading the save")
                try:
                    status = http(cfg, "GET", "/status", timeout=5)["data"]
                except (urllib.error.URLError, OSError, ValueError, KeyError):
                    status = None
                if status and status["ready"]:
                    break
                winpos.place(cfg["window"])
                time.sleep(1)
            else:
                raise ProbeError("load-save", f"save not ready within {cfg['load_timeout_s']} s (last status: {status})")
            result["load_after_s"] = round(time.time() - load_started, 1)
            winpos.place(cfg["window"])
            result["status"] = status

        if steps:
            result["stage"] = "steps"
            result["steps"] = []
            for method, path, *rest in steps:
                reply = http(cfg, method.upper(), path, rest[0] if rest else None, timeout=cfg["step_timeout_s"])
                result["steps"].append({"request": [method, path, *rest], "response": reply})
                if not reply.get("ok"):
                    raise ProbeError("steps", f"{method} {path} failed: {reply.get('error', {}).get('message')}")

        if tests is not None:
            result["stage"] = "tests"
            # Project defaults (harness.json): a test run fails on unexpected error logs unless the request says otherwise.
            tests = dict(tests)
            for key in ("fail_on_log_errors", "ignore_log_errors"):
                if key in cfg:
                    tests.setdefault(key, cfg[key])
            reply = http(cfg, "POST", "/tests/run", tests, timeout=cfg["step_timeout_s"] * 6)
            if not reply.get("ok"):
                raise ProbeError("tests", f"/tests/run failed: {reply.get('error', {}).get('message')}")
            data = reply["data"]
            result["tests"] = data
            if data["total"] == 0:
                raise ProbeError("tests", "no test matched the selection")
            if data["failed"] or data["errors"]:
                bad = [f"{r['id']}: {r.get('message')}" for r in data["results"] if r["status"] in ("failed", "error", "timeout")]
                raise ProbeError("tests", f"{data['failed']} failed, {data['errors']} errors: " + "; ".join(bad))

        if soak is not None:
            if not save_path:
                raise ProbeError("soak", "--soak needs a save (--save or 'save' in harness.json)")
            result["stage"] = "soak"
            soak_stage(cfg, run_dir, soak, player_log, result)

        if keep:
            result.update(ok=True, stage="done", note="game left running (--keep)")
            return result

        result["stage"] = "quit"
        http(cfg, "POST", "/quit")
        deadline = time.time() + cfg["quit_timeout_s"]
        while game_running() and time.time() < deadline:
            time.sleep(0.5)
        if game_running():
            raise ProbeError("quit", f"game still running {cfg['quit_timeout_s']} s after /quit")
        result.update(ok=True, stage="done")
    except ProbeError as e:
        result.update(ok=False, stage=e.stage, error=e.message)
    except Exception as e:  # keep the JSON contract even for unexpected failures
        result.update(ok=False, error=f"{type(e).__name__}: {e}")
    finally:
        if launched and not keep and game_running():
            # Never kill a game that is still loading unless we must: that makes ONI boot into mod safe mode.
            try:
                http(cfg, "POST", "/quit", timeout=3)
            except (urllib.error.URLError, OSError, ValueError):
                pass
            deadline = time.time() + 20
            while game_running() and time.time() < deadline:
                time.sleep(0.5)
            if game_running():
                subprocess.run(["taskkill", "/F", "/IM", "OxygenNotIncluded.exe"], capture_output=True)
                time.sleep(2)
                result["killed"] = True
        # Put the user's mods.json back (also clears what a kill during mod loading leaves behind, so the next
        # start is not in mod safe mode). Only once the game is gone: ONI rewrites the file on exit.
        if original_mods is not None and not keep:
            if game_running():
                result["mods_json_restored"] = False
                result["mods_json_note"] = "game still running; mods.json not restored"
            else:
                result["mods_json_restored"] = True
                result["mods_json_changed_by_game_or_probe"] = mods_json.restore(cfg["mods_dir"], original_mods)
                mods_json.clear_pending(cfg["mods_dir"])
        elif keep and original_mods is not None:
            result["mods_json_note"] = ("game left running (--keep): mods.json still has the probe's changes. After quitting, run "
                                        "'probe.py restore-mods' to put the original back (the next run refuses to start until then)")
        if launched and player_log.exists():  # without a launch this would be a stale log of an earlier run
            shutil.copy2(player_log, run_dir / "Player.log")
            result["player_log"] = str(run_dir / "Player.log")
            # Exceptions matter even when the run itself passed.
            result["log_exceptions"] = extract_exceptions(run_dir / "Player.log")
            if not result["ok"]:
                result["crash"] = crash_info(run_dir / "Player.log", cfg["game_dir"], run_started)
        (run_dir / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    return result


def registered_ids(mods_dir):
    """staticIDs ONI has registered in mods.json (empty if the file is missing or unreadable)."""
    try:
        data = json.loads(mods_json.mods_json_path(mods_dir).read_text(encoding="utf-8-sig"))
        return {m.get("staticID") for m in data.get("mods", [])}
    except (OSError, ValueError):
        return set()


def setup_cycle(cfg):
    """First-time registration of new mod DLLs: build, start the game WITHOUT touching mods.json, wait until ONI has
    registered the mods in mods.json, then close the game. Writing mods.json before ONI has seen a mod crashes the start."""
    run_dir = cfg["runs_dir"] / time.strftime("%Y%m%d-%H%M%S-setup")
    run_dir.mkdir(parents=True)
    result = {"ok": False, "run_dir": str(run_dir), "stage": "start", "mode": "setup"}
    player_log = Path(cfg["player_log"]) if cfg.get("player_log") else DEFAULT_PLAYER_LOG
    expected = set(cfg["required_mods"]) | {mods_json.HARNESS_ID}
    launched = False
    try:
        if game_running():
            raise ProbeError("precheck", "OxygenNotIncluded.exe is already running; close it first")
        result["stage"] = "build"
        build(cfg, run_dir)

        result["stage"] = "launch"
        before = registered_ids(cfg["mods_dir"])
        result["registered_before"] = sorted(expected & before)
        if player_log.exists():
            player_log.unlink()
        steam = Path(cfg["steam_exe"])
        if not steam.exists():
            raise ProbeError("launch", f"steam_exe not found: {steam}")
        launched = True
        winpos.remember_foreground()
        subprocess.Popen([str(steam), "-applaunch", str(cfg["app_id"]), *cfg["launch_args"],
                          "-harness-port", str(cfg["port"]), *target_mods_args(cfg), "-harness-out", str(run_dir / "screenshots")],
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        started = time.time()
        while not game_running():
            if time.time() - started > cfg["launch_timeout_s"]:
                raise ProbeError("launch", f"OxygenNotIncluded.exe did not start within {cfg['launch_timeout_s']} s")
            time.sleep(0.5)

        # Registration shows up as new entries in mods.json (ONI writes the file after it has scanned the mods folder).
        result["stage"] = "wait-registered"
        while True:
            if not game_running():
                raise ProbeError("wait-registered", "game process exited before the mods were registered")
            missing = expected - registered_ids(cfg["mods_dir"])
            if not missing:
                break
            if in_safe_mode(player_log):
                result["safe_mode"] = True
                raise ProbeError("safe-mode", SAFE_MODE_MESSAGE)
            if time.time() - started > cfg["ready_timeout_s"]:
                raise ProbeError("wait-registered", f"not registered in mods.json within {cfg['ready_timeout_s']} s: "
                                                    f"{sorted(missing)} (wrong folder or staticID? is mod_info.yaml in place?)")
            time.sleep(1)
        result["registered_after_s"] = round(time.time() - started, 1)

        # Do not close the game while it is still loading mods (that can leave ONI in mod safe mode): wait until the
        # harness reports the main menu, or, if the harness is not loaded (first time), until Player.log has been quiet.
        result["stage"] = "wait-boot"
        while True:
            if not game_running():
                raise ProbeError("wait-boot", "game process exited during start-up")
            try:
                st = http(cfg, "GET", "/status", timeout=3)
                if st.get("ok") and st["data"].get("screen") == "main_menu":
                    break
            except (urllib.error.URLError, OSError, ValueError, KeyError):
                pass
            try:
                if time.time() - player_log.stat().st_mtime > 15:
                    break
            except OSError:
                pass
            if time.time() - started > cfg["ready_timeout_s"]:
                raise ProbeError("wait-boot", f"start-up not finished within {cfg['ready_timeout_s']} s")
            time.sleep(1)
        result["booted_after_s"] = round(time.time() - started, 1)
        time.sleep(3)

        result["stage"] = "quit"
        # Ask the game to quit: through the harness if it answers, otherwise a window close request (taskkill without
        # /F is the normal quit path, so ONI can write mods.json).
        try:
            http(cfg, "POST", "/quit", timeout=3)
        except (urllib.error.URLError, OSError, ValueError):
            subprocess.run(["taskkill", "/IM", "OxygenNotIncluded.exe"], capture_output=True)
        deadline = time.time() + cfg["quit_timeout_s"]
        while game_running() and time.time() < deadline:
            time.sleep(0.5)
        if game_running():
            raise ProbeError("quit", f"game still running {cfg['quit_timeout_s']} s after the close request")
        result.update(ok=True, stage="done", registered=sorted(expected & registered_ids(cfg["mods_dir"])),
                      note="mods.json was not modified by the probe; run the probe normally from here")
    except ProbeError as e:
        result.update(ok=False, stage=e.stage, error=e.message)
    except Exception as e:
        result.update(ok=False, error=f"{type(e).__name__}: {e}")
    finally:
        if launched and game_running():
            result.setdefault("note", "game still running; close it before the next step")
        if player_log.exists():
            shutil.copy2(player_log, run_dir / "Player.log")
            result["player_log"] = str(run_dir / "Player.log")
        (run_dir / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    return result


def summarize(out):
    """A short plain-text report of a probe result (the JSON stays in <run_dir>/result.json)."""
    lines = []
    head = "OK" if out.get("ok") else "FAILED"
    lines.append(f"{head}  stage={out.get('stage')}" + (f"  {out['error']}" if out.get("error") else ""))
    tests = out.get("tests")
    if tests:
        lines.append(f"tests: {tests.get('passed')}/{tests.get('total')} passed, {tests.get('failed')} failed, "
                     f"{tests.get('errors')} errors, {tests.get('skipped')} skipped ({tests.get('duration_ms')} ms)")
        for r in tests.get("results", []):
            if r.get("status") == "passed":
                continue
            lines.append(f"  {r.get('status')}: {r.get('id')}: {str(r.get('message') or '')[:300]}")
            for l in (r.get("log_errors") or [])[:3]:
                lines.append(f"    log: {str(l)[:200]}")
        ids = [r.get("id") for r in tests.get("results", []) if r.get("status") == "passed"]
        if ids:
            lines.append("  passed: " + ", ".join(ids))
    for step in out.get("steps", []) or []:
        lines.append(f"step: {json.dumps(step, ensure_ascii=False)[:300]}")
    sk = out.get("soak")
    if sk:
        lines.append(f"soak: {sk.get('stop_reason')} after {sk.get('cycles_advanced')} cycles / {sk.get('real_seconds')} s real "
                     f"({sk.get('game_s_per_real_s')}x), fps {sk.get('fps')}, mem {sk.get('start', {}).get('working_set_mb')} -> "
                     f"{sk.get('end', {}).get('working_set_mb')} MB (max {sk.get('working_set_mb_max')}, "
                     f"{sk.get('working_set_growth_mb_per_cycle')} MB/cycle), new log exceptions {sk.get('new_log_exceptions')}")
    exc = out.get("log_exceptions") or []
    lines.append(f"log exceptions: {len(exc)}")
    for e in exc[:5]:
        lines.append("  " + " ".join(str(e)[:200].splitlines()))
    if out.get("crash"):
        lines.append(f"crash: {str(out['crash'])[:300]}")
    if out.get("mods_json_restored") is False:
        lines.append("WARNING: mods.json was not restored (run probe.py restore-mods)")
    if out.get("run_dir"):
        lines.append(f"details: {out['run_dir']}")
    return os.linesep.join(lines)


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # the Windows console codec (cp932) cannot print everything the game logs
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--project", action="append", help="harness.json of the mod under test (or its folder): its build commands, required_mods, "
                   "save and runs location are used on top of the harness build. Give it several times to run mods together "
                   "(builds and required_mods are combined; the first project's save and runs folder win)")
    r.add_argument("--setup", action="store_true",
                   help="first-time registration: build, start the game WITHOUT touching mods.json, wait until the mods are "
                        "registered in mods.json, then close it (no save, no tests). Run this before the first normal run "
                        "after placing a new mod's DLL")
    r.add_argument("--keep", action="store_true")
    r.add_argument("--window", choices=winpos.MODES, help="where to keep the game window: front (default), background (behind other windows), "
                   "minimize, offscreen. Also the config key 'window'")
    r.add_argument("--summary", action="store_true",
                   help="print a short plain-text report instead of the full JSON (the JSON is still saved in <run dir>/result.json)")
    r.add_argument("--steps", help="JSON list of [method, path, body?] run in order once the save is playing")
    r.add_argument("--save", help="save to auto-load (file name in saves/, or a path); default: config 'save'")
    r.add_argument("--soak", nargs="?", const="{}", help="after load (and steps/tests), run the save forward in chunks and sample FPS/memory/exceptions "
                   "into <run dir>/metrics.jsonl. JSON options: max_cycles (10), max_minutes (30, real time; the shorter limit wins), "
                   "speed (3), multiplier (debug speed 3..100, instead of speed), snapshots (false), chunk_seconds (60). Needs a save")
    r.add_argument("--tests", nargs="?", const="{}", help="run in-game tests after the steps: a JSON body for /tests/run "
                   "(names/pattern/tags/timeout_s), a pattern string, or nothing for all non-manual tests")
    sub.add_parser("fix-mods")
    rm = sub.add_parser("restore-mods", help="put back the mods.json saved before a run that never restored it")
    rm.add_argument("--discard", action="store_true", help="keep the current mods.json and only drop the saved original")
    h = sub.add_parser("http")
    h.add_argument("method")
    h.add_argument("path")
    h.add_argument("body", nargs="?")
    args = ap.parse_args()
    try:
        cfg = load_config(getattr(args, "project", None))
        if getattr(args, "window", None):
            cfg["window"] = args.window
        if args.cmd == "run" and args.setup:
            with game_lock():
                out = setup_cycle(cfg)
        elif args.cmd == "run":
            tests = None
            if args.tests is not None:
                tests = json.loads(args.tests) if args.tests.lstrip().startswith("{") else {"pattern": args.tests}
            with game_lock():
                out = run_cycle(cfg, args.keep, args.save, json.loads(args.steps) if args.steps else None, tests,
                                json.loads(args.soak) if args.soak is not None else None)
        elif args.cmd == "restore-mods":
            if game_running():
                raise ProbeError("precheck", "OxygenNotIncluded.exe is running; close it first (ONI rewrites mods.json on exit)")
            out = mods_json.recover(cfg["mods_dir"], args.discard)
        elif args.cmd == "fix-mods":
            out = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], cfg.get("exclusive", False), fix=True, dlc_id=cfg.get("dlc_id"))
        else:
            out = http(cfg, args.method.upper(), args.path, json.loads(args.body) if args.body else None)
    except ProbeError as e:
        out = {"ok": False, "stage": e.stage, "error": e.message}
    except gamelock.Busy as e:
        out = {"ok": False, "stage": "busy", "error": str(e)}
    except (urllib.error.URLError, OSError) as e:
        out = {"ok": False, "stage": "http", "error": f"cannot reach the harness (is the game running?): {e}"}
    if getattr(args, "summary", False):
        print(summarize(out))
    else:
        print(json.dumps(out, indent=2, ensure_ascii=False))
    return 0 if out.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
