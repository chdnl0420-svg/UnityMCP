import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createConnection } from 'node:net';
import { z } from 'zod';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { resolveProjectConfig } from './config.js';
import { executeEditorCommand } from './commandBridge.js';
import { launchUnity, runUnityTests } from './unityCli.js';
import { fileSize, pathExists, readTail } from './utils/files.js';
import { summarizeEditorLog } from './utils/editorLog.js';
import { findStaleCandidates, killProcess, listUnityRelatedProcesses } from './processes.js';
import { normalizeOutputs, setPlayModeAndWait } from './playMode.js';
import { clickUiTextAndWait, waitForUiText, waitThenClick } from './uiText.js';
import { runUiTextQaFlow } from './qaFlow.js';
import { verifyScreenshotResponse } from './screenshot.js';
import { registerEditorTools } from './editorTools.js';

const baseConfigShape = {
  unityPath: z.string().optional(),
  projectPath: z.string().optional(),
  commandRoot: z.string().optional(),
};

const timeoutSchema = z.number().int().positive().max(60 * 60 * 1000).optional();

export function registerTools(server: McpServer): void {
  server.tool('unity_status', 'Checks Unity, ProjectM, command bridge, logs, port, and stale process status.', {
    ...baseConfigShape,
  }, async (params) => toToolResult(await unityStatus(params)));

  server.tool('unity_launch', 'Launches Unity 2022.3.76f1 for ProjectM using normal user privileges.', {
    ...baseConfigShape,
    extraArgs: z.array(z.string()).optional(),
  }, async (params) => toToolResult(await unityLaunch(params)));

  server.tool('unity_run_editmode_tests', 'Runs Unity Test Framework EditMode tests through Unity CLI and parses XML results.', {
    ...baseConfigShape,
    testFilter: z.string().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unityRunTests({ ...params, mode: 'EditMode' })));

  server.tool('unity_run_playmode_tests', 'Runs Unity Test Framework PlayMode tests through Unity CLI and parses XML results.', {
    ...baseConfigShape,
    testFilter: z.string().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unityRunTests({ ...params, mode: 'PlayMode' })));

  server.tool('unity_read_editor_log', 'Reads Editor.log tail and summarizes recent warnings, errors, and bridge lines.', {
    logPath: z.string().optional(),
    maxBytes: z.number().int().positive().max(20 * 1024 * 1024).optional(),
    maxLines: z.number().int().positive().max(2000).optional(),
  }, async (params) => toToolResult(await unityReadEditorLog(params)));

  server.tool('unity_execute_editor_command', 'Writes a request JSON file and waits for the Unity editor bridge response JSON.', {
    ...baseConfigShape,
    command: z.string(),
    parameters: z.record(z.unknown()).optional(),
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityExecuteEditorCommand(params)));

  server.tool('unity_capture_screenshot', 'Requests a Unity screenshot and verifies that the PNG exists and has non-zero size.', {
    ...baseConfigShape,
    outputPath: z.string().optional(),
    cameraName: z.string().optional(),
    width: z.number().int().positive().max(8192).optional(),
    height: z.number().int().positive().max(8192).optional(),
    requireRequestedSize: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityCaptureScreenshot(params)));

  server.tool('unity_click_ui_text', 'Finds a visible NGUI label by text and clicks its resolved clickable target.', {
    ...baseConfigShape,
    text: z.string().min(1),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityClickUiText(params)));

  server.tool('unity_wait_ui_text', 'Polls dump_ui until the requested UI text appears.', {
    ...baseConfigShape,
    text: z.string().min(1),
    exact: z.boolean().optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
  }, async (params) => toToolResult(await unityWaitUiText(params)));

  server.tool('unity_click_ui_text_and_wait', 'Clicks a visible NGUI label by text, then waits for expected UI text.', {
    ...baseConfigShape,
    clickText: z.string().min(1),
    waitText: z.string().min(1),
    exact: z.boolean().optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
  }, async (params) => toToolResult(await unityClickUiTextAndWait(params)));

  server.tool('unity_wait_then_click', 'Polls dump_ui until the requested text appears, then clicks it. Useful for buttons that appear after a variable-length animation or load.', {
    ...baseConfigShape,
    text: z.string().min(1),
    exact: z.boolean().optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
  }, async (params) => toToolResult(await unityWaitThenClick(params)));

  server.tool('unity_run_ui_text_qa_flow', 'Runs a full PlayMode UI text QA flow: enter, wait, screenshot, click, wait, screenshot, and optional exit.', {
    ...baseConfigShape,
    initialText: z.string().min(1),
    clickText: z.string().min(1),
    expectedText: z.string().min(1),
    outputRoot: z.string().optional(),
    exact: z.boolean().optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
    width: z.number().int().positive().max(8192).optional(),
    height: z.number().int().positive().max(8192).optional(),
    requireRequestedSize: z.boolean().optional(),
    exitPlayMode: z.boolean().optional(),
  }, async (params) => toToolResult(await unityRunUiTextQaFlow(params)));

  server.tool('unity_enter_play_mode', 'Requests Unity PlayMode and waits until editor_status reports isPlaying=true.', {
    ...baseConfigShape,
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
  }, async (params) => toToolResult(await unitySetPlayMode(params, true)));

  server.tool('unity_exit_play_mode', 'Requests Unity to leave PlayMode and waits until editor_status reports isPlaying=false.', {
    ...baseConfigShape,
    timeoutMs: timeoutSchema,
    pollIntervalMs: z.number().int().positive().max(10000).optional(),
  }, async (params) => toToolResult(await unitySetPlayMode(params, false)));
  server.tool('unity_start_frame_capture', 'Starts recording the Unity game view as a PNG frame sequence on every editor update, for fast motion a single screenshot round-trip misses. Returns immediately with the frames folder; run the fast action, then call unity_stop_frame_capture. Auto-stops at maxFrames/maxDurationSeconds. Use only when a single screenshot cannot catch the change.', {
    ...baseConfigShape,
    framesDir: z.string().optional(),
    cameraName: z.string().optional(),
    width: z.number().int().positive().max(8192).optional(),
    height: z.number().int().positive().max(8192).optional(),
    captureEveryNthUpdate: z.number().int().positive().max(60).optional(),
    maxFrames: z.number().int().positive().max(3600).optional(),
    maxDurationSeconds: z.number().positive().max(120).optional(),
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityStartFrameCapture(params)));

  server.tool('unity_stop_frame_capture', 'Stops the running game view frame-sequence recording and returns the frames folder, frame count, and fps. Read the PNG frame sequence in order to inspect the fast motion.', {
    ...baseConfigShape,
    framesDir: z.string().optional(),
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityStopFrameCapture(params)));

  server.tool('unity_kill_stale', 'Reports stale Unity/node/MCP processes and optionally kills only explicit stale candidates.', {
    ...baseConfigShape,
    kill: z.boolean().optional(),
    includeUnity: z.boolean().optional(),
  }, async (params) => toToolResult(await unityKillStale(params)));

  // Recompiling, compile status and the console are covered by unity_editor_refresh,
  // unity_editor_compile_status, unity_editor_console_read and unity_editor_console_clear.

  server.tool('unity_inspect_object', 'Inspects a GameObject by hierarchy path or name: components, active state, transform, NGUI label/sprite/input, and collider bounds.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'inspect_object', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    includeInactive: params.includeInactive ?? false,
  })));

  server.tool('unity_find_objects', 'Finds active GameObjects whose name contains a query string, returning hierarchy paths, active state, and clickable/label hints.', {
    ...baseConfigShape,
    nameQuery: z.string().min(1),
    includeInactive: z.boolean().optional(),
    maxCount: z.number().int().positive().max(500).optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'find_objects', {
    nameQuery: params.nameQuery,
    includeInactive: params.includeInactive ?? false,
    maxCount: params.maxCount,
  })));

  server.tool('unity_set_active', 'Activates or deactivates a GameObject found by hierarchy path or name.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    active: z.boolean(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'set_active', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    value: String(params.active),
    includeInactive: params.includeInactive ?? true,
  })));

  server.tool('unity_set_label_text', 'Sets the text of an NGUI UILabel on a GameObject found by hierarchy path or name.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    value: z.string(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'set_label_text', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    value: params.value,
    includeInactive: params.includeInactive ?? false,
  })));

  server.tool('unity_set_input_text', 'Sets the value of an NGUI UIInput on a GameObject found by hierarchy path or name.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    value: z.string(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'set_input_text', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    value: params.value,
    includeInactive: params.includeInactive ?? false,
  })));

  server.tool('unity_set_sprite', 'Sets the spriteName of an NGUI UISprite on a GameObject found by hierarchy path or name.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    value: z.string(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'set_sprite', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    value: params.value,
    includeInactive: params.includeInactive ?? false,
  })));

  server.tool('unity_scene_info', 'Reports the active scene, all loaded scenes, and the active scene root GameObjects for QA context.', {
    ...baseConfigShape,
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'scene_info', {})));

  server.tool('unity_get_hierarchy', 'Dumps a depth-limited GameObject tree under a path (or the active scene roots), with active/clickable/label hints.', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    maxDepth: z.number().int().positive().max(20).optional(),
    maxCount: z.number().int().positive().max(2000).optional(),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'get_hierarchy', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    maxDepth: params.maxDepth,
    maxCount: params.maxCount,
    includeInactive: params.includeInactive ?? false,
  })));

  server.tool('unity_get_component', 'Reads simple-typed public fields/properties of a named component on a GameObject (QA state inspection, e.g. script values).', {
    ...baseConfigShape,
    targetPath: z.string().optional(),
    targetName: z.string().optional(),
    componentName: z.string().min(1),
    includeInactive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'get_component', {
    targetPath: params.targetPath,
    targetName: params.targetName,
    componentName: params.componentName,
    includeInactive: params.includeInactive ?? false,
  })));

  // --- Editor tool QA tools ---------------------------------------------------------------
  // Menu items are covered by unity_editor_menu_execute, which fails loudly on an
  // unknown path, and unity_editor_menu_list, which discovers the exact path.

  server.tool('unity_invoke_static_method', 'Reflection-invokes a public or non-public static C# method with string arguments (converted to each parameter type). The key tool for QAing editor tools headlessly: call the underlying logic directly instead of clicking a modal dialog. Example: typeName "MyTool.EditorBuild", methodName "Run", methodArgs ["2"].', {
    ...baseConfigShape,
    typeName: z.string().min(1),
    methodName: z.string().min(1),
    methodArgs: z.array(z.string()).optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'invoke_static_method', {
    typeName: params.typeName,
    methodName: params.methodName,
    methodArgs: params.methodArgs ?? [],
  })));

  // Editor windows are covered by unity_editor_window_list (which also reports the
  // instance id every other editor tool targets by) and unity_editor_window_dump.
  // Window pixels are covered by unity_editor_window_capture, which asks Windows for
  // the window's own composition surface instead of reading a desktop screen region,
  // so it works for floating windows, survives occlusion, and reports which backend ran.
  // EditorPrefs are covered by unity_editor_prefs_get / unity_editor_prefs_set, which
  // reach PlayerPrefs as well.

  server.tool('unity_get_asset_guid', 'Returns the GUID and main asset type for a project-relative asset path (e.g. "Assets/Foo.prefab"). Empty guid means the asset does not exist.', {
    ...baseConfigShape,
    assetPath: z.string().min(1),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'get_asset_guid', {
    assetPath: params.assetPath,
  })));

  server.tool('unity_import_asset', 'Reimports a project asset (AssetDatabase.ImportAsset). Use to force Unity to pick up a file an editor tool wrote. Optionally force-update and/or recurse into a folder.', {
    ...baseConfigShape,
    assetPath: z.string().min(1),
    forceUpdate: z.boolean().optional(),
    recursive: z.boolean().optional(),
    timeoutMs: timeoutSchema,
  }, async (params) => toToolResult(await unitySimpleCommand(params, 'import_asset', {
    assetPath: params.assetPath,
    forceUpdate: params.forceUpdate ?? false,
    importRecursive: params.recursive ?? false,
  })));

  server.tool('unity_assert_path_exists', 'Checks whether a file or directory exists on disk and reports its size (pure filesystem check, no Unity round-trip). Use to verify build outputs an editor tool produced.', {
    path: z.string().min(1),
    expectExists: z.boolean().optional(),
  }, async (params) => toToolResult(await unityAssertPathExists(params)));

  server.tool('unity_read_text_file', 'Reads a UTF-8 text file from disk (pure filesystem, no Unity). Reads the tail when the file exceeds maxBytes. Use to inspect logs/manifests an editor tool produced.', {
    path: z.string().min(1),
    maxBytes: z.number().int().positive().max(5 * 1024 * 1024).optional(),
  }, async (params) => toToolResult(await unityReadTextFile(params)));
  // Editor-tool automation and the in-editor test runner: the tools above drive the running game,
  // these drive the editor itself.
  registerEditorTools(server);
}

async function unityStatus(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const processes = await listUnityRelatedProcesses();
  const staleCandidates = findStaleCandidates(processes, config.projectPath);
  const mcpSettingsPath = join(config.projectPath, 'ProjectSettings', 'McpUnitySettings.json');
  const mcpSettings = await readMcpSettings(mcpSettingsPath);
  const portOpen = mcpSettings?.Port ? await isPortOpen('127.0.0.1', mcpSettings.Port, 500) : undefined;
  const editorLogPath = defaultEditorLogPath();

  return {
    success: true,
    unityPath: config.unityPath,
    unityExists: await pathExists(config.unityPath),
    projectPath: config.projectPath,
    projectExists: await pathExists(config.projectPath),
    commandRoot: config.commandRoot,
    commandRootExists: await pathExists(config.commandRoot),
    lockfilePath: join(config.projectPath, 'Temp', 'UnityLockfile'),
    lockfileExists: await pathExists(join(config.projectPath, 'Temp', 'UnityLockfile')),
    editorInstancePath: join(config.projectPath, 'Library', 'EditorInstance.json'),
    editorInstanceExists: await pathExists(join(config.projectPath, 'Library', 'EditorInstance.json')),
    editorLogPath,
    editorLogExists: await pathExists(editorLogPath),
    legacyMcpUnity: {
      settingsPath: mcpSettingsPath,
      settingsExists: await pathExists(mcpSettingsPath),
      port: mcpSettings?.Port,
      portOpen,
    },
    processes,
    staleCandidates,
  };
}

async function unityLaunch(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const pid = await launchUnity(config.unityPath, config.projectPath, params.extraArgs ?? []);
  return {
    success: pid !== undefined,
    pid,
    unityPath: config.unityPath,
    projectPath: config.projectPath,
  };
}

async function unityRunTests(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return runUnityTests({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    mode: params.mode,
    testFilter: params.testFilter,
    timeoutMs: params.timeoutMs ?? 3 * 60 * 1000,
  });
}

async function unityReadEditorLog(params: any): Promise<unknown> {
  const logPath = params.logPath || defaultEditorLogPath();
  if (!await pathExists(logPath)) {
    return {
      success: false,
      logPath,
      error: `Editor log not found: ${logPath}`,
    };
  }

  const text = await readTail(logPath, params.maxBytes ?? 512 * 1024);
  return {
    success: true,
    logPath,
    summary: summarizeEditorLog(text, params.maxLines ?? 200),
  };
}

async function unityExecuteEditorCommand(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command: params.command,
    parameters: params.parameters,
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: params.runOnce ?? false,
  });
}

async function unityCaptureScreenshot(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const outputPath = params.outputPath || join(config.commandRoot, 'screenshots', `screenshot-${Date.now()}.png`);
  const response = await executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command: 'capture_screenshot',
    parameters: {
      outputPath,
      cameraName: params.cameraName,
      width: params.width ?? 1280,
      height: params.height ?? 720,
    },
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: params.runOnce ?? false,
  });
  const bytes = await fileSize(outputPath);

  return verifyScreenshotResponse({
    response,
    outputPath,
    pngBytes: bytes,
    requestedWidth: params.width ?? 1280,
    requestedHeight: params.height ?? 720,
    requireRequestedSize: params.requireRequestedSize ?? false,
  });
}

async function unityClickUiText(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command: 'click_ui_text',
    parameters: {
      text: params.text,
      includeInactive: params.includeInactive ?? false,
    },
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: params.runOnce ?? false,
  });
}

async function unityClickUiTextAndWait(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return clickUiTextAndWait({
    clickText: params.clickText,
    waitText: params.waitText,
    exact: params.exact ?? false,
    includeInactive: params.includeInactive ?? false,
    timeoutMs: params.timeoutMs ?? 3000,
    pollIntervalMs: params.pollIntervalMs ?? 500,
    commandTimeoutMs: Math.min(params.timeoutMs ?? 1500, 1500),
    execute: (command, parameters, timeoutMs) => executeEditorCommand({
      unityPath: config.unityPath,
      projectPath: config.projectPath,
      commandRoot: config.commandRoot,
      command,
      parameters,
      timeoutMs,
      runOnce: false,
    }),
  });
}

async function unityRunUiTextQaFlow(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const outputRoot = params.outputRoot || join(config.projectPath, '.qa', `nx3-mcp-flow-${Date.now()}`, 'screenshots');
  return runUiTextQaFlow({
    initialText: params.initialText,
    clickText: params.clickText,
    expectedText: params.expectedText,
    outputRoot,
    exact: params.exact ?? true,
    includeInactive: params.includeInactive ?? false,
    timeoutMs: params.timeoutMs ?? 9000,
    pollIntervalMs: params.pollIntervalMs ?? 500,
    commandTimeoutMs: Math.min(params.timeoutMs ?? 1500, 1500),
    width: params.width ?? 1280,
    height: params.height ?? 720,
    requireRequestedSize: params.requireRequestedSize ?? false,
    exitPlayMode: params.exitPlayMode ?? true,
    execute: (command, parameters, timeoutMs) => executeEditorCommand({
      unityPath: config.unityPath,
      projectPath: config.projectPath,
      commandRoot: config.commandRoot,
      command,
      parameters,
      timeoutMs,
      runOnce: false,
    }),
  });
}

async function unityWaitUiText(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return waitForUiText({
    text: params.text,
    exact: params.exact ?? false,
    includeInactive: params.includeInactive ?? false,
    timeoutMs: params.timeoutMs ?? 3000,
    pollIntervalMs: params.pollIntervalMs ?? 500,
    commandTimeoutMs: Math.min(params.timeoutMs ?? 1500, 1500),
    execute: (command, parameters, timeoutMs) => executeEditorCommand({
      unityPath: config.unityPath,
      projectPath: config.projectPath,
      commandRoot: config.commandRoot,
      command,
      parameters,
      timeoutMs,
      runOnce: false,
    }),
  });
}

async function unityWaitThenClick(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return waitThenClick({
    text: params.text,
    exact: params.exact ?? false,
    includeInactive: params.includeInactive ?? false,
    timeoutMs: params.timeoutMs ?? 3000,
    pollIntervalMs: params.pollIntervalMs ?? 500,
    commandTimeoutMs: Math.min(params.timeoutMs ?? 1500, 1500),
    execute: (command, parameters, timeoutMs) => executeEditorCommand({
      unityPath: config.unityPath,
      projectPath: config.projectPath,
      commandRoot: config.commandRoot,
      command,
      parameters,
      timeoutMs,
      runOnce: false,
    }),
  });
}

async function unitySetPlayMode(params: any, targetPlaying: boolean): Promise<unknown> {
  const config = resolveProjectConfig(params);
  return setPlayModeAndWait({
    targetPlaying,
    timeoutMs: params.timeoutMs ?? 6000,
    pollIntervalMs: params.pollIntervalMs ?? 500,
    commandTimeoutMs: Math.min(params.timeoutMs ?? 1500, 1500),
    execute: (command, parameters, timeoutMs) => executeEditorCommand({
      unityPath: config.unityPath,
      projectPath: config.projectPath,
      commandRoot: config.commandRoot,
      command,
      parameters,
      timeoutMs,
      runOnce: false,
    }),
  });
}

async function unityStartFrameCapture(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const framesDir = params.framesDir || join(config.commandRoot, 'recordings', `rec-${Date.now()}`);
  const response = await executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command: 'start_frame_capture',
    parameters: {
      framesDir,
      cameraName: params.cameraName,
      width: params.width ?? 1280,
      height: params.height ?? 720,
      captureEveryNthUpdate: params.captureEveryNthUpdate ?? 1,
      maxFrames: params.maxFrames ?? 600,
      maxDurationSeconds: params.maxDurationSeconds ?? 30,
    },
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: params.runOnce ?? false,
  });

  return {
    ...response,
    outputs: {
      ...normalizeOutputs(response.outputs),
      framesDir,
    },
  };
}

async function unityStopFrameCapture(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const response = await executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command: 'stop_frame_capture',
    parameters: {
      framesDir: params.framesDir,
    },
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: params.runOnce ?? false,
  });

  const outputs = normalizeOutputs(response.outputs);
  const framesDir = (outputs.framesDir as string) || params.framesDir;
  const frameCount = Number(outputs.frameCount ?? 0);

  return {
    ...response,
    success: response.success && frameCount > 0,
    outputs: {
      ...outputs,
      framesDir,
      framesDirExists: framesDir ? await pathExists(framesDir) : false,
    },
  };
}

async function unityKillStale(params: any): Promise<unknown> {
  const config = resolveProjectConfig(params);
  const processes = await listUnityRelatedProcesses();
  const candidates = findStaleCandidates(processes, config.projectPath)
    .filter((candidate) => params.includeUnity || candidate.name.toLowerCase() !== 'unity.exe');

  if (!params.kill) {
    return {
      success: true,
      killed: [],
      candidates,
      note: 'Set kill=true to terminate killable stale candidates.',
    };
  }

  const killed: number[] = [];
  for (const candidate of candidates) {
    if (!candidate.killableByDefault && !params.includeUnity) {
      continue;
    }
    await killProcess(candidate.pid);
    killed.push(candidate.pid);
  }

  return {
    success: true,
    killed,
    candidates,
  };
}

async function unityAssertPathExists(params: any): Promise<unknown> {
  const exists = await pathExists(params.path);
  const bytes = exists ? await fileSize(params.path) : 0;
  const expectExists = params.expectExists ?? true;
  return {
    success: exists === expectExists,
    path: params.path,
    exists,
    sizeBytes: bytes,
    expectExists,
  };
}

async function unityReadTextFile(params: any): Promise<unknown> {
  if (!await pathExists(params.path)) {
    return {
      success: false,
      path: params.path,
      error: `File not found: ${params.path}`,
    };
  }

  const maxBytes = params.maxBytes ?? 256 * 1024;
  const size = await fileSize(params.path);
  const truncated = size > maxBytes;
  const text = truncated ? await readTail(params.path, maxBytes) : await readFile(params.path, 'utf8');
  return {
    success: true,
    path: params.path,
    sizeBytes: size,
    truncated,
    text,
  };
}

async function unitySimpleCommand(params: any, command: string, parameters: Record<string, unknown>): Promise<unknown> {
  const config = resolveProjectConfig(params);
  // Drop undefined values so JsonUtility on the C# side keeps its defaults.
  const cleaned = Object.fromEntries(Object.entries(parameters).filter(([, value]) => value !== undefined));
  return executeEditorCommand({
    unityPath: config.unityPath,
    projectPath: config.projectPath,
    commandRoot: config.commandRoot,
    command,
    parameters: cleaned,
    timeoutMs: params.timeoutMs ?? 1500,
    runOnce: false,
  });
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

function defaultEditorLogPath(): string {
  const localAppData = process.env.LOCALAPPDATA || '';
  return join(localAppData, 'Unity', 'Editor', 'Editor.log');
}

async function readMcpSettings(filePath: string): Promise<{ Port?: number } | undefined> {
  try {
    return JSON.parse(await readFile(filePath, 'utf8')) as { Port?: number };
  } catch {
    return undefined;
  }
}

function isPortOpen(host: string, port: number, timeoutMs: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection({ host, port });
    const done = (result: boolean) => {
      socket.destroy();
      resolve(result);
    };
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => done(true));
    socket.once('timeout', () => done(false));
    socket.once('error', () => done(false));
  });
}
