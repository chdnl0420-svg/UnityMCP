import { join } from 'node:path';
import { z } from 'zod';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { resolveProjectConfig } from './config.js';
import { executeEditorCommand, EditorCommandResponse } from './commandBridge.js';
import { fileSize, pathExists } from './utils/files.js';

/**
 * MCP tools for driving Unity *editor* tools (EditorWindow) and the in-editor test runner.
 *
 * The runtime tools elsewhere in this server target the game's NGUI stack, which editor windows never
 * go through. These wrap the editor-side bridge commands instead.
 */

const baseConfigShape = {
  unityPath: z.string().optional(),
  projectPath: z.string().optional(),
  commandRoot: z.string().optional(),
};

const windowShape = {
  windowType: z.string().optional().describe('EditorWindow type name, e.g. "UILayoutCheckerWindow" or a full namespace-qualified name.'),
  windowTitle: z.string().optional().describe('Window tab title, used when the type name is unknown.'),
  instanceId: z.number().int().optional().describe('Exact window instance id from unity_editor_window_list.'),
};

const timeoutSchema = z.number().int().positive().max(60 * 60 * 1000).optional();

const commonShape = {
  ...baseConfigShape,
  timeoutMs: timeoutSchema,
  runOnce: z.boolean().optional(),
};

/** Output keys the bridge packs as JSON text, so they come back as real structures instead of strings. */
const JSON_OUTPUT_KEYS = new Set([
  'windows', 'window', 'fields', 'methods', 'layout', 'visualTree',
  'menuItems', 'entries', 'tests', 'result', 'entryRect', 'focusedWindow',
  'events', 'frames',
]);

export function registerEditorTools(server: McpServer): void {
  // ---------------------------------------------------------------- windows

  server.tool('unity_editor_window_list',
    'Lists every open Unity EditorWindow with its type, title, instance id, focus state and screen rect. Start here when you do not know a tool window\'s exact type name.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_window_list', {})));

  server.tool('unity_editor_window_open',
    'Opens a Unity editor tool window, either by EditorWindow type name or by running its menu item, and returns the opened window. Prefer menuPath when the tool does setup work in its menu handler.',
    {
      ...commonShape,
      ...windowShape,
      menuPath: z.string().optional().describe('Menu item that opens the window, e.g. "Tools/UI Layout Checker".'),
      utility: z.boolean().optional().describe('Open as a floating utility window instead of a dockable one.'),
      noFocus: z.boolean().optional().describe('Open without stealing focus.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_window_open', {
      windowType: params.windowType,
      windowTitle: params.windowTitle,
      menuPath: params.menuPath,
      utility: params.utility ?? false,
      noFocus: params.noFocus ?? false,
    })));

  server.tool('unity_editor_window_close',
    'Closes an open Unity editor tool window.',
    { ...commonShape, ...windowShape },
    async (params) => toToolResult(await runBridge(params, 'editor_window_close', {})));

  server.tool('unity_editor_window_focus',
    'Focuses an editor tool window and forces a repaint, which also refreshes its IMGUI layout rects.',
    { ...commonShape, ...windowShape },
    async (params) => toToolResult(await runBridge(params, 'editor_window_focus', {})));

  server.tool('unity_editor_window_dump',
    'Dumps everything needed to drive an editor tool window: its instance fields with current values (this is where in-house tools keep their state), its callable methods, its IMGUI layout rects with style names for clicking, and its UIElements tree. Call this before set_field or click.',
    {
      ...commonShape,
      ...windowShape,
      maxDepth: z.number().int().min(0).max(4).optional().describe('How deep to expand nested field values (default 1).'),
      publicOnly: z.boolean().optional().describe('Skip private fields. Off by default, because tool state is usually private.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_window_dump', {
      maxDepth: params.maxDepth ?? 1,
      publicOnly: params.publicOnly ?? false,
    })));

  server.tool('unity_editor_window_screenshot',
    'Captures a PNG of an editor tool window as it appears on screen, and verifies the file is non-empty. The window must be visible and not minimized.',
    {
      ...commonShape,
      ...windowShape,
      outputPath: z.string().optional(),
      noFlipY: z.boolean().optional().describe('Skip the bottom-left screen-space y flip. Try this if the capture looks vertically offset.'),
      originX: z.number().optional().describe('Override the capture origin x, in points. For diagnosing a misplaced capture.'),
      originY: z.number().optional().describe('Override the capture origin y, in bottom-left points. For diagnosing a misplaced capture.'),
    },
    async (params) => {
      const config = resolveProjectConfig(params);
      const outputPath = params.outputPath
        || join(config.commandRoot, 'screenshots', `editor-window-${Date.now()}.png`);
      const result = await runBridge(params, 'editor_window_screenshot', {
        outputPath,
        noFlipY: params.noFlipY ?? false,
        originX: params.originX ?? 0,
        originY: params.originY ?? 0,
      });
      const bytes = await fileSize(outputPath);
      // A flat single-colour capture is a failure even though a PNG was written: reporting it as
      // success would hand back a blank image that looks like a real screenshot.
      const blank = result.outputs?.uniformColor === 'true';
      return toToolResult({
        ...result,
        success: result.success && bytes > 0 && !blank,
        note: blank
          ? 'Capture came back a single flat colour. ReadScreenPixel reads the editor framebuffer, so this happens when the window is not actually rendered on screen. Read window state with unity_editor_window_dump instead.'
          : undefined,
        outputs: { ...result.outputs, outputPath, pngExists: await pathExists(outputPath), pngBytes: bytes },
      });
    });

  server.tool('unity_editor_window_capture',
    'Captures the real on-screen pixels of one editor window, whether it is docked or floating, and writes them to a PNG. Unlike unity_editor_window_screenshot this reads the operating system window that hosts the target, so a floating window works and no GameView, SceneView or neighbouring tab can leak into the image. The window is not focused, moved, resized or re-docked. A capture that comes back empty is reported as a failure with the reason, never as a blank success.',
    {
      ...commonShape,
      ...windowShape,
      outputPath: z.string().optional().describe('Defaults to <commandRoot>/screenshots/editor-window-capture-<timestamp>.png.'),
      includeChrome: z.boolean().optional().describe('Include the dock tab strip around the window. Off by default, so only the window\'s own content is captured.'),
      captureBackend: z.enum(['auto', 'printwindow', 'screen', 'framebuffer']).optional()
        .describe('Force one backend instead of trying them in order. For diagnosing a bad capture.'),
      captureSettleMs: z.number().int().min(0).max(2000).optional().describe('Wait this long after the repaint before reading pixels (default 24).'),
      allowUniform: z.boolean().optional().describe('Accept a single-colour image instead of treating it as no pixels. Use for a legitimately blank window.'),
    },
    async (params) => {
      const config = resolveProjectConfig(params);
      const outputPath = params.outputPath
        || join(config.commandRoot, 'screenshots', `editor-window-capture-${Date.now()}.png`);
      const result = await runBridge(params, 'editor_window_capture', {
        outputPath,
        includeChrome: params.includeChrome ?? false,
        captureBackend: params.captureBackend ?? 'auto',
        captureSettleMs: params.captureSettleMs ?? 0,
        allowUniform: params.allowUniform ?? false,
      });

      const bytes = await fileSize(outputPath);
      const exists = await pathExists(outputPath);
      return toToolResult({
        ...result,
        success: result.success && exists && bytes > 0,
        outputs: { ...result.outputs, outputPath, pngExists: exists, pngBytes: bytes },
      });
    });

  // ---------------------------------------------------------------- drag

  const dragShape = {
    fromX: z.number().describe('Drag start x, in window-local coordinates.'),
    fromY: z.number().describe('Drag start y, in window-local coordinates.'),
    toX: z.number().describe('Drag end x.'),
    toY: z.number().describe('Drag end y.'),
    durationMs: z.number().int().min(0).max(60000).optional().describe('Total gesture time, spread over the moves (default 240).'),
    moveStepCount: z.number().int().min(1).max(240).optional().describe('How many MouseDrag events to send between press and release (default 12).'),
    coordinateSpace: z.enum(['content', 'host']).optional()
      .describe('"content" (default) treats 0,0 as the window\'s content corner and adds the dock tab strip offset automatically; "host" sends raw host-view coordinates, matching unity_editor_click.'),
    button: z.number().int().min(0).max(2).optional(),
    modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    noFocus: z.boolean().optional().describe('Do not focus the window first. Most drags need focus, so this is off by default.'),
  };

  server.tool('unity_editor_drag',
    'Drags the mouse inside an editor window: one MouseDown, several MouseDrag events along the path, then MouseUp. The moves carry a real per-step delta, which is what makes UI Toolkit GraphView node dragging and IMGUI drag handling respond instead of seeing a click. Coordinates are window-local and the dock tab strip offset is added automatically. Returns every coordinate actually sent.',
    { ...commonShape, ...windowShape, ...dragShape },
    async (params) => toToolResult(await runBridge(params, 'editor_drag', dragParameters(params))));

  server.tool('unity_editor_drag_capture',
    'Runs the same drag as unity_editor_drag and saves a PNG of the target window right after the MouseDown, after each MouseDrag and after the MouseUp, so a mid-gesture rendering can be checked rather than only the end state. Works for floating and docked windows. Returns the ordered frame list with each path, size and failure reason.',
    {
      ...commonShape,
      ...windowShape,
      ...dragShape,
      framesDir: z.string().optional().describe('Directory for the frames. Defaults to <commandRoot>/screenshots/drag-<timestamp>.'),
      captureEveryMove: z.boolean().optional().describe('Capture after every MouseDrag (default true). When false only the press and release frames are written.'),
      includeChrome: z.boolean().optional(),
      captureBackend: z.enum(['auto', 'printwindow', 'screen', 'framebuffer']).optional(),
      captureSettleMs: z.number().int().min(0).max(2000).optional(),
      allowUniform: z.boolean().optional(),
    },
    async (params) => {
      const config = resolveProjectConfig(params);
      const framesDir = params.framesDir
        || join(config.commandRoot, 'screenshots', `drag-${Date.now()}`);
      const result = await runBridge(params, 'editor_drag_capture', {
        ...dragParameters(params),
        framesDir,
        captureEveryMove: (params.captureEveryMove ?? true) ? 'true' : 'false',
        includeChrome: params.includeChrome ?? false,
        captureBackend: params.captureBackend ?? 'auto',
        captureSettleMs: params.captureSettleMs ?? 0,
        allowUniform: params.allowUniform ?? false,
      });

      // Re-check the frames from this side: the bridge reports what it believes it wrote, and a caller
      // reading these paths cares whether the files are actually there.
      const frames = Array.isArray(result.outputs?.frames) ? result.outputs.frames : [];
      const verified = await Promise.all(frames.map(async (frame: any) => ({
        ...frame,
        pngExists: await pathExists(frame?.path ?? ''),
        pngBytes: await fileSize(frame?.path ?? ''),
      })));

      const written = verified.filter((frame) => frame.pngExists && frame.pngBytes > 0).length;
      return toToolResult({
        ...result,
        success: result.success && written > 0,
        outputs: { ...result.outputs, framesDir, frames: verified, framesOnDisk: written },
      });
    });

  // ---------------------------------------------------------------- state

  server.tool('unity_editor_get_field',
    'Reads one field or property on an editor tool window by path, e.g. "_inputName" or "_resolutions[0].width".',
    {
      ...commonShape,
      ...windowShape,
      fieldPath: z.string().describe('Dotted/indexed path, e.g. "_resolutions[2].scale".'),
      maxDepth: z.number().int().min(0).max(4).optional(),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_get_field', {
      fieldPath: params.fieldPath,
      maxDepth: params.maxDepth ?? 2,
    })));

  server.tool('unity_editor_set_field',
    'Sets a field or property on an editor tool window and returns its before/after values as evidence the write landed. This is the most reliable way to drive an IMGUI tool, because tools read their state from these fields during OnGUI. Values are given as text and converted to the target type (numbers, bools, enums, Vector2/3/4, Color, or JSON for plain objects).',
    {
      ...commonShape,
      ...windowShape,
      fieldPath: z.string().describe('Dotted/indexed path, e.g. "_inputW" or "_resolutions[0].name".'),
      fieldValue: z.string().describe('New value as text. Enums accept the name or the number.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_set_field', {
      fieldPath: params.fieldPath,
      fieldValue: params.fieldValue,
    })));

  server.tool('unity_editor_invoke_method',
    'Invokes a method on an editor tool window instance. Use this only when a behaviour cannot be reached by clicking, since it bypasses the tool\'s own GUI path.',
    {
      ...commonShape,
      ...windowShape,
      methodName: z.string(),
      methodArgs: z.array(z.string()).optional().describe('Arguments as text, converted to the parameter types.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_invoke_method', {
      methodName: params.methodName,
      // Unit Separator keeps arguments intact when one of them contains a comma.
      methodArgs: (params.methodArgs ?? []).join('\u001F'),
    })));

  // ---------------------------------------------------------------- input

  server.tool('unity_editor_click',
    'Clicks inside an editor tool window by injecting a real IMGUI MouseDown/MouseUp pair, so the tool\'s own button callback runs. Target either a point (x, y in window-local coordinates) or a layout entry index from unity_editor_window_dump.',
    {
      ...commonShape,
      ...windowShape,
      targetMode: z.enum(['point', 'entry']).optional().describe('"point" (default) uses x/y; "entry" uses entryIndex from the dump\'s layout array.'),
      x: z.number().optional(),
      y: z.number().optional(),
      entryIndex: z.number().int().min(0).optional().describe('Index "i" of a layout entry from unity_editor_window_dump.'),
      button: z.number().int().min(0).max(2).optional(),
      clickCount: z.number().int().min(1).max(3).optional(),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_click', {
      targetMode: params.targetMode ?? 'point',
      x: params.x ?? 0,
      y: params.y ?? 0,
      entryIndex: params.entryIndex ?? 0,
      button: params.button ?? 0,
      clickCount: params.clickCount ?? 1,
      modifiers: params.modifiers,
    })));

  server.tool('unity_editor_key',
    'Sends keyboard events to an editor tool window: text is typed character by character, and keyCode presses a named key such as Return or Escape. For filling text fields, unity_editor_set_field is more reliable because it does not depend on GUI focus.',
    {
      ...commonShape,
      ...windowShape,
      text: z.string().optional(),
      keyCode: z.string().optional().describe('UnityEngine.KeyCode name, e.g. Return, Escape, Tab, A.'),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_key', {
      text: params.text,
      keyCode: params.keyCode,
      modifiers: params.modifiers,
    })));

  // ---------------------------------------------------------------- menus

  server.tool('unity_editor_menu_execute',
    'Runs a Unity main-menu item by its exact path, e.g. "Edit/Delete PlayerPrefs". Fails loudly when the path does not exist rather than silently doing nothing.',
    { ...commonShape, menuPath: z.string() },
    async (params) => toToolResult(await runBridge(params, 'editor_menu_execute', { menuPath: params.menuPath })));

  server.tool('unity_editor_menu_list',
    'Lists registered [MenuItem] paths with the type and method behind each one, optionally filtered by substring. Use it to find the exact menu path for a tool.',
    { ...commonShape, filter: z.string().optional() },
    async (params) => toToolResult(await runBridge(params, 'editor_menu_list', { filter: params.filter })));

  // ---------------------------------------------------------------- console

  server.tool('unity_editor_console_read',
    'Reads Unity Console entries newest first, with error/warning/log counts. This is the main way to confirm that an editor tool action actually ran or that it logged an error.',
    {
      ...commonShape,
      maxEntries: z.number().int().positive().max(1000).optional(),
      filter: z.string().optional().describe('Only return entries containing this text.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_console_read', {
      maxEntries: params.maxEntries ?? 100,
      filter: params.filter,
    })));

  server.tool('unity_editor_console_clear',
    'Clears the Unity Console. Call this before an action so the entries that follow belong only to that action.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_console_clear', {})));

  // ---------------------------------------------------------------- prefs

  server.tool('unity_editor_prefs_get',
    'Reads an EditorPrefs or PlayerPrefs value. Many editor tools persist their state here, which makes it good independent evidence that an action took effect.',
    {
      ...commonShape,
      prefKey: z.string(),
      prefStore: z.enum(['editor', 'player']).optional(),
      prefType: z.enum(['string', 'int', 'float', 'bool']).optional(),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_prefs_get', {
      prefKey: params.prefKey,
      prefStore: params.prefStore ?? 'editor',
      prefType: params.prefType ?? 'string',
    })));

  server.tool('unity_editor_prefs_set',
    'Writes an EditorPrefs or PlayerPrefs value, for setting up a known starting state before exercising a tool.',
    {
      ...commonShape,
      prefKey: z.string(),
      fieldValue: z.string().describe('Value as text.'),
      prefStore: z.enum(['editor', 'player']).optional(),
      prefType: z.enum(['string', 'int', 'float', 'bool']).optional(),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_prefs_set', {
      prefKey: params.prefKey,
      fieldValue: params.fieldValue,
      prefStore: params.prefStore ?? 'editor',
      prefType: params.prefType ?? 'string',
    })));

  // ---------------------------------------------------------------- play mode

  server.tool('unity_editor_play_mode',
    'Reads or changes play mode, and reports whether the editor is compiling. Unity defers recompilation while playing, so exit play mode when a newly added bridge command comes back as unsupported.',
    {
      ...commonShape,
      playModeAction: z.enum(['status', 'enter', 'exit', 'toggle']).optional(),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_play_mode', {
      playModeAction: params.playModeAction ?? 'status',
    })));

  // ---------------------------------------------------------------- tests

  server.tool('unity_run_tests_in_editor',
    'Starts a Unity Test Framework run inside the already-open editor using TestRunnerApi and returns a runId immediately. Unlike the CLI test tools this does not need a second Unity process, so it works while the editor holds the project lock. Poll unity_get_test_results with the runId.',
    {
      ...commonShape,
      testMode: z.enum(['EditMode', 'PlayMode']).optional(),
      testFilter: z.string().optional().describe('Semicolon separated full test names.'),
      assemblyNames: z.string().optional().describe('Semicolon separated test assembly names.'),
      categoryNames: z.string().optional().describe('Semicolon separated categories.'),
    },
    async (params) => toToolResult(await runBridge(params, 'run_tests', {
      testMode: params.testMode ?? 'EditMode',
      testFilter: params.testFilter,
      assemblyNames: params.assemblyNames,
      categoryNames: params.categoryNames,
    })));

  server.tool('unity_get_test_results',
    'Reads the result of an in-editor test run: status, pass/fail/skip counts and per-test outcomes with failure messages. Results are stored on disk, so they survive the domain reload a test run can trigger.',
    { ...commonShape, runId: z.string().optional().describe('Defaults to the most recent run in this editor session.') },
    async (params) => toToolResult(await runBridge(params, 'get_test_results', { runId: params.runId })));

  server.tool('unity_list_tests',
    'Lists the tests the editor knows about for a given mode, so a filter can be built without guessing names. The scan runs across later editor updates, so the first call may return resolved=false; call again to get the result.',
    {
      ...commonShape,
      testMode: z.enum(['EditMode', 'PlayMode']).optional(),
      maxEntries: z.number().int().positive().max(5000).optional(),
      refresh: z.boolean().optional().describe('Rescan even when a cached list exists.'),
    },
    async (params) => toToolResult(await runBridge(params, 'list_tests', {
      testMode: params.testMode ?? 'EditMode',
      maxEntries: params.maxEntries ?? 2000,
      refresh: params.refresh ?? false,
    })));
}

function dragParameters(params: any): Record<string, unknown> {
  return {
    fromX: params.fromX,
    fromY: params.fromY,
    toX: params.toX,
    toY: params.toY,
    durationMs: params.durationMs ?? 240,
    moveStepCount: params.moveStepCount ?? 12,
    coordinateSpace: params.coordinateSpace ?? 'content',
    button: params.button ?? 0,
    modifiers: params.modifiers,
    noFocus: params.noFocus ?? false,
  };
}

async function runBridge(params: any, command: string, extra: Record<string, unknown>): Promise<any> {
  const config = resolveProjectConfig(params);
  const response = await executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command,
    parameters: {
      windowType: params.windowType,
      windowTitle: params.windowTitle,
      instanceId: params.instanceId ?? 0,
      ...stripUndefined(extra),
    },
    timeoutMs: params.timeoutMs ?? 20000,
    runOnce: params.runOnce ?? false,
  });

  return { ...response, outputs: decodeOutputs(response) };
}

function stripUndefined(source: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(source)) {
    if (value !== undefined) {
      result[key] = value;
    }
  }
  return result;
}

/**
 * The bridge can only return string key/value pairs (Unity's JsonUtility has no dictionary support), so
 * structured payloads travel as JSON text. Decode those back so callers get real arrays and objects.
 */
function decodeOutputs(response: EditorCommandResponse): Record<string, unknown> {
  const outputs = response.outputs;
  const flat: Record<string, unknown> = {};

  if (Array.isArray(outputs)) {
    for (const item of outputs) {
      if (item && typeof item === 'object' && 'key' in item && 'value' in item) {
        const entry = item as { key?: unknown; value?: unknown };
        if (typeof entry.key === 'string') {
          flat[entry.key] = entry.value;
        }
      }
    }
  } else if (outputs && typeof outputs === 'object') {
    Object.assign(flat, outputs);
  }

  for (const [key, value] of Object.entries(flat)) {
    if (typeof value !== 'string') continue;
    const trimmed = value.trim();
    const looksStructured = trimmed.startsWith('[') || trimmed.startsWith('{');
    if (!JSON_OUTPUT_KEYS.has(key) && !looksStructured) continue;
    if (!looksStructured) continue;

    try {
      flat[key] = JSON.parse(trimmed);
    } catch {
      // Leave the raw text in place: a half-formed payload is still more useful than a dropped field.
    }
  }

  return flat;
}

function toToolResult(value: unknown): any {
  return {
    content: [
      {
        type: 'text',
        text: JSON.stringify(value, null, 2),
      },
    ],
  };
}
