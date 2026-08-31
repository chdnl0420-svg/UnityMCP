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
  'events', 'frames', 'elements', 'elementRect', 'messages', 'assemblies', 'objects',
]);

/** Element filters shared by the query and by the "element" target mode of the input commands. */
const elementShape = {
  elementName: z.string().optional().describe('Exact VisualElement name (the "name" field, not the type).'),
  elementClass: z.string().optional().describe('USS class the element carries.'),
  elementType: z.string().optional().describe('Element type name, e.g. "Button", "Toggle", "ListView".'),
  elementText: z.string().optional().describe('Text the element contains, case-insensitive substring.'),
  elementIndex: z.number().int().min(0).optional().describe('Which match to use when several match (default 0).'),
};

/**
 * The coordinate space for the two click commands, which share one resolver on the Unity side and so
 * have to offer the same choice here. Note the default is the opposite of the drag/move/scroll one:
 * click coordinates have always been host-view, and that is also the space unity_editor_element_query
 * reports, so changing it would silently move every existing click by the tab strip height.
 */
const clickCoordinateSpaceSchema = z.enum(['host', 'content']).optional()
  .describe('"host" (default for clicks) sends raw host-view coordinates, the space unity_editor_element_query reports; "content" treats 0,0 as the window\'s content corner and adds the dock tab strip offset, matching unity_editor_drag and unity_editor_move.');

function elementParameters(params: any): Record<string, unknown> {
  return {
    elementName: params.elementName,
    elementClass: params.elementClass,
    elementType: params.elementType,
    elementText: params.elementText,
    elementIndex: params.elementIndex ?? 0,
  };
}

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
      includePopups: z.boolean().optional()
        .describe('Draw this process\'s popups and modal dialogs that sit over the target into the same image. A confirmation box or a right-click menu is a separate OS window and is otherwise absent from the picture. Each overlay is read with PrintWindow like the target itself, never from the desktop, so a locked screen cannot turn it white and no other application can appear in the frame. Reports how many were composited and their titles.'),
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
        includePopups: params.includePopups ?? false,
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

  server.tool('unity_editor_drag_drop',
    'Runs a real editor drag and drop - the kind that moves an item between two panes of a tool window - and saves a PNG at each stage. unity_editor_drag cannot do this on its own: once the source calls DragAndDrop.StartDrag the editor takes over the gesture, and the receiving side then waits for DragUpdated and DragPerform, which no amount of further MouseDrag produces. This sends the press and a few short moves to make the source start the drag, then DragUpdated (twice, with a pause so the drop highlight is on screen long enough to capture), DragPerform and DragExited at the destination. Use framesDir to keep the frames; the hover frame is usually the one worth showing.',
    {
      ...commonShape,
      ...windowShape,
      fromX: z.number().describe('Drag start x, in window-local coordinates - the item being dragged.'),
      fromY: z.number().describe('Drag start y.'),
      toX: z.number().describe('Drop target x.'),
      toY: z.number().describe('Drop target y.'),
      coordinateSpace: z.enum(['content', 'host']).optional()
        .describe('"content" (default) treats 0,0 as the window\'s content corner and adds the dock tab strip offset automatically; "host" sends raw host-view coordinates.'),
      hoverMs: z.number().int().min(0).max(10000).optional()
        .describe('How long to hold over the target before dropping, so the highlight is painted and capturable (default 400).'),
      performDrop: z.boolean().optional()
        .describe('Actually drop (default true). False hovers and then leaves, which captures the highlight without changing anything.'),
      genericDataKey: z.string().optional()
        .describe('The DragAndDrop.SetGenericData key the receiving tool reads. Naming it reports whether the source armed the drag - the first thing to know when a drop does nothing.'),
      genericDataJson: z.string().optional()
        .describe('Payload to stand in with when the source did not arm one. Lights a highlight for a screenshot, but a tool that mutates the dragged item will mutate this copy, not its own model - real edits need the source to start the drag itself.'),
      genericDataType: z.string().optional()
        .describe('Full type name to deserialise genericDataJson into (e.g. "MyTool.DragPayload"). Omit to pass the JSON through as a raw string.'),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
      noFocus: z.boolean().optional().describe('Do not focus the window first. Most drags need focus, so this is off by default.'),
      framesDir: z.string().optional().describe('Directory for the frames. Omit to run without capturing.'),
      includeChrome: z.boolean().optional(),
      captureBackend: z.enum(['auto', 'printwindow', 'screen', 'framebuffer']).optional(),
      captureSettleMs: z.number().int().min(0).max(2000).optional(),
      allowUniform: z.boolean().optional(),
    },
    async (params) => {
      const result = await runBridge(params, 'editor_drag_drop', {
        fromX: params.fromX,
        fromY: params.fromY,
        toX: params.toX,
        toY: params.toY,
        coordinateSpace: params.coordinateSpace ?? 'content',
        hoverMs: params.hoverMs ?? 400,
        performDrop: (params.performDrop ?? true) ? 'true' : 'false',
        genericDataKey: params.genericDataKey,
        genericDataJson: params.genericDataJson,
        genericDataType: params.genericDataType,
        modifiers: params.modifiers,
        noFocus: params.noFocus ?? false,
        framesDir: params.framesDir,
        includeChrome: params.includeChrome ?? false,
        captureBackend: params.captureBackend ?? 'auto',
        captureSettleMs: params.captureSettleMs ?? 0,
        allowUniform: params.allowUniform ?? false,
      });

      // Same re-check as the capture command: the bridge reports what it believes it wrote, and a
      // caller reading these paths cares whether the files are actually there.
      const frames = Array.isArray(result.outputs?.frames) ? result.outputs.frames : [];
      const verified = await Promise.all(frames.map(async (frame: any) => ({
        ...frame,
        pngExists: await pathExists(frame?.path ?? ''),
        pngBytes: await fileSize(frame?.path ?? ''),
      })));

      return toToolResult({
        ...result,
        outputs: {
          ...result.outputs,
          frames: verified,
          framesOnDisk: verified.filter((frame) => frame.pngExists && frame.pngBytes > 0).length,
        },
      });
    });

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

  // ---------------------------------------------------------------- hover

  server.tool('unity_editor_move',
    'Moves the mouse pointer inside an editor window with no button held, so hover behaviour actually runs. '
    + 'unity_editor_drag cannot test hover: its moves are MouseDrag events, which reach UI Toolkit as a MouseMoveEvent with pressedButtons set, '
    + 'so anything that only reacts to a bare cursor - GraphView highlighting the edge under the mouse, a hover tooltip, a rollover tint - never fires. '
    + 'This sends a single MouseMove with pressedButtons 0, and no MouseDown, MouseDrag or MouseUp, so it cannot move a node, change the selection, start a marquee, pan or open a context menu. '
    + 'Consecutive calls on the same window carry the delta from the previous point, and each window keeps its own. '
    + 'The response reports what was sent, not that the UI reacted: confirm the hover with unity_editor_window_capture right after, or by reading the tool\'s state with unity_editor_get_field.',
    {
      ...commonShape,
      ...windowShape,
      ...elementShape,
      targetMode: z.enum(['point', 'element']).optional()
        .describe('"point" (default) uses x/y; "element" hovers the centre of the UI Toolkit element matching the element filters, which survives a resize.'),
      x: z.number().optional().describe('Pointer x, in window-local coordinates. Required unless targetMode is "element".'),
      y: z.number().optional().describe('Pointer y, in window-local coordinates. Required unless targetMode is "element".'),
      coordinateSpace: z.enum(['content', 'host']).optional()
        .describe('Same convention as unity_editor_drag: "content" (default) treats 0,0 as the window\'s content corner and adds the dock tab strip offset automatically; "host" sends raw host-view coordinates, matching unity_editor_click.'),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
      ensureWantsMouseMove: z.boolean().optional()
        .describe('Turn EditorWindow.wantsMouseMove on for the send and put it straight back (default true). IMGUI code only receives EventType.MouseMove in a window that set this flag - that is Unity\'s rule for a real mouse too - so an IMGUI tool that never set it would see nothing. Pass false to send exactly what production would see. UI Toolkit is unaffected either way.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_move', {
      targetMode: params.targetMode ?? 'point',
      x: params.x,
      y: params.y,
      coordinateSpace: params.coordinateSpace ?? 'content',
      modifiers: params.modifiers,
      ensureWantsMouseMove: (params.ensureWantsMouseMove ?? true) ? 'true' : 'false',
      ...elementParameters(params),
    })));

  server.tool('unity_editor_scroll',
    'Turns the mouse wheel at a point inside an editor window, so a ScrollView, a long inspector or a GraphView zoom actually moves. '
    + 'One notch is about 3 and positive y scrolls down, matching Unity\'s own event convention. '
    + 'Coordinates follow unity_editor_drag, or aim at a named UI Toolkit element with targetMode "element".',
    {
      ...commonShape,
      ...windowShape,
      ...elementShape,
      scrollX: z.number().optional().describe('Horizontal wheel delta.'),
      scrollY: z.number().optional().describe('Vertical wheel delta. About 3 per notch, positive scrolls down.'),
      targetMode: z.enum(['point', 'element']).optional(),
      x: z.number().optional(),
      y: z.number().optional(),
      coordinateSpace: z.enum(['content', 'host']).optional(),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_scroll', {
      targetMode: params.targetMode ?? 'point',
      x: params.x ?? 0,
      y: params.y ?? 0,
      scrollX: params.scrollX ?? 0,
      scrollY: params.scrollY ?? 0,
      coordinateSpace: params.coordinateSpace ?? 'content',
      modifiers: params.modifiers,
      ...elementParameters(params),
    })));

  // ---------------------------------------------------------------- elements

  server.tool('unity_editor_element_query',
    'Finds UI Toolkit elements in an editor window by name, USS class, type or text, and returns where each one is on screen. '
    + 'Use this instead of guessing pixels: the returned centerX/centerY are host-view coordinates that feed straight into '
    + 'unity_editor_click, unity_editor_move or unity_editor_scroll with coordinateSpace "host" - or skip the copy and pass the same filters with targetMode "element". '
    + 'Each hit reports type, name, classes, text, rect, enabled, visible and pickable, so a click that would land on a disabled or zero-sized element is visible before it is sent.',
    { ...commonShape, ...windowShape, ...elementShape },
    async (params) => toToolResult(await runBridge(params, 'editor_element_query', elementParameters(params))));

  // ---------------------------------------------------------------- selection

  server.tool('unity_editor_selection_get',
    'Reads the current editor selection: every selected object with its name, type, instance id and asset path, plus the active object.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_selection_get', {})));

  server.tool('unity_editor_selection_set',
    'Selects assets or scene objects, which is how an inspector-driven tool is put in front of the thing it should act on before its buttons are clicked. '
    + 'Pass project-relative asset paths and/or one instance id. Passing neither clears the selection. Fails if any path cannot be resolved, rather than silently selecting a subset.',
    {
      ...commonShape,
      assetPaths: z.array(z.string()).optional().describe('Project-relative paths, e.g. ["Assets/Prefabs/Hero.prefab"].'),
      instanceId: z.number().int().optional().describe('Instance id of a scene object or asset.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_selection_set', {
      // Unit Separator, so a path containing a comma survives the trip.
      assetPaths: (params.assetPaths ?? []).join(''),
    })));

  // ---------------------------------------------------------------- compile

  server.tool('unity_editor_refresh',
    'Reimports changed assets and recompiles scripts in the open editor, then reports the compiler\'s own verdict. '
    + 'This is a real compile gate: errorCount comes from Unity\'s CompilerMessages, so "0 errors" means the assemblies built. '
    + 'By default it waits for compilation to finish (polling across the domain reload that a recompile triggers) and returns the errors and warnings with file and line. '
    + 'Set waitForCompile false to fire and forget, then poll unity_editor_compile_status yourself.',
    {
      ...commonShape,
      forceRecompile: z.boolean().optional().describe('Also request a script recompilation even when no asset changed. Off by default.'),
      waitForCompile: z.boolean().optional().describe('Wait until compilation finishes (default true).'),
      waitTimeoutMs: z.number().int().min(1000).max(30 * 60 * 1000).optional().describe('How long to wait for compilation (default 30000).'),
    },
    async (params) => {
      const requested = await runBridge(params, 'editor_refresh', {
        forceRecompile: (params.forceRecompile ?? false) ? 'true' : 'false',
      });

      if (!requested.success || (params.waitForCompile ?? true) === false) {
        return toToolResult(requested);
      }

      const status = await waitForCompile(params, params.waitTimeoutMs ?? 30000);
      return toToolResult({
        ...requested,
        outputs: { ...requested.outputs, compile: status.outputs, compileWaitTimedOut: status.timedOut },
        // An editor that came back with compile errors is not a successful refresh from the caller's side.
        success: requested.success && !status.timedOut && Number(status.outputs?.errorCount ?? 0) === 0,
      });
    });

  server.tool('unity_editor_compile_status',
    'Reads the last compilation result recorded in the editor: status, whether it is compiling right now, error and warning counts, the assemblies that rebuilt, and each compiler message with file, line and column. '
    + 'The result is stored on disk by the package, so it survives the domain reload a recompile causes.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_compile_status', {})));

  // ---------------------------------------------------------------- waiting

  server.tool('unity_editor_wait_for_field',
    'Polls one field on an editor tool window until it reaches the expected value, or the timeout runs out. '
    + 'Use this instead of a fixed sleep after triggering slow work: it returns as soon as the value lands, and reports the last value it saw when it does not. '
    + 'The editor is never blocked - the polling happens on this side.',
    {
      ...commonShape,
      ...windowShape,
      fieldPath: z.string().describe('Dotted/indexed path, e.g. "_isBuilding" or "_results[0].state".'),
      expected: z.string().describe('Value to wait for, compared as text.'),
      comparison: z.enum(['equals', 'contains', 'notEquals']).optional().describe('How to compare (default equals).'),
      pollIntervalMs: z.number().int().min(100).max(10000).optional().describe('Gap between reads (default 500).'),
      waitTimeoutMs: z.number().int().min(1000).max(30 * 60 * 1000).optional().describe('Give up after this long (default 6000).'),
    },
    async (params) => {
      const interval = params.pollIntervalMs ?? 500;
      const deadline = Date.now() + (params.waitTimeoutMs ?? 6000);
      const comparison = params.comparison ?? 'equals';
      let attempts = 0;
      let last: any;

      while (Date.now() < deadline) {
        attempts++;
        last = await runBridge(params, 'editor_get_field', { fieldPath: params.fieldPath, maxDepth: 0 });
        const value = String(last.outputs?.value ?? '');
        const matched = comparison === 'equals' ? value === params.expected
          : comparison === 'contains' ? value.includes(params.expected)
          : value !== params.expected;

        if (matched) {
          return toToolResult({ ...last, outputs: { ...last.outputs, matched: true, attempts, waitedMs: undefined } });
        }

        await new Promise((resolve) => setTimeout(resolve, interval));
      }

      return toToolResult({
        ...(last ?? {}),
        success: false,
        outputs: { ...(last?.outputs ?? {}), matched: false, attempts, expected: params.expected, comparison },
        error: { message: `Field ${params.fieldPath} did not reach "${params.expected}" within the timeout. Last value: "${last?.outputs?.value ?? '(never read)'}"` },
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
      methodArgsText: (params.methodArgs ?? []).join('\u001F'),
    })));

  // ---------------------------------------------------------------- input

  server.tool('unity_editor_click',
    'Clicks inside an editor tool window by injecting a real IMGUI MouseDown/MouseUp pair, so the tool\'s own button callback runs. Target either a point (x, y in window-local coordinates) or a layout entry index from unity_editor_window_dump.',
    {
      ...commonShape,
      ...windowShape,
      ...elementShape,
      targetMode: z.enum(['point', 'entry', 'element']).optional()
        .describe('"point" (default) uses x/y; "entry" uses entryIndex from the dump\'s layout array; "element" clicks the centre of the UI Toolkit element matching the element filters.'),
      x: z.number().optional(),
      y: z.number().optional(),
      coordinateSpace: clickCoordinateSpaceSchema,
      entryIndex: z.number().int().min(0).optional().describe('Index "i" of a layout entry from unity_editor_window_dump.'),
      button: z.number().int().min(0).max(2).optional(),
      clickCount: z.number().int().min(1).max(3).optional(),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_click', {
      targetMode: params.targetMode ?? 'point',
      x: params.x ?? 0,
      y: params.y ?? 0,
      coordinateSpace: params.coordinateSpace,
      entryIndex: params.entryIndex ?? 0,
      button: params.button ?? 0,
      clickCount: params.clickCount ?? 1,
      modifiers: params.modifiers,
      ...elementParameters(params),
    })));

  server.tool('unity_editor_context_click',
    'Right-clicks inside an editor tool window so a context menu actually opens: it injects MouseDown and MouseUp with the right button and then the EventType.ContextClick that Unity opens menus from, which unity_editor_click with button 1 never sends. This is what reaches a GraphView\'s BuildContextualMenu, an IMGUI GenericMenu and a UI Toolkit ContextualMenuManipulator. Targets a point, a layout entry or a UI Toolkit element, exactly as unity_editor_click does.',
    {
      ...commonShape,
      ...windowShape,
      ...elementShape,
      targetMode: z.enum(['point', 'entry', 'element']).optional()
        .describe('"point" (default) uses x/y; "entry" uses entryIndex from the dump\'s layout array; "element" right-clicks the centre of the UI Toolkit element matching the element filters.'),
      x: z.number().optional(),
      y: z.number().optional(),
      coordinateSpace: clickCoordinateSpaceSchema,
      entryIndex: z.number().int().min(0).optional().describe('Index "i" of a layout entry from unity_editor_window_dump.'),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
    },
    // No button or clickCount: a context click is one right-button gesture by definition, so letting a
    // caller pick another button would only ever produce a click that opens nothing.
    async (params) => toToolResult(await runBridge(params, 'editor_context_click', {
      targetMode: params.targetMode ?? 'point',
      x: params.x ?? 0,
      y: params.y ?? 0,
      coordinateSpace: params.coordinateSpace,
      entryIndex: params.entryIndex ?? 0,
      modifiers: params.modifiers,
      ...elementParameters(params),
    })));

  server.tool('unity_editor_key',
    'Sends keyboard events to an editor tool window: text is typed character by character, and keyCode presses a named key such as Return or Escape. Reaches UI Toolkit KeyDownEvent handlers as well as IMGUI, because the panel\'s focused element is preserved across the focus call. A key only reaches an element callback if something holds focus - the response says what held it, and says so explicitly when nothing did. For filling text fields, unity_editor_set_field is still more reliable because it does not depend on focus at all.',
    {
      ...commonShape,
      ...windowShape,
      text: z.string().optional(),
      keyCode: z.string().optional().describe('UnityEngine.KeyCode name, e.g. Return, Escape, Tab, A.'),
      modifiers: z.string().optional().describe('Comma separated: shift, control, alt, command.'),
      noFocus: z.boolean().optional()
        .describe('Do not focus the window first. Use when the caller has already put focus exactly where it wants it.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_key', {
      text: params.text,
      keyCode: params.keyCode,
      modifiers: params.modifiers,
      noFocus: params.noFocus ?? false,
    })));

  // ---------------------------------------------------------------- modal dialogs

  server.tool('unity_editor_dialog_click',
    'Presses a button on a modal dialog. Can be sent before the dialog appears (it waits) or after it is already up - this command is read by a background thread, so it still arrives while the editor is frozen. The button is chosen by label or index, pressed as a real button with BM_CLICK, and the label actually pressed is reported. Read the outcome with unity_editor_dialog_status.',
    {
      ...commonShape,
      buttonLabel: z.string().optional()
        .describe('Substring of the button text, matched case-insensitively. Preferred over index: it is checked against the real button and reported back.'),
      buttonIndex: z.number().int().min(0).optional()
        .describe('Zero-based button position, used when no label is given. 0 is the accept button, 1 the cancel one.'),
      dialogTitle: z.string().optional()
        .describe('Only act on a dialog whose title contains this. Required to target a Unity container window rather than a native dialog box.'),
      armMs: z.number().int().min(500).max(120000).optional()
        .describe('How long to wait for the dialog to appear before giving up (default 1500).'),
      onMiss: z.enum(['cancel', 'leave']).optional()
        .describe('What to do when no button matches. "cancel" (default) presses the last button - normally Cancel - so the editor starts ticking again, and reports missed=true so the press is not mistaken for a choice. "leave" presses nothing, which leaves the editor blocked until someone dismisses the dialog by hand.'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_dialog_click', {
      buttonLabel: params.buttonLabel,
      buttonIndexText: params.buttonIndex === undefined ? undefined : String(params.buttonIndex),
      dialogTitle: params.dialogTitle,
      armMs: params.armMs ?? 1500,
      onMiss: params.onMiss ?? 'cancel',
    })));

  server.tool('unity_editor_dialog_status',
    'Reports what the armed dialog watcher saw and did: the dialog title, the buttons it offered, which label was pressed, whether it was pressed as a real button or by keyboard, and whether the dialog actually closed.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_dialog_status', {})));

  server.tool('unity_editor_dialog_capture',
    'Photographs the editor while a modal dialog has it frozen - the one picture unity_editor_window_capture cannot take, because that command starts from an EditorWindow and turning one into an OS window handle needs the main thread that the dialog is holding. This runs entirely off the main thread, so it works exactly when nothing else does. By default it shoots the window the last normal capture resolved; if a dialog is up and is not over that window, it falls back to the largest window of the process (the main editor one) so the dialog is actually in frame, and says so in targetBasis. Popups and dialogs above the subject are composited in with PrintWindow, never a desktop read.',
    {
      ...commonShape,
      outputPath: z.string().optional().describe('Defaults to <commandRoot>/screenshots/editor-dialog-capture-<ticks>.png.'),
      windowTitle: z.string().optional()
        .describe('Shoot the window whose title contains this instead of choosing one automatically. Use the dialog title to capture the dialog alone.'),
      includePopups: z.boolean().optional()
        .describe('Composite overlapping popups and dialogs into the frame (default true here - a capture taken during a dialog that left the dialog out would answer the wrong question).'),
    },
    async (params) => toToolResult(await runBridge(params, 'editor_dialog_capture', {
      outputPath: params.outputPath,
      windowTitle: params.windowTitle,
      includePopups: (params.includePopups ?? true) ? 'true' : 'false',
    })));

  server.tool('unity_editor_dialog_list',
    'Lists modal dialogs currently open in the editor process, with their buttons. Usually returns nothing by design - while a modal is up the editor does not tick, so this command cannot run. Useful for native dialogs that leave the editor running.',
    { ...commonShape },
    async (params) => toToolResult(await runBridge(params, 'editor_dialog_list', {})));

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

/**
 * Polls the editor until compilation settles.
 *
 * A recompile ends in a domain reload, which kills any in-flight request, so waiting has to happen
 * from this side with short independent calls. A reload also makes the bridge briefly unreachable:
 * a failed poll is treated as "still busy" rather than as a verdict, or every recompile would look
 * like an error.
 */
async function waitForCompile(params: any, timeoutMs: number): Promise<{ outputs: any; timedOut: boolean }> {
  const deadline = Date.now() + timeoutMs;
  let started = false;
  let last: any;

  while (Date.now() < deadline) {
    try {
      last = await runBridge({ ...params, timeoutMs: 1500 }, 'editor_compile_status', {});
      const status = String(last.outputs?.status ?? '');
      const compiling = String(last.outputs?.isCompiling ?? '') === 'true';

      if (compiling || status === 'compiling') {
        started = true;
      } else if (status === 'finished' || status === 'idle' || status === 'failed') {
        // "idle" straight after the request means nothing needed rebuilding; give the compiler a
        // moment to start before believing it.
        if (started || status !== 'idle' || Date.now() > deadline - timeoutMs + 4000) {
          return { outputs: last.outputs, timedOut: false };
        }
      }
    } catch {
      // Unreachable during the reload; keep polling.
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  return { outputs: last?.outputs ?? {}, timedOut: true };
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
    timeoutMs: params.timeoutMs ?? 2000,
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
