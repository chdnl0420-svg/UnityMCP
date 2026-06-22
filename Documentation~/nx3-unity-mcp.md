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

NGUI support is reflection-based. Projects without NGUI still compile.
