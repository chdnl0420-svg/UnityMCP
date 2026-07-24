# ProjectM QA MCP Design

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

`outputs` is a list of `{key, value}` string pairs, because Unity's `JsonUtility` has no dictionary
support. Structured payloads (window lists, field dumps, layout rects, test results) travel as JSON
text inside a value; the Node server parses those back into real objects before returning them.

For the same reason every command parameter must exist as a public field on `CommandParameters` —
there is no free-form parameter bag.

## Runtime commands (game / NGUI)

- `ping`, `editor_status` — also report `bridgeVersion`, play/compile state and the full command list
- `capture_screenshot`, `capture_game_view`
- `start_frame_capture`, `stop_frame_capture`
- `open_scene`, `load_prefab`
- `find_ngui_object`, `click_ngui_object`

NGUI support is reflection-based. Projects without NGUI still compile.

## Editor-tool commands (EditorWindow)

Editor tools cannot be reached through the runtime commands above. Those go through `NguiRaycast` and
`UICamera.Notify`, a path an `EditorWindow` never takes, and IMGUI is immediate-mode, so there is no
retained widget tree to walk instead.

`EditorToolBridge` closes that gap with three mechanisms, in order of reliability:

1. **Reflection over the window instance.** In-house editor tools keep their state in instance fields
   that `OnGUI` reads and writes. Dumping and setting those fields drives most tools, and the
   before/after readback is direct evidence the change landed.
2. **Real IMGUI event injection.** `EditorWindow.SendEvent` with a MouseDown/MouseUp pair makes
   `GUILayout.Button` genuinely fire, so the tool's own callback runs instead of being bypassed.
3. **Layout-rect recovery.** The `IMGUIContainer` that ran `OnGUI` keeps a layout cache whose entries
   survive the pass, each with its rect and `GUIStyle`. That turns a click from "guess a coordinate"
   into "click entry 41, the button-styled rect 30 pixels tall".

Commands:

| Command | Purpose |
|---|---|
| `editor_window_list` | Open windows: type, title, instance id, focus, screen rect |
| `editor_window_open` | Open by type name or by menu path |
| `editor_window_close` / `editor_window_focus` | Close, or focus and refresh layout |
| `editor_window_dump` | Fields with values, invokable methods, IMGUI layout rects, UIElements tree |
| `editor_window_screenshot` | PNG of the window as it appears on screen |
| `editor_get_field` / `editor_set_field` | Read/write by dotted+indexed path, e.g. `_resolutions[0].name` |
| `editor_invoke_method` | Call a method on the window instance (escape hatch) |
| `editor_click` | Inject a click at a point, or at a layout entry index |
| `editor_key` | Type text, or press a named key |
| `editor_menu_execute` / `editor_menu_list` | Run a `[MenuItem]`, or discover its exact path |
| `editor_console_read` / `editor_console_clear` | Console entries and error/warning counts |
| `editor_prefs_get` / `editor_prefs_set` | EditorPrefs and PlayerPrefs |
| `editor_play_mode` / `exit_play_mode` | Read or change play mode |

### Three gotchas worth knowing

**Pick the right IMGUIContainer.** A docked window's host view owns several — the tab strip and the
rest of the dock chrome each draw through their own. The first one found depth-first is usually not
the window's, and its layout cache looks nearly empty. The bridge measures every candidate and takes
the one with the most entries.

**Mind the coordinate spaces for clicks.** Layout rects are local to the container, while `SendEvent`
delivers into the host view's space, which also contains the tab strip. Clicks must be offset by the
container's `worldBound`, or they land high by the height of the tab strip.

**Screenshots are a known open issue.** `editor_window_screenshot` is wired up but does not reliably
return pixels, so treat it as unverified and read window state with `editor_window_dump` instead.

What is established: `ReadScreenPixel` reads bottom-left **physical pixels** while the editor reports
rects top-left in **points**, so the rect is flipped and scaled by `EditorGUIUtility.pixelsPerPoint`
(1.25 on a 1920x1080 display at 125% Windows scaling). It reads the **main editor window's**
framebuffer, not the desktop — a window in its own floating container is not in those pixels at all,
and the command refuses rather than writing a blank PNG. Beyond that, calls made from the bridge's
`EditorApplication.update` turn come back a single flat colour even for a docked window with the
editor in the foreground, which points at needing a real GUI repaint context; deferring the capture
into one (the way test runs defer to callbacks) is the likely fix and is not implemented.

Because a blank PNG looks exactly like a real one, the command reports `uniformColor`, logs a warning,
and the MCP tool returns `success: false` when the capture is flat. Every input to the origin
calculation comes back too (`screenResolution`, `pixelsPerPoint`, `containerRect`, `mainWindowRect`,
`windowRect`, `hostRect`, `inMainWindow`, `screenOrigin`), and `originX`/`originY` override the
computed origin, so the next attempt can be driven from numbers rather than guesswork.

## Test-runner commands

| Command | Purpose |
|---|---|
| `list_tests` | Tests known to the editor for a mode |
| `run_tests` | Start an EditMode/PlayMode run, returns a `runId` immediately |
| `get_test_results` | Status, pass/fail/skip counts, per-test outcomes with failure messages |

These use `TestRunnerApi` inside the already-open editor. The CLI test tools in the Node server spawn
a second Unity in batch mode, which cannot work while a normal editor holds the project lock — that
is, during exactly the sessions MCP work happens in.

A run outlives the request that started it, and a PlayMode run crosses a domain reload, so results are
written to `.codex/unity-commands/test-runs/<runId>.json` rather than kept in memory, and callbacks are
re-registered on every load via `[InitializeOnLoad]`.

The code is guarded by `PROJECTM_TEST_FRAMEWORK`, set from the asmdef's `versionDefines` when
`com.unity.test-framework` is present. The package declares that dependency, so it normally is.

## Verifying which build is loaded

`manifest.json` can name one commit while UPM is still running an older checkout — `packages-lock.json`
holds the hash UPM actually resolves. So do not treat the manifest as proof. Check `bridgeVersion` in
the `ping` response instead: it comes from the code that is really loaded.

`bridgeVersion` is a constant in `EditorToolBridge`, deliberately separate from `package.json` so it can
be bumped mid-session to prove a recompile actually landed. Keep the two in step when releasing, or the
version a caller reads will not match the version the package claims.
