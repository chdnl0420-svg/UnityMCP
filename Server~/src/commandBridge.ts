import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileSize, pathExists, readJsonFile, writeJsonAtomic } from './utils/files.js';
import { runUnityExecuteMethod } from './unityCli.js';
import { clampTimeout } from './timeouts.js';

export interface EditorCommandRequest {
  id: string;
  command: string;
  createdAtUtc: string;
  parameters: Record<string, unknown>;
}

export interface EditorCommandResponse {
  id: string;
  command: string;
  success: boolean;
  elapsedMs: number;
  logs: string[];
  outputs: unknown;
  /** The wait this call actually ran with. Fixed by the bridge; a caller cannot set it. */
  timeoutUsedMs?: number;
  error?: {
    message: string;
    details?: string;
  };
}

export interface ExecuteEditorCommandOptions {
  unityPath: string;
  projectPath: string;
  commandRoot: string;
  command: string;
  parameters?: Record<string, unknown>;
  timeoutMs: number;
  runOnce?: boolean;
}

export async function executeEditorCommand(options: ExecuteEditorCommandOptions): Promise<EditorCommandResponse> {
  // Last gate before anything actually waits: every single bridge command funnels through here.
  const timeoutMs = clampTimeout(options.timeoutMs);
  const id = `${Date.now()}-${randomUUID()}`;
  const requestsDir = join(options.commandRoot, 'requests');
  const responsesDir = join(options.commandRoot, 'responses');
  const logsDir = join(options.commandRoot, 'logs');
  await Promise.all([
    mkdir(requestsDir, { recursive: true }),
    mkdir(responsesDir, { recursive: true }),
    mkdir(logsDir, { recursive: true }),
  ]);

  const requestPath = join(requestsDir, `${id}.json`);
  const responsePath = join(responsesDir, `${id}.json`);
  const request: EditorCommandRequest = {
    id,
    command: options.command,
    createdAtUtc: new Date().toISOString(),
    parameters: options.parameters ?? {},
  };

  await writeJsonAtomic(requestPath, request);

  let runOncePromise: Promise<unknown> | undefined;
  if (options.runOnce) {
    const logPath = join(logsDir, `run-once-${id}.log`);
    runOncePromise = runUnityExecuteMethod(
      options.unityPath,
      options.projectPath,
      'ProjectMQaMcp.Editor.CommandRunner.RunOnce',
      logPath,
      timeoutMs,
    );
  }

  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    if (await pathExists(responsePath)) {
      const response = await readJsonFile<EditorCommandResponse>(responsePath);
      // Stale clients may still send a timeout of their own; reporting the real one shows it was ignored.
      return { ...response, timeoutUsedMs: timeoutMs };
    }
    await delay(250);
  }

  if (runOncePromise) {
    await runOncePromise.catch(() => undefined);
  }

  return {
    id,
    command: options.command,
    success: false,
    elapsedMs: Date.now() - startedAt,
    logs: [],
    outputs: {
      requestPath,
      responsePath,
      responseBytes: await fileSize(responsePath),
    },
    timeoutUsedMs: timeoutMs,
    error: {
      message: `Timed out waiting for Unity response after ${timeoutMs}ms`,
    },
  };
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
