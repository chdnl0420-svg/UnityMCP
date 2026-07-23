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
