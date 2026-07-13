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
    timeoutMs: timeoutSchema,
    runOnce: z.boolean().optional(),
  }, async (params) => toToolResult(await unityCaptureScreenshot(params)));

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
    timeoutMs: params.timeoutMs ?? 30 * 60 * 1000,
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
    timeoutMs: params.timeoutMs ?? 15000,
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
    timeoutMs: params.timeoutMs ?? 15000,
    runOnce: params.runOnce ?? false,
  });
  const bytes = await fileSize(outputPath);

  return {
    ...response,
    success: response.success && bytes > 0,
    outputs: {
      ...normalizeOutputs(response.outputs),
      outputPath,
      pngExists: await pathExists(outputPath),
      pngBytes: bytes,
    },
  };
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
    timeoutMs: params.timeoutMs ?? 15000,
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
    timeoutMs: params.timeoutMs ?? 15000,
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

function normalizeOutputs(outputs: unknown): Record<string, unknown> {
  if (Array.isArray(outputs)) {
    return outputs.reduce<Record<string, unknown>>((acc, item) => {
      if (item && typeof item === 'object' && 'key' in item && 'value' in item) {
        const entry = item as { key?: unknown; value?: unknown };
        if (typeof entry.key === 'string') {
          acc[entry.key] = entry.value;
        }
      }
      return acc;
    }, {});
  }

  if (outputs && typeof outputs === 'object') {
    return outputs as Record<string, unknown>;
  }

  return {};
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
