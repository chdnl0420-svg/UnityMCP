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
Test tools parse Unity Test Framework XML and expose failure counts.
Screenshot tools verify that a PNG exists and has non-zero size.

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
