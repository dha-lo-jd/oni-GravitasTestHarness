# Harness HTTP protocol (v1)

The harness mod listens on `127.0.0.1:<port>` (default 18080; `-harness-port N` launch argument or env `GRAVITAS_HARNESS_PORT`).
All handlers run on the Unity main thread. Bodies and replies are JSON.

Reply envelope:

```json
{ "ok": true,  "data": { } }
{ "ok": false, "error": { "message": "...", "stackTrace": "..." } }
```

HTTP status: 200 ok, 404 unknown path, 405 wrong method, 500 handler exception.

## Launch arguments (given to the game, e.g. through `Steam.exe -applaunch 457140 ...`)

| Argument | Meaning |
| --- | --- |
| `-harness-port N` | HTTP port (env fallback `GRAVITAS_HARNESS_PORT`) |
| `-harness-load PATH` | Save to load automatically once the main menu appears (env fallback `GRAVITAS_HARNESS_LOAD`) |
| `-harness-out DIR` | Directory for screenshots (env fallback `GRAVITAS_HARNESS_OUT`). The probe passes `runs/<timestamp>/screenshots` |
| `-harness-allow-gameover` | Keep the game-over check. Off by default: a colony without duplicants would show the game-over screen, which pauses the game |

## Endpoints

| Endpoint | Body | Reply `data` |
| --- | --- | --- |
| `GET /ping` | – | `pong`, `frame`, `realtime`, `cycle` (null before a game is loaded) |
| `GET /version` | – | `version`, `protocol`. The probe compares both with the source and fails on a mismatch (stale deployment) |
| `GET /status` | – | see below |
| `POST /load` | `{"path": "<.sav>"}` | status. Returns at once; poll `/status` until `ready`. Main menu only |
| `POST /time/pause` | – | status |
| `POST /time/resume` | – | status |
| `POST /time/speed` | `{"speed": 1..3}` | status (does not change paused state) |
| `POST /time/advance` | one of `{"ticks":N}` `{"seconds":S}` `{"cycles":C}`; optional `"speed":1..3`, `"timeout_s"` (default 120) | `requested_seconds`, `elapsed_seconds`, `elapsed_ticks`, `cycle`, `time_in_cycle`, `time`. Replies after the game has run that long and is paused again |
| `POST /query` | see below | see below |
| `POST /screenshot` | `{"name": "optional"}` | `path`, `width`, `height`, `bytes`. PNG of the game view, taken at the end of the next frame, written to `-harness-out` |
| `GET /tests` | – | `count`, `tests[]` (`id`, `tags`, `requires_game`, `timeout_s`, `invalid`) |
| `POST /tests/run` | `{"names":[..],"pattern":"..","tags":[..],"timeout_s":N}` (all optional) | `total`, `passed`, `failed`, `errors`, `skipped`, `duration_ms`, `results[]`. Replies when every selected test is done |
| `POST /invoke` | `{"method":"Ns.Type.StaticMethod","args":[..]}` or `{"target":"Game.Instance","method":"Name","args":[..]}` | `method`, `result` (JSON as in /query) |
| `POST /quit` | – | `{"quitting": true}`; the game exits right after the reply |

Units: seconds are game seconds, a cycle is 600 s, a tick is 0.2 s (one 200 ms sim step). Advance stops within one 33 ms sim step of the target, so `elapsed_seconds` can exceed the request slightly. Never assert exact equality.

### `/status`

`screen` (`main_menu` | `loading` | `playing` | `unknown`), `ready` (= `screen == "playing"`), `loading_save`, `paused`, `speed` (1..3),
`advancing`, `game_over`, `cycle`, `time_in_cycle`, `time`, `frame`, `suppressed_dialogs`.

`ready` becomes true when the in-game scene has finished spawning (`Game.OnSpawnComplete`). Saves load **paused**.

`suppressed_dialogs`: informational mod dialogs (for example "mods differ from the save") are recorded here instead of being shown,
because they block the UI. Restart prompts are not suppressed.

### `/query`

Static path:

```json
{ "path": "GameClock.Instance.cycle" }
```

The path starts with a type (simple or full name), then static members, then instance members; `[n]` indexes lists. Private members are readable.

Components in the scene:

```json
{ "type": "Building", "select": ["name", "transform.position"], "limit": 50, "include_inactive": false }
```

Reply: `type`, `count` (all found), `returned`, `items` (each has `id` plus the selected paths, or `name`/`position` without `select`).
A path that fails inside `select` yields `"<error: ...>"` for that entry. Values: primitives and strings as is, enums as names, Unity objects as
`{_type, name, id}`, collections as arrays (max 100), structs by their fields, other objects as a shallow object (depth 2).

### `/invoke`

Reflection call, nothing else (no expression evaluation). Without `target`, `method` is `Type.Method` and must be static.
With `target` (a static path as in `/query`, e.g. `Game.Instance` or `Components.LiveMinionIdentities[0]`), `method` is the bare
name of an instance method of that value. Arguments are converted with Newtonsoft to the parameter types. Private methods work.
Overloads are picked by argument count, then by whether the arguments convert.

## In-game tests

Mark a static, parameterless method with `[HarnessTest]` (`Gravitas.TestHarness.HarnessTestAttribute`). The harness scans every loaded
assembly except the game's own and the system ones, so tests live in the mod under development (or a test mod), for example inside
`#if DEBUG`. The attribute is matched by its simple name: a mod may declare its own `HarnessTestAttribute` instead of referencing
`Gravitas.TestHarness.dll` (a reference is only needed for `Check` and `Wait`).

```csharp
[HarnessTest(Name = "mymod.heater_warms", Tags = "heat", TimeoutSeconds = 300)]
public static IEnumerator HeaterWarms()
{
    var before = ReadTemperature();
    yield return Wait.Cycles(1);          // advances game time, pauses again afterwards
    Check.AtLeast(before + 1, ReadTemperature());
}
```

- Return `void` (synchronous) or `IEnumerator`. Yield `Wait.Ticks(n)` / `Wait.Seconds(s)` / `Wait.Cycles(c)` to advance game time,
  `null` to wait one frame, or a nested `IEnumerator`.
- A test passes if it returns. `Check.*` throws `HarnessAssertionException` → status `failed`; any other exception → `error` (with stack);
  exceeding `TimeoutSeconds` (real seconds, default 120) → `timeout`; `RequiresGame` (default true) without a loaded save → `skipped`.
- Tags: tests tagged `manual` run only when named or selected by tag. Selection: `names` exact ids, `pattern` glob on the id
  (substring without wildcards), `tags` any match. No selector = all non-manual tests.
- Game state carries over between tests in one run. Put a test that needs a clean state in its own save/run.
- Error/exception log lines emitted during a test are returned as `log_errors` (they do not fail the test by themselves).
- The probe: `python probe.py run --save X.sav --tests` (all), `--tests "pattern"` or `--tests '{"tags":["heat"]}'`. A failed/errored test
  fails the run (`stage: "tests"`), and the full results stay in `result.json`.
- `Gravitas.TestHarness/SelfTests.cs` has the harness's own tests (tag `selftest`) and failing demos (tag `manual,demo`).
