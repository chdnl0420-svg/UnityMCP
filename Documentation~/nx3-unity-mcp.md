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

The Node MCP server also exposes higher-level PlayMode helpers:

- `unity_enter_play_mode`
- `unity_exit_play_mode`
- `unity_click_ui_text`
- `unity_wait_ui_text`
- `unity_click_ui_text_and_wait`

The PlayMode helpers call the bridge command first, then poll `editor_status`
until the requested `isPlaying` state is observed. `unity_click_ui_text` finds a
visible NGUI label by text and clicks the resolved clickable target, avoiding
manual coordinate transfer from `dump_ui`. `unity_wait_ui_text` polls `dump_ui`
until expected text appears, which makes post-click screen confirmation
deterministic. `unity_click_ui_text_and_wait` combines the click and expected
text wait into one QA step.

NGUI support is reflection-based. Projects without NGUI still compile.
