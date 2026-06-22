# NX3 Unity MCP

NX3 Unity MCP is a Unity Package Manager package that installs a small
Unity Editor command bridge and ships a bundled Node MCP server.

The package is intentionally ProjectM-focused. It avoids the long-lived
WebSocket bridge used by general Unity MCP packages and uses request/response
JSON files under `.codex/unity-commands`.

## Install in Unity

Use Unity Package Manager with `Add package from git URL...`.
The URL must include the `.git` suffix:

```text
https://github.com/chdnl0420-svg/UnityMCP.git
```

You can also edit `Packages/manifest.json` directly:

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

## Tool Success Criteria

`unity_status` must return real JSON data, not just a connection signal.
Editor commands must write response JSON with `success`, `command`,
`elapsedMs`, `logs`, `outputs`, and `error`.
`unity_enter_play_mode` and `unity_exit_play_mode` must wait for
`editor_status` to report the requested `isPlaying` value before returning
success.
`unity_click_ui_text` must resolve a visible NGUI label to its clickable target
instead of requiring manual `dump_ui` coordinate transfer.
`unity_wait_ui_text` must poll `dump_ui` until expected text appears or return a
timed-out response with the last observed UI dump.
`unity_click_ui_text_and_wait` must click a visible label and return the
post-click matched UI text in one response.
`unity_run_ui_text_qa_flow` must enter PlayMode, wait for initial text, capture
before/after screenshots, click text, wait for expected text, and return every
step in one response.
Test tools parse Unity Test Framework XML and expose failure counts.
Screenshot tools verify that a PNG exists and has non-zero size.
Screenshot responses also report requested dimensions, actual dimensions, and
`matchesRequestedSize`; set `requireRequestedSize=true` when dimension mismatch
should fail the tool call.

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
