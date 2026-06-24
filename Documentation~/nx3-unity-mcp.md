# NX3 Unity MCP Design

The bridge uses files instead of a long-lived socket.

```text
MCP client
  -> Node MCP server
  -> .codex/unity-commands/requests/<id>.json
  -> Unity Editor bridge
  -> .codex/unity-commands/responses/<id>.json
```

Each response contains:

```json
{
  "success": true,
  "command": "ping",
  "elapsedMs": 12,
  "logs": [],
  "outputs": [],
  "error": null
}
```

Supported MVP commands:

- `ping`
- `editor_status`
- `capture_screenshot`
- `capture_game_view`
- `open_scene`
- `load_prefab`
- `find_ngui_object`
- `click_ngui_object`
- `click_at`
- `click_ui_text`
- `enter_play_mode`
- `exit_play_mode`
- `dump_ui`
- `batch`
- `resolve_packages`

Code/compile commands (added in bridge protocol v2-v4):

- `refresh_assets` — `AssetDatabase.Refresh`, reimports and recompiles
- `recompile_scripts` — `CompilationPipeline.RequestScriptCompilation` (Edit mode only)
- `compile_status` — `isCompiling`/`isUpdating` plus buffered compile errors

QA inspection commands:

- `get_console_logs` — reads the Editor console (filter by `logType`, cap with `maxCount`)
- `clear_console` — clears the Editor console
- `inspect_object` — components, active state, transform, NGUI label/sprite/input, collider bounds
- `find_objects` — name-substring search returning paths + active/clickable/label hints
- `scene_info` — active scene, loaded scenes, active-scene root objects
- `get_hierarchy` — depth-limited GameObject tree under a path (or active-scene roots)

UI mutation commands:

- `set_active` — `GameObject.SetActive` (`value` = `true`/`false`)
- `set_label_text` — set NGUI `UILabel.text`
- `set_input_text` — set NGUI `UIInput.value`
- `set_sprite` — set NGUI `UISprite.spriteName`

Compile errors survive the recompile domain reload by being buffered to
`<commandRoot>/compile-errors.json` (cleared on `compilationStarted`, appended on
`assemblyCompilationFinished`). `editor_status`/`ping` report a `bridgeVersion`
sentinel so a re-resolved package can be verified as actually loaded.

The Node MCP server also exposes higher-level PlayMode helpers:

- `unity_enter_play_mode`
- `unity_exit_play_mode`
- `unity_click_ui_text`
- `unity_wait_ui_text`
- `unity_click_ui_text_and_wait`
- `unity_run_ui_text_qa_flow`

And typed wrappers for the commands above:

- `unity_recompile` — triggers recompile/refresh, waits for the domain reload to
  settle (tolerating the mid-reload bridge outage), and returns compile errors
- `unity_compile_status`, `unity_get_console_logs`, `unity_clear_console`
- `unity_inspect_object`, `unity_find_objects`, `unity_scene_info`, `unity_get_hierarchy`
- `unity_set_active`, `unity_set_label_text`, `unity_set_input_text`, `unity_set_sprite`

The PlayMode helpers call the bridge command first, then poll `editor_status`
until the requested `isPlaying` state is observed. `unity_click_ui_text` finds a
visible NGUI label by text and clicks the resolved clickable target, avoiding
manual coordinate transfer from `dump_ui`. `unity_wait_ui_text` polls `dump_ui`
until expected text appears, which makes post-click screen confirmation
deterministic. `unity_click_ui_text_and_wait` combines the click and expected
text wait into one QA step.
Labels that have no direct clickable object can resolve to a nearby clickable
target; those rows and click responses include `clickResolution=nearest`.
`dump_ui` filters out off-screen entries by default and reports how many were
omitted. Send `includeOffscreen=true` to keep the older full dump behavior.
`unity_run_ui_text_qa_flow` wraps the common Splash QA path into one tool call:
enter PlayMode, wait for initial text, capture before, click text, wait for
expected text, capture after, then optionally exit PlayMode. The response keeps
per-step success and elapsed time so a single failed phase is visible.
Screenshot helpers include requested versus actual dimensions and can fail on a
dimension mismatch when `requireRequestedSize` is set.

NGUI support is reflection-based. Projects without NGUI still compile.
