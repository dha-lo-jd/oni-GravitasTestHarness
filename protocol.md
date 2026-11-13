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
| `POST /tests/run` | `{"names":[..],"pattern":"..","tags":[..],"timeout_s":N,"fail_on_log_errors":bool,"ignore_log_errors":[regex..]}` (all optional) | `total`, `passed`, `failed`, `errors`, `skipped`, `duration_ms`, `results[]`. Replies when every selected test is done |
| `POST /invoke` | `{"method":"Ns.Type.StaticMethod","args":[..]}` or `{"target":"Game.Instance","method":"Name","args":[..]}` | `method`, `result` (JSON as in /query) |
| `POST /input/tool` | `{"tool":"DigTool"}` (activate by C# type name) or `{"button":"DigTool"}` (click its toolbar button) | `active_tool` |
| `POST /input/drag` | `{"from":cell,"to":cell,"tool":"optional type name"}`; a cell is its index or `{"x":X,"y":Y}` | `active_tool` |
| `POST /input/click` | `{"cell":cell,"tool":"optional type name"}` | `active_tool` |
| `POST /ui/find` | `{"name":"<UI object name>"}` | `matches[]`: hierarchy paths of the active UI objects with that exact name |
| `POST /ui/click` | `{"name":"<UI object name>"}` (exactly one match, else an error) | `clicked` |
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

Built-in diagnostics reachable through `/invoke` (static methods, `Gravitas.TestHarness.Core.HarmonyInspect`):
`Duplicates(ownerContains)` lists patch methods registered more than once on the same target (a double `PatchAll`), with the patch
count per Harmony id; `Patches(ownerContains)` lists every patch of the matching Harmony ids.

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
- Error/exception/assert log lines emitted during a test are returned as `log_errors` (they do not fail the test by themselves).
  Error logs as assertions:
  - `Check.NoLogErrors()` fails if an unexpected error line was logged since the test started; `Check.ExpectLogError(regex)` passes
    when one matching was logged and marks it expected (to test that a mod reports bad input).
  - Run option `"fail_on_log_errors": true` (in the `/tests/run` body): a test that passed but logged unexpected error lines is
    reported as `failed` ("N unexpected error log(s); first: ..."). `"ignore_log_errors": ["regex", ...]` lists messages that never
    count (known game noise). A test that errors on purpose opts out with `[HarnessTest(AllowLogErrors = true)]`.
  - The probe takes the same two keys from `harness.json` as defaults for its tests stage.
  - Only lines logged while a test runs are counted. Errors at game start-up are in the probe's `log_exceptions`.
- The probe: `python probe.py run --save X.sav --tests` (all), `--tests "pattern"` or `--tests '{"tags":["heat"]}'`. A failed/errored test
  fails the run (`stage: "tests"`), and the full results stay in `result.json`.
### Arranging game state (`Arrange`, `Find`)

Tests can build their own scene instead of relying on a hand-made save (needs the `Gravitas.TestHarness.dll` reference). They do what the
in-game sandbox tools do:

| Call | Does |
|---|---|
| `Arrange.HomeCell()` | A cell in the starting area (printing pod, else first duplicant) |
| `Arrange.Cell(x, y)` / `CellOf(go)` / `XY(cell)` | Cell <-> coordinates |
| `Arrange.FindBuildableCell(prefabId, near, radius)` | A free cell where the building fits now, or -1 |
| `Arrange.Build(prefabId, cell, orientation, elements)` | Instantly places a completed building |
| `Arrange.SpawnElement(SimHashes, cell, mass, temp)` | An element chunk |
| `Arrange.Spawn(prefabId, cell)` | An item or creature prefab |
| `Arrange.SpawnDuplicant(cell)` | A random duplicant |
| `Arrange.SetElement(cell, element, mass, temp)` | Replaces the cell's element (applies on the next simulation step) |
| `Arrange.Destroy(go)` | Removes an object |
| `Find.Buildings(prefabId)` / `Duplicants()` / `ElementAt` / `TemperatureAt` / `MassAt` | Lookups |

A setup that cannot be done throws `InvalidOperationException` (status `error`). A new object is registered with the game's lists one frame
later: `yield return null` before `Find.*`. Objects a test creates stay in the game for the following tests: destroy them at the end.

### Playing the player's input (`Play`, since 0.8.0)

A test can do what a player does with the mouse (the HTTP endpoints `/input/*` and `/ui/*` call the same code). `Play` calls the entry
points the game itself calls for a real mouse (`InterfaceTool.OnLeftClickDown/Up`, `OnMouseMove`, the UI pointer click), so a mod's
patches run as they do for a player. For the map it moves the camera over the cells and locks the game's mouse position
(`KInputManager.GetMousePos()`) on the cell, so "the object under the cursor" is right; and it lets the game believe its window has focus
(the game ignores clicks otherwise). It does not move the OS cursor or press keys: code that reads `Input.mousePosition` / `Input.GetKey`
directly sees the idle real input. The class is `Play` because the game has its own global `Sim` and `Player`.

| Call | Does |
|---|---|
| `Play.ClickToolButton(toolName)` | Makes the tool active by clicking its toolbar button (collection button first, like a player); nothing if it is already active |
| `Play.Activate(tool)` / `FindTool("CensusTool")` / `ActiveTool` | Switch tools without the toolbar; look a tool up by type name |
| `Play.ClearTool()` | Leave tool mode like Esc (the toolbar forgets its selection too). Use it for cleanup: `Activate` alone leaves the toolbar "selected", and the next button click would then deselect |
| `Play.Drag(fromCell, toCell)` / `Drag(tool, from, to)` | Move, press at `from`, move, release at `to` with the active (or given) tool |
| `Play.Click(cell)` | Press and release on the cell with the active tool |
| `Play.FindUi(name, root)` / `ClickUi(name, root)` / `ClickUi(go)` | Find active UI objects by exact name (under `root` if given) and left-click one (handed up to the first parent that handles a click). The game's own UI reuses names such as "Row" and "Button": scope to your own screen |
| `Play.FrameCells(cells)` / `WorldPos(cell)` | The camera framing used by Drag/Click; a cell's centre |

A tool, button or cell that cannot be found throws `InvalidOperationException` (status `error`). Not covered: key presses, mouse wheel, hover texts,
drag modifiers (straight-line drag), anything read from `Input` directly.

- `Gravitas.TestHarness/SelfTests.cs` has the harness's own tests (tag `selftest`) and failing demos (tag `manual,demo`).
