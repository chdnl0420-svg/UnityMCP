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
| `editor_window_screenshot` | Legacy PNG capture through the editor framebuffer (docked windows only) |
| `editor_window_capture` | PNG of one window's real pixels, docked or floating |
| `editor_drag` | MouseDown, several MouseDrag steps, MouseUp inside a window |
| `editor_drag_capture` | The same drag, with a PNG after the press, every move and the release |
| `editor_move` | One MouseMove with no button held, for testing hover |
| `editor_scroll` | One ScrollWheel at a point, for ScrollViews, long inspectors and GraphView zoom |
| `editor_element_query` | UI Toolkit elements by name/class/type/text, with where each one is |
| `editor_selection_get` / `editor_selection_set` | Read or set the editor selection |
| `editor_refresh` / `editor_compile_status` | Reimport and recompile, and the compiler's own verdict |
| `editor_get_field` / `editor_set_field` | Read/write by dotted+indexed path, e.g. `_resolutions[0].name` |
| `editor_invoke_method` | Call a method on the window instance (escape hatch) |
| `editor_click` | Inject a click at a point, or at a layout entry index |
| `editor_context_click` | Right-click that opens a context menu: the press pair plus `EventType.ContextClick` |
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

**Use `editor_window_capture`, not `editor_window_screenshot`.** The old command reads the editor
framebuffer through `InternalEditorUtility.ReadScreenPixel`, which only contains the **main editor
window**: a floating window is not in those pixels at all, and even for a docked window the read comes
back a single flat colour when it is issued from the bridge's `EditorApplication.update` turn. It is
kept unchanged for callers that already depend on it, and it still refuses rather than writing a
convincing blank PNG.

`editor_window_capture` goes around the problem by asking Windows for the pixels instead of Unity:

1. It finds the OS window hosting the target's `ContainerWindow` by matching the container's rect
   against the process's top-level windows. Unity does not expose that handle, and matching on the
   title breaks the moment a tool renames its tab, so the match is geometric and its residual is
   reported — a wrong match is visible in the response instead of silently producing a wrong image.
2. That match also *measures* the points-to-pixels scale, which is why 100%, 125%, 150% and 200%
   Windows scaling need no special casing and no DPI setting is read.
3. `PrintWindow(PW_RENDERFULLCONTENT)` then reads that window's own composition surface. It needs no
   focus, does not raise or move the window, and is unaffected by anything stacked on top.
4. The bitmap is cropped to the target window's host rect, minus the dock tab strip unless
   `includeChrome` is set — so a GameView, a SceneView or a neighbouring tab in the same container
   cannot leak in.

Two fallbacks follow when a driver refuses `PrintWindow`: a plain screen read of the same rect
(correct pixels, but anything overlapping shows through — reported as `occluded`), and finally the
legacy framebuffer path for docked windows. Whichever ran comes back as `captureBackend`, alongside
`outputPath`, `pngExists`, `pngBytes`, `imageWidth`, `imageHeight`, `windowRect`, `hostRect`,
`containerRect`, `captureRect`, `pixelsPerPoint`, `measuredScale`, `inMainWindow`, `uniformColor`,
`osWindowHandle`, `osWindowRect`, `mappingBasis` and `backendAttempts`. A capture that produces no
pixels fails with the target window's identity and the reason each backend gave; it never returns an
empty image as a success.

**Dragging needs deltas, not just positions.** `editor_drag` sends MouseDown, then `moveStepCount`
**MouseDrag** events along the path, then MouseUp. The event type matters: Unity only emits MouseMove
when no button is held, so both IMGUI drag handling and UI Toolkit's pressed-button move path key off
MouseDrag. So does the per-step `Event.delta` — IMGUI code reads `Event.current.delta` rather than
recomputing from `mousePosition`, and GraphView's `SelectionDragger` moves a node by exactly that
delta, so a drag built from positions alone moves nothing.

Coordinates are content-local by default: the host view's border — the dock tab strip on a docked
window, nothing on a floating one — is added automatically, so the same numbers work before and after
the user docks the window. Pass `coordinateSpace: "host"` for the raw host-view space that
`editor_click` uses.

`editor_drag_capture` runs the identical gesture and captures the target window right after the
press, after each move and after the release, which is what makes a mid-drag rendering checkable
rather than only the end state. It returns the ordered frame list with each path, byte count, image
size, backend and failure reason, and fails outright if not one frame could be captured.

`Window/NX3 MCP/Capture Probe` opens a window built for exercising all of this: a UI Toolkit GraphView
with two draggable nodes and an edge, plus an IMGUI strip that draws and counts the raw events it
receives. Every observable is also a plain instance field, so `editor_get_field` can confirm the same
facts the PNGs are supposed to show.

### Hover needs a move with no button: `editor_move`

A drag cannot stand in for a hover. UI Toolkit has no MouseDragEvent — a drag arrives as a
`MouseMoveEvent` whose `pressedButtons` is non-zero, because the `MouseDown` that opened the gesture
registered the press in `PointerDeviceState`. So a tool that only reacts to a bare cursor (GraphView
highlighting the edge under the mouse, a rollover tint, a hover tooltip) sits on the branch a drag
never takes, and was untestable over MCP.

`editor_move` sends exactly one event: `EventType.MouseMove`, no button, carrying the delta from
wherever the pointer was last left in that window. Because no `MouseDown` is ever sent, nothing
registers a pressed button, so the receiving side sees `MouseMoveEvent` with `pressedButtons == 0`, and
IMGUI sees `EventType.MouseMove`. It follows that the command cannot move a node, click-select, start a
marquee, pan or open a context menu: every one of those needs a press.

| Parameter | Meaning |
|---|---|
| `windowType` / `windowTitle` / `instanceId` | Target window, resolved exactly as `editor_click`, `editor_drag` and `editor_window_capture` resolve it |
| `x`, `y` | Pointer position |
| `coordinateSpace` | `content` (default) or `host`, identical to `editor_drag` |
| `modifiers` | `shift`, `control`, `alt`, `command`, comma separated, same as the other input commands |

The response carries `targetWindowType`, `targetWindowTitle`, `targetInstanceId`, the resolved `x`/`y`
with `contentX`/`contentY` and `contentOffset`, `deltaX`/`deltaY` with `previousX`/`previousY` and
`hadPreviousPoint`, `eventType` (`MouseMove`), `eventsSent`, `pressedButtons` (`0`), `events` and
`sendEventReturned`.

`sendEventReturned` is the raw return of `EditorWindow.SendEvent`. It is **not** a guarantee that the UI
reacted — the same caveat as `editor_click` and `editor_drag`. Verify a hover by capturing the window
right after the move with `editor_window_capture`, or by reading the tool's own state with
`editor_get_field`.

Two states are kept per window, not globally: the last pointer position, so consecutive calls carry a
real delta (the first call into a window carries `0,0`, since it has nowhere to come from), and nothing
else — moving over window A cannot shift window B's pointer path. Positions for closed windows are
dropped on the next call.

An unresolved window fails with the same message the other commands produce, listing the open window
types. A point outside the window fails too, rather than reporting a successful move that hovered
nothing: the error gives the content coordinates, the window's size and the border that content space
added.

**IMGUI only sees MouseMove in a window that asked for it.** `EditorWindow.wantsMouseMove` is Unity's
own gate, for a real mouse as much as for an injected event: with it off, `OnGUI` never receives
`EventType.MouseMove` no matter where the cursor is. So `editor_move` turns the flag on for the send
and puts it straight back — the window keeps the setting it had, and the response reports both the
original `wantsMouseMove` and whether it was `wantsMouseMoveEnabledForSend`. Pass
`ensureWantsMouseMove: false` to send exactly what production would see. UI Toolkit does not use the
flag and is unaffected either way. (Measured: with the flag off the IMGUI counter stays at 0 and the
UI Toolkit counter still rises; with it handled, both rise.)

`Window/NX3 MCP/Move Probe` (with `(Floating)` and `(Docked)` variants) is the target built for
checking this. A UI Toolkit element records every `MouseMoveEvent` — count, `mousePosition`,
`mouseDelta`, `pressedButtons` and the event type name — plus any `MouseDownEvent` or `MouseUpEvent`
that should never arrive; an IMGUI strip counts the raw `EventType`s; and a GraphView node exposes the
position, selection and pan that a move must leave untouched. `Tests/Editor/EditorMoveTests.cs` asserts
all of it, docked and floating, and that `editor_window_capture` still works right after a move. Package
tests need the project to list the package under `testables` in `Packages/manifest.json`.

### A context menu needs a third event: `editor_context_click`

`editor_click` with `button: 1` delivers the button faithfully — a `MouseDown`/`MouseUp` pair a window
records as a right-button press. What it never delivers is `EventType.ContextClick`. Measured on
2022.3.62, Windows, against `MoveProbeWindow`:

| Path | Right `editor_click` | `editor_context_click` |
|---|---|---|
| UI Toolkit `ContextualMenuManipulator` | **already worked** | works |
| GraphView `BuildContextualMenu` | **already worked** | works |
| IMGUI `GenericMenu` on `EventType.ContextClick` | **never fired** | works |

The two UI Toolkit rows are the surprise, and they are worth stating plainly because the obvious story
says otherwise: on Windows a `ContextualMenuManipulator` listens on `MouseUpEvent`, so the pair
`editor_click` already sent is enough to run a GraphView's `BuildContextualMenu` and display its menu.
If a GraphView tool is not showing a menu on a right `editor_click`, the cause is more likely the click
missing its target — `editor_click` reads x/y as **host-view** coordinates, so a content-space number on
a docked window lands high by the tab strip — or a `BuildContextualMenu` that appends no items for that
target, since Unity displays nothing for an empty menu.

The real gap is IMGUI. `OnGUI` code builds a `GenericMenu` by testing
`Event.current.type == EventType.ContextClick`, and no such event ever arrived, so that branch was dead.
Unity's native input layer synthesises it for a real right-click — which is why the same click sent with
Win32 `mouse_event` behaves differently — and `EditorWindow.SendEvent` performs no such synthesis.
`editor_context_click` sends it, which reaches IMGUI menu code and makes the injected gesture what a
real right-click is rather than only what UI Toolkit happens to accept.

Three events go out, in this order:

| # | Event | Why it is there |
|---|---|---|
| 1 | `MouseDown`, button 1 | A menu is a gesture. Handlers that track which button is down, dismiss an already-open popup, or take the menu's anchor from the press need to see it start |
| 2 | `MouseUp`, button 1 | Closes the press, and is itself the trigger for a `ContextualMenuManipulator` on some paths |
| 3 | `ContextClick` | The event that actually opens the menu, carrying the same `mousePosition` — which is also where the menu is placed |

| Parameter | Meaning |
|---|---|
| `windowType` / `windowTitle` / `instanceId` | Target window, resolved exactly as `editor_click` resolves it |
| `targetMode` | `point` (default), `entry` with `entryIndex`, or `element` with the element filters |
| `x`, `y` | Point to right-click |
| `coordinateSpace` | `host` (default, as for clicks) or `content` — see below |
| `elementName` / `elementClass` / `elementType` / `elementText` / `elementIndex` | Element filters, identical to `editor_element_query` |
| `modifiers` | `shift`, `control`, `alt`, `command`, comma separated |

There is no `button` and no `clickCount`: a context click is one right-button gesture by definition, and
any other button would produce a click that opens nothing.

Targeting runs through the **same resolver `editor_click` uses**, so an element, an `entryIndex` or an
x/y means the same pixel in both commands — a right-click can aim at exactly what a left click just hit,
and neither command can drift from the other as one of them changes. That also fixes the coordinate
convention to click's: **`x`/`y` are host-view by default**, the space `editor_element_query` reports,
*not* the content-corner default that `editor_drag`, `editor_move` and `editor_scroll` use. Pass
`coordinateSpace: "content"` for that convention, and the dock tab strip is added the way it is there.

The response carries `targetWindowType`/`targetWindowTitle`/`targetInstanceId`, the resolved `x`/`y`
with whatever the target mode reported (`resolvedFrom`, plus `entryRect`/`containerOffset` or
`elementName`/`elementRect`/`elementMatchCount`, or `coordinateSpace`/`contentOffset`), `button` (`1`),
`clickCount` (`1`), `modifiers`, `eventsSent` (`3`), `eventOrder`, and the three raw returns
`mouseDownReturned`, `mouseUpReturned` and `contextClickReturned`.

Those returns are raw `EditorWindow.SendEvent` values, and are **not** proof a menu opened — the same
caveat as `editor_click`, `editor_drag` and `editor_move`. Confirm with `editor_window_capture`, or with
`editor_element_query` against the popup's own elements. One case is worth knowing because it is
indistinguishable from failure at this level: a menu whose `BuildContextualMenu` appends **no items** is
built and then displays nothing at all, so a right-click on a GraphView's empty canvas can legitimately
produce no visible menu.

`Window/NX3 MCP/Move Probe` is the target built for checking this. Its GraphView (`graph-area`) carries
a `BuildContextualMenu` override that always appends an item, so it opens a real menu;
`_graphContextMenuCount` and `_graphContextMenuItems` record that the override ran and with what,
`_contextClickCount` and `_contextMenuCount` record the `ContextClickEvent` and the
`ContextualMenuPopulateEvent` on the root, and `_imguiContextClickCount` records the raw
`EventType.ContextClick` on the IMGUI side — all readable with `editor_get_field`.

`Tests/Editor/EditorContextClickTests.cs` asserts the sequence, the IMGUI before/after that is the
command's actual justification, that `editor_click` with button 1 still produces no `ContextClick`, and
that both commands resolve the same element to the same pixel. It aims at `hover-area`, `menu-area` and
`imgui-strip` rather than at `graph-area`, because those build an empty menu that Unity never displays —
no popup is left on screen, and nothing blocks.

**A menu that displays blocks the editor.** Unity shows it from inside `SendEvent`, so the main thread —
and this bridge with it — is held until the menu is dismissed. The same click on the probe's GraphView
was measured at 4s, 24s and 123s, differing only in how long the menu stayed up; unattended, nothing
dismisses it. `contextClickBlockedMs` reports the wait. The practical consequences: no other bridge
command can run while a menu is up, so the popup **cannot** be inspected with `editor_element_query` or
`editor_window_capture` while it is open — confirm instead by reading the tool's own state afterwards
with `editor_get_field`, which is what the probe's `_graphContextMenuCount` and `_graphContextMenuItems`
are for.

### Aiming at elements instead of pixels: `editor_element_query`

A hard-coded coordinate breaks the moment a window is resized or a field is added above the target.
`editor_element_query` finds UI Toolkit elements by `elementName`, `elementClass`, `elementType` or
`elementText` (every filter given must match; text is a case-insensitive substring) and reports each
hit's `type`, `name`, `classes`, `text`, rect, `centerX`/`centerY`, `enabled`, `visible` and
`pickable`. Those coordinates are host-view space, so they go straight into `editor_click`,
`editor_context_click`, `editor_move` or `editor_scroll` — the click commands read x/y that way by
default, the other two need `coordinateSpace: "host"`.

Or skip the copying: those four commands accept `targetMode: "element"` with the same filters and aim
at the matched element's centre themselves. No match, several matches with an out-of-range
`elementIndex`, or an element with no size each fail with what was asked for and what was there —
never as a click into empty space that silently did nothing.

`editor_scroll` sends one `EventType.ScrollWheel` at that point, with `scrollX`/`scrollY` as the
delta. One notch is about 3 and positive y scrolls down, which is Unity's own convention, deliberately
not renormalised. A zero delta is rejected rather than sent as a no-op.

### The compile gate: `editor_refresh` and `editor_compile_status`

`editor_refresh` reimports and, with `forceRecompile`, requests a script compilation;
`editor_compile_status` reports what the compiler said. `errorCount` comes from Unity's own
`CompilerMessages`, so zero errors means the assemblies really built — each message carries `type`,
`assembly`, `file`, `line` and `column`.

Three details make this work where a naive version would not:

1. **The result outlives the process.** A recompile ends in a domain reload that wipes every static, so
   the state is written to `.codex/unity-commands/compile/state.json` as each assembly finishes, not
   accumulated in memory. A reload landing on a state file that still says `compiling` repairs it.
2. **The refresh is deferred by one editor tick**, because the reload can otherwise kill the request
   before its own response is written. The deferral runs on `EditorApplication.update` — the same tick
   the command bridge runs on — rather than `delayCall`, which was observed not to fire while the
   editor was unfocused with a modal window up.
3. **`assetPaths` forces a targeted reimport.** A plain refresh does not notice an edit inside an
   installed package; naming the file does.

An unfocused editor can defer compiling until it regains focus. That is Unity's behaviour, not the
command's, and the status says which state it is really in rather than claiming success.

The Node side wraps this as `unity_editor_refresh`, which polls until compilation settles (treating a
failed poll during the reload as "still busy") and reports success only when the compiler agreed.
`unity_editor_wait_for_field` polls one field until it reaches an expected value, which replaces a
fixed sleep after triggering slow work; both wait on this side, so the editor is never blocked.

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
