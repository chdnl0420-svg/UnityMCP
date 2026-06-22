import { EditorCommandResponse } from './commandBridge.js';

export interface PlayModeWaitOptions {
  targetPlaying: boolean;
  timeoutMs: number;
  pollIntervalMs?: number;
  commandTimeoutMs?: number;
  execute: (command: string, parameters: Record<string, unknown>, timeoutMs: number) => Promise<EditorCommandResponse>;
  delay?: (ms: number) => Promise<void>;
  now?: () => number;
}

export interface PlayModeWaitResult {
  success: boolean;
  targetPlaying: boolean;
  isPlaying: boolean | undefined;
  elapsedMs: number;
  polls: number;
  transitionCommand: string;
  transitionResponse: EditorCommandResponse;
  lastStatus?: EditorCommandResponse;
  error?: {
    message: string;
  };
}

export async function setPlayModeAndWait(options: PlayModeWaitOptions): Promise<PlayModeWaitResult> {
  const now = options.now ?? (() => Date.now());
  const delay = options.delay ?? defaultDelay;
  const pollIntervalMs = options.pollIntervalMs ?? 500;
  const commandTimeoutMs = options.commandTimeoutMs ?? 15000;
  const transitionCommand = options.targetPlaying ? 'enter_play_mode' : 'exit_play_mode';
  const startedAt = now();

  const transitionResponse = await options.execute(transitionCommand, {}, commandTimeoutMs);
  if (!transitionResponse.success) {
    return {
      success: false,
      targetPlaying: options.targetPlaying,
      isPlaying: undefined,
      elapsedMs: now() - startedAt,
      polls: 0,
      transitionCommand,
      transitionResponse,
      error: {
        message: transitionResponse.error?.message ?? `${transitionCommand} failed`,
      },
    };
  }

  let polls = 0;
  let lastStatus: EditorCommandResponse | undefined;
  while (now() - startedAt < options.timeoutMs) {
    lastStatus = await options.execute('editor_status', {}, commandTimeoutMs);
    polls += 1;

    const isPlaying = readBooleanOutput(lastStatus.outputs, 'isPlaying');
    if (isPlaying === options.targetPlaying) {
      return {
        success: true,
        targetPlaying: options.targetPlaying,
        isPlaying,
        elapsedMs: now() - startedAt,
        polls,
        transitionCommand,
        transitionResponse,
        lastStatus,
      };
    }

    await delay(pollIntervalMs);
  }

  return {
    success: false,
    targetPlaying: options.targetPlaying,
    isPlaying: lastStatus ? readBooleanOutput(lastStatus.outputs, 'isPlaying') : undefined,
    elapsedMs: now() - startedAt,
    polls,
    transitionCommand,
    transitionResponse,
    lastStatus,
    error: {
      message: `Timed out waiting for PlayMode=${options.targetPlaying} after ${options.timeoutMs}ms`,
    },
  };
}

export function readBooleanOutput(outputs: unknown, key: string): boolean | undefined {
  const value = normalizeOutputs(outputs)[key];
  if (typeof value === 'boolean') {
    return value;
  }

  if (typeof value === 'string') {
    if (value.toLowerCase() === 'true') {
      return true;
    }

    if (value.toLowerCase() === 'false') {
      return false;
    }
  }

  return undefined;
}

export function normalizeOutputs(outputs: unknown): Record<string, unknown> {
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

function defaultDelay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
