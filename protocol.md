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

## Not implemented yet

`POST /invoke`, `POST /tests/run` (M6).
