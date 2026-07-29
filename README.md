# ProjectM QA MCP

ProjectM QA MCP is a Unity Package Manager package that installs a small
Unity Editor command bridge and ships a bundled Node MCP server.

The package is intentionally ProjectM-focused. It avoids the long-lived
WebSocket bridge used by general Unity MCP packages and uses request/response
JSON files under `.codex/unity-commands`.

## Install in Unity

Use Unity Package Manager with a Git URL:

```json
{
  "dependencies": {
    "com.nx3games.unity-mcp": "https://github.com/chdnl0420-svg/UnityMCP.git"
  }
}
```

For local development, add this folder as a local package:

```json
{
  "dependencies": {
    "com.nx3games.unity-mcp": "file:D:/Project/UnityMCP"
  }
}
```

## Codex MCP Registration

Add a server entry to `C:\Users\NX3GAMES\.codex\config.toml`.
Adjust the `args` path to the installed package location.

```toml
[mcp_servers.nx3-unity-mcp]
command = "node"
args = ['D:\Project\UnityMCP\Server~\build\index.js']
startup_timeout_sec = 120

[mcp_servers.nx3-unity-mcp.env]
PROJECTM_UNITY_PATH = 'C:\Program Files\Unity\Hub\Editor\2022.3.76f1\Editor\Unity.exe'
PROJECTM_DEFAULT_PROJECT_PATH = 'C:\Project\CLIENT_KSH_ASIA_L\client\ProjectM'
PROJECTM_COMMAND_ROOT = 'C:\Project\CLIENT_KSH_ASIA_L\client\ProjectM\.codex\unity-commands'
```

## Build and Test

```powershell
cd D:\Project\UnityMCP\Server~
npm install
npm test
npm run build
```

The generated MCP server entrypoint is:

```text
Server~/build/index.js
```

## Editor-tool automation and in-editor tests

Editor tools (`EditorWindow`) cannot be driven by the runtime NGUI commands: those go through
`NguiRaycast` and `UICamera.Notify`, a path an editor window never takes, and IMGUI keeps no retained
widget tree to walk instead.

`unity_editor_*` tools cover that: open a window by type or menu path, dump its instance fields, its
callable methods and its IMGUI layout rects, set fields by dotted/indexed path with a before/after
readback, inject real clicks and keystrokes, run menu items, read the Console, and read or write
EditorPrefs/PlayerPrefs.

`unity_run_tests_in_editor`, `unity_get_test_results` and `unity_list_tests` run Unity Test Framework
tests inside the already-open editor via `TestRunnerApi`. The older CLI test tools spawn a second Unity
in batch mode, which cannot work while an editor holds the project lock.

See `Documentation~/projectm-qa-mcp.md` for the mechanisms, the coordinate-space gotchas, and the
current limitation on window pixel capture.

### Testing hover: `unity_editor_move`

`unity_editor_drag` cannot test hover. Its moves are `MouseDrag` events, and the `MouseDown` that opens
the gesture registers a pressed button, so UI Toolkit delivers them as a `MouseMoveEvent` with
`pressedButtons != 0`. Everything that reacts to a bare cursor — GraphView highlighting the edge under
the mouse, a rollover tint, a hover tooltip — sits on the other branch and never runs.

`unity_editor_move` sends one `MouseMove` with no button held, and nothing else: no `MouseDown`,
`MouseDrag` or `MouseUp`, so it cannot move a node, change the selection, start a marquee, pan the view
or open a context menu. Coordinates follow `unity_editor_drag` — content-local by default with the dock
tab strip added automatically, `coordinateSpace: "host"` for the raw host-view space `unity_editor_click`
uses. Consecutive calls on the same window carry the delta between the two points, and every window
keeps its own last position, so one window's hover path never depends on another's.

```jsonc
// hover the middle of a GraphView, then photograph the result
{ "tool": "unity_editor_move",
  "arguments": { "windowType": "MoveProbeWindow", "x": 300, "y": 160 } }
{ "tool": "unity_editor_window_capture",
  "arguments": { "windowType": "MoveProbeWindow", "outputPath": "C:/tmp/hover.png" } }
```

The response reports what was sent — target window, resolved `x`/`y`, `deltaX`/`deltaY`,
`pressedButtons: 0`, `eventType: MouseMove` and the raw `sendEventReturned`. None of that is proof the
UI reacted, exactly as with click and drag: verify with `unity_editor_window_capture` right after the
move, or by reading the tool's own state with `unity_editor_get_field`.

IMGUI is a special case worth knowing: `OnGUI` only receives `EventType.MouseMove` in a window that set
`EditorWindow.wantsMouseMove`, which is Unity's rule for a real mouse too. `unity_editor_move` turns
that flag on for the send and puts it straight back, and reports both the window's original setting and
whether it did so. Pass `ensureWantsMouseMove: false` for strict production fidelity.

`Window/NX3 MCP/Move Probe` (plus `(Floating)` and `(Docked)`) opens a window built for checking all of
this, and `Tests/Editor/EditorMoveTests.cs` asserts it. Package tests only compile in a project that
lists the package under `testables` in `Packages/manifest.json`:

```json
{ "testables": ["com.nx3games.unity-mcp"] }
```

### Opening a context menu: `unity_editor_context_click`

What `unity_editor_click` with `button: 1` misses is one event, not the button. Measured on 2022.3.62,
Windows, against `MoveProbeWindow`:

| Path | Right `unity_editor_click` | `unity_editor_context_click` |
|---|---|---|
| UI Toolkit `ContextualMenuManipulator` | **already worked** — it listens on `MouseUpEvent` | works |
| GraphView `BuildContextualMenu` | **already worked** — same manipulator path | works |
| IMGUI `GenericMenu` on `EventType.ContextClick` | **never fired** | works |

So the gap is IMGUI: `OnGUI` code builds its menu by testing `Event.current.type` against
`EventType.ContextClick`, and `unity_editor_click` delivers no such event, leaving that branch dead.
Unity's native input layer synthesises `ContextClick` for a real right-click; `EditorWindow.SendEvent`
does not, so the bridge sends it itself — which reaches IMGUI menu code and makes the gesture what a
real right-click is, rather than only what UI Toolkit happens to accept.

`unity_editor_context_click` sends three events in order — `MouseDown` (button 1), `MouseUp` (button 1),
then `ContextClick` at the same point — and reports `mouseDownReturned`, `mouseUpReturned` and
`contextClickReturned` separately. The press pair still goes first because a menu is a gesture, not a
lone event: handlers that track which button is down, dismiss an open popup, or take the menu's anchor
from the press would otherwise see a `ContextClick` arrive out of nowhere.

```jsonc
// right-click a GraphView, then check what opened
{ "tool": "unity_editor_context_click",
  "arguments": { "windowType": "MoveProbeWindow", "targetMode": "element", "elementName": "graph-area" } }
{ "tool": "unity_editor_window_capture",
  "arguments": { "windowType": "MoveProbeWindow", "outputPath": "C:/tmp/menu.png" } }
```

Targeting is the same resolver `unity_editor_click` uses — a point, an `entryIndex`, or `targetMode:
"element"` with the element filters — so a right-click can aim at exactly the pixel a left click just
hit. Coordinates therefore follow **click**, not drag: `x`/`y` are host-view by default, the space
`unity_editor_element_query` reports. Pass `coordinateSpace: "content"` for the content-corner
convention `unity_editor_drag` and `unity_editor_move` default to. There is no `button` or `clickCount`
parameter, because a context click is one right-button gesture by definition.

The three `*Returned` values are raw `SendEvent` returns, not proof a menu opened — the same caveat as
click, drag and move. One case looks identical from the outside and is worth knowing: a menu whose
`BuildContextualMenu` appends **no items** is built and then displays nothing at all.

**A menu that does display blocks the editor until it is dismissed.** Unity shows it from inside
`SendEvent`, so the main thread and this bridge are held for the duration — 4s, 24s and 123s measured
for the same click, differing only in how long the menu stayed up. `contextClickBlockedMs` reports it.
Because nothing else can run meanwhile, the popup cannot be inspected while it is up; confirm the menu
afterwards by reading the tool's own state with `unity_editor_get_field`.

`Window/NX3 MCP/Move Probe` carries a GraphView with a `BuildContextualMenu` override that always
appends an item, named `graph-area`, alongside `hover-area` and `imgui-strip`; `_graphContextMenuCount`
and `_contextMenuCount` are readable with `unity_editor_get_field`.
`Tests/Editor/EditorContextClickTests.cs` asserts the event sequence, and deliberately aims away from
the GraphView so no popup is left on screen mid-run.

### Aiming, scrolling, selecting, compiling

- `unity_editor_element_query` finds UI Toolkit elements by name, USS class, type or text and returns
  where each one is, with `enabled`, `visible` and `pickable`. `unity_editor_click`,
  `unity_editor_move` and `unity_editor_scroll` take the same filters with `targetMode: "element"`, so
  input can aim at "the button named Build" instead of a pixel that a resize invalidates.
- `unity_editor_context_click` opens a context menu, which `unity_editor_click` with `button: 1` never
  did — see below.
- `unity_editor_scroll` turns the wheel at a point: ScrollViews, long inspectors and GraphView zoom.
- `unity_editor_selection_get` / `unity_editor_selection_set` read and set the editor selection, which
  is how an inspector-driven tool is put in front of the asset it should act on.
- `unity_editor_refresh` reimports, recompiles and waits for the verdict; `unity_editor_compile_status`
  reads the last one. `errorCount` is Unity's own compiler output, so this is usable as a gate, and the
  result is stored on disk so it survives the domain reload a recompile causes.
- `unity_editor_wait_for_field` polls a field until it reaches an expected value instead of sleeping a
  fixed amount after triggering slow work.

## Tool Success Criteria

`unity_status` must return real JSON data, not just a connection signal.
Editor commands must write response JSON with `success`, `command`,
`elapsedMs`, `logs`, `outputs`, and `error`.
Test tools parse Unity Test Framework XML and expose failure counts.
Screenshot tools verify that a PNG exists and has non-zero size, and that its pixels are not a single
flat colour — a blank capture is reported as a failure rather than handed back as an image.

## Frame-sequence recording (fast motion)

For fast motion that a single `unity_capture_screenshot` round-trip misses,
record the game view as a PNG frame sequence:

- `unity_start_frame_capture` — starts capturing the game view camera on every
  editor update into a frames folder and returns immediately. Run the fast
  action next. Bounded by `maxFrames` and `maxDurationSeconds` (auto-stops).
- `unity_stop_frame_capture` — stops recording and returns `framesDir`,
  `frameCount`, and `fps`. Read the `frame_NNNNN.png` files in order to inspect
  the motion.

Use recording only when a single screenshot cannot catch the change; a normal
screenshot is cheaper for static checks.

## Recovery Notes

If Unity does not answer a command, inspect:

```text
<ProjectM>/.codex/unity-commands/requests
<ProjectM>/.codex/unity-commands/responses
<ProjectM>/.codex/unity-commands/processed
%LOCALAPPDATA%/Unity/Editor/Editor.log
```

Use `unity_kill_stale` without `kill=true` first. It reports candidates and
reasons before termination is requested.
