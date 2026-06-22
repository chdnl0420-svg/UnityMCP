import { spawn } from 'node:child_process';
import { mkdir, readFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { parseUnityTestResultsXml, UnityTestResults } from './utils/testResults.js';
import { pathExists } from './utils/files.js';

export interface UnityTestRunOptions {
  unityPath: string;
  projectPath: string;
  commandRoot: string;
  mode: 'EditMode' | 'PlayMode';
  testFilter?: string;
  timeoutMs: number;
}

export interface UnityTestRunResult {
  success: boolean;
  exitCode: number | null;
  timedOut: boolean;
  elapsedMs: number;
  resultsXmlPath: string;
  logPath: string;
  parsed?: UnityTestResults;
  error?: string;
}

export async function launchUnity(unityPath: string, projectPath: string, extraArgs: string[] = []): Promise<number | undefined> {
  const child = spawn(unityPath, ['-projectPath', projectPath, ...extraArgs], {
    detached: true,
    stdio: 'ignore',
    windowsHide: false,
  });
  child.unref();
  return child.pid;
}

export async function runUnityTests(options: UnityTestRunOptions): Promise<UnityTestRunResult> {
  const id = `${Date.now()}-${randomUUID()}`;
  const outputRoot = join(options.commandRoot, 'test-results');
  await mkdir(outputRoot, { recursive: true });

  const resultsXmlPath = join(outputRoot, `${options.mode}-${id}.xml`);
  const logPath = join(outputRoot, `${options.mode}-${id}.log`);
  const args = [
    '-batchmode',
    '-quit',
    '-projectPath',
    options.projectPath,
    '-runTests',
    '-testPlatform',
    options.mode,
    '-testResults',
    resultsXmlPath,
    '-logFile',
    logPath,
  ];

  if (options.testFilter && options.testFilter.trim().length > 0) {
    args.push('-testFilter', options.testFilter);
  }

  const startedAt = Date.now();
  const result = await runProcess(options.unityPath, args, options.timeoutMs);
  const elapsedMs = Date.now() - startedAt;
  let parsed: UnityTestResults | undefined;
  let error = result.error;

  if (await pathExists(resultsXmlPath)) {
    parsed = parseUnityTestResultsXml(await readFile(resultsXmlPath, 'utf8'));
  } else {
    error = error || `Unity did not create ${basename(resultsXmlPath)}`;
  }

  return {
    success: result.exitCode === 0 && !result.timedOut && (parsed?.failed ?? 1) === 0,
    exitCode: result.exitCode,
    timedOut: result.timedOut,
    elapsedMs,
    resultsXmlPath,
    logPath,
    parsed,
    error,
  };
}

export async function runUnityExecuteMethod(
  unityPath: string,
  projectPath: string,
  methodName: string,
  logPath: string,
  timeoutMs: number,
): Promise<{ exitCode: number | null; timedOut: boolean; error?: string }> {
  return runProcess(unityPath, [
    '-batchmode',
    '-quit',
    '-projectPath',
    projectPath,
    '-executeMethod',
    methodName,
    '-logFile',
    logPath,
  ], timeoutMs);
}

function runProcess(
  command: string,
  args: string[],
  timeoutMs: number,
): Promise<{ exitCode: number | null; timedOut: boolean; error?: string }> {
  return new Promise((resolve) => {
    const child = spawn(command, args, {
      stdio: 'ignore',
      windowsHide: true,
    });
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) {
        return;
      }
      settled = true;
      child.kill();
      resolve({ exitCode: null, timedOut: true, error: `Timed out after ${timeoutMs}ms` });
    }, timeoutMs);

    child.on('error', (error) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      resolve({ exitCode: null, timedOut: false, error: error.message });
    });

    child.on('exit', (exitCode) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      resolve({ exitCode, timedOut: false });
    });
  });
}
