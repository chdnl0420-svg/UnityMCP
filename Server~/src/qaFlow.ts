import { join } from 'node:path';
import { EditorCommandResponse } from './commandBridge.js';
import { PlayModeWaitResult, normalizeOutputs, setPlayModeAndWait } from './playMode.js';
import { ClickUiTextAndWaitResult, WaitUiTextResult, clickUiTextAndWait, waitForUiText } from './uiText.js';
import { fileSize } from './utils/files.js';

export interface UiTextQaFlowOptions {
  initialText: string;
  clickText: string;
  expectedText: string;
  outputRoot: string;
  exact?: boolean;
  includeInactive?: boolean;
  timeoutMs: number;
  pollIntervalMs?: number;
  commandTimeoutMs?: number;
  width?: number;
  height?: number;
  exitPlayMode?: boolean;
  execute: (command: string, parameters: Record<string, unknown>, timeoutMs: number) => Promise<EditorCommandResponse>;
  delay?: (ms: number) => Promise<void>;
  now?: () => number;
  getFileSize?: (filePath: string) => Promise<number>;
}

export interface QaFlowStep {
  name: string;
  success: boolean;
  elapsedMs: number;
  detail?: string;
}

export interface UiTextQaFlowResult {
  success: boolean;
  elapsedMs: number;
  steps: QaFlowStep[];
  screenshots: {
    before: string;
    after: string;
  };
  enter?: PlayModeWaitResult;
  initialWait?: WaitUiTextResult;
  action?: ClickUiTextAndWaitResult;
  beforeCapture?: EditorCommandResponse;
  afterCapture?: EditorCommandResponse;
  exit?: PlayModeWaitResult;
  error?: {
    message: string;
  };
}

export async function runUiTextQaFlow(options: UiTextQaFlowOptions): Promise<UiTextQaFlowResult> {
  const now = options.now ?? (() => Date.now());
  const pollIntervalMs = options.pollIntervalMs ?? 500;
  const commandTimeoutMs = options.commandTimeoutMs ?? 15000;
  const exact = options.exact ?? true;
  const getFileSize = options.getFileSize ?? fileSize;
  const startedAt = now();
  const steps: QaFlowStep[] = [];
  const beforePath = join(options.outputRoot, 'before.png');
  const afterPath = join(options.outputRoot, 'after.png');

  const result: UiTextQaFlowResult = {
    success: false,
    elapsedMs: 0,
    steps,
    screenshots: {
      before: beforePath,
      after: afterPath,
    },
  };

  const enter = await recordStep(steps, 'enter_play_mode', now, async () => {
    return setPlayModeAndWait({
      targetPlaying: true,
      timeoutMs: remainingMs(options.timeoutMs, startedAt, now),
      pollIntervalMs,
      commandTimeoutMs,
      execute: options.execute,
      delay: options.delay,
      now,
    });
  }, (value) => value.isPlaying === true ? 'isPlaying=true' : undefined);
  result.enter = enter;
  if (!enter.success) {
    return finishFailure(result, startedAt, now, enter.error?.message ?? 'Failed to enter PlayMode');
  }

  const initialWait = await recordStep(steps, 'wait_initial_text', now, async () => {
    return waitForUiText({
      text: options.initialText,
      exact,
      includeInactive: options.includeInactive,
      timeoutMs: remainingMs(options.timeoutMs, startedAt, now),
      pollIntervalMs,
      commandTimeoutMs,
      execute: options.execute,
      delay: options.delay,
      now,
    });
  }, (value) => value.matchedLine);
  result.initialWait = initialWait;
  if (!initialWait.success) {
    return finishFailure(result, startedAt, now, initialWait.error?.message ?? `Initial text not found: ${options.initialText}`);
  }

  const beforeCapture = await recordStep(steps, 'capture_before', now, async () => {
    const response = await options.execute('capture_screenshot', {
      outputPath: beforePath,
      width: options.width ?? 1280,
      height: options.height ?? 720,
    }, commandTimeoutMs);
    return verifyPngCapture(response, beforePath, getFileSize);
  }, () => beforePath);
  result.beforeCapture = beforeCapture;
  if (!beforeCapture.success) {
    return finishFailure(result, startedAt, now, beforeCapture.error?.message ?? 'Failed to capture before screenshot');
  }

  const action = await recordStep(steps, 'click_and_wait', now, async () => {
    return clickUiTextAndWait({
      clickText: options.clickText,
      waitText: options.expectedText,
      exact,
      includeInactive: options.includeInactive,
      timeoutMs: remainingMs(options.timeoutMs, startedAt, now),
      pollIntervalMs,
      commandTimeoutMs,
      execute: options.execute,
      delay: options.delay,
      now,
    });
  }, (value) => value.matchedLine);
  result.action = action;
  if (!action.success) {
    return finishFailure(result, startedAt, now, action.error?.message ?? `Expected text not found: ${options.expectedText}`);
  }

  const afterCapture = await recordStep(steps, 'capture_after', now, async () => {
    const response = await options.execute('capture_screenshot', {
      outputPath: afterPath,
      width: options.width ?? 1280,
      height: options.height ?? 720,
    }, commandTimeoutMs);
    return verifyPngCapture(response, afterPath, getFileSize);
  }, () => afterPath);
  result.afterCapture = afterCapture;
  if (!afterCapture.success) {
    return finishFailure(result, startedAt, now, afterCapture.error?.message ?? 'Failed to capture after screenshot');
  }

  if (options.exitPlayMode ?? true) {
    const exit = await recordStep(steps, 'exit_play_mode', now, async () => {
      return setPlayModeAndWait({
        targetPlaying: false,
        timeoutMs: remainingMs(options.timeoutMs, startedAt, now),
        pollIntervalMs,
        commandTimeoutMs,
        execute: options.execute,
        delay: options.delay,
        now,
      });
    }, (value) => value.isPlaying === false ? 'isPlaying=false' : undefined);
    result.exit = exit;
    if (!exit.success) {
      return finishFailure(result, startedAt, now, exit.error?.message ?? 'Failed to exit PlayMode');
    }
  }

  result.success = true;
  result.elapsedMs = now() - startedAt;
  return result;
}

async function verifyPngCapture(
  response: EditorCommandResponse,
  outputPath: string,
  getFileSize: (filePath: string) => Promise<number>,
): Promise<EditorCommandResponse> {
  const pngBytes = await getFileSize(outputPath);
  return {
    ...response,
    success: response.success && pngBytes > 0,
    outputs: {
      ...normalizeOutputs(response.outputs),
      outputPath,
      pngExists: pngBytes > 0,
      pngBytes,
    },
    error: response.success && pngBytes <= 0
      ? { message: `Screenshot PNG was not created or is empty: ${outputPath}` }
      : response.error,
  };
}

async function recordStep<T extends { success: boolean }>(
  steps: QaFlowStep[],
  name: string,
  now: () => number,
  action: () => Promise<T>,
  detail?: (value: T) => string | undefined,
): Promise<T> {
  const startedAt = now();
  const value = await action();
  steps.push({
    name,
    success: value.success,
    elapsedMs: now() - startedAt,
    detail: detail?.(value),
  });
  return value;
}

function remainingMs(timeoutMs: number, startedAt: number, now: () => number): number {
  return Math.max(1, timeoutMs - (now() - startedAt));
}

function finishFailure(
  result: UiTextQaFlowResult,
  startedAt: number,
  now: () => number,
  message: string,
): UiTextQaFlowResult {
  result.success = false;
  result.elapsedMs = now() - startedAt;
  result.error = {
    message,
  };
  return result;
}
