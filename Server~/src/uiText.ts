import { EditorCommandResponse } from './commandBridge.js';
import { normalizeOutputs } from './playMode.js';

export interface WaitUiTextOptions {
  text: string;
  exact?: boolean;
  includeInactive?: boolean;
  timeoutMs: number;
  pollIntervalMs?: number;
  commandTimeoutMs?: number;
  execute: (command: string, parameters: Record<string, unknown>, timeoutMs: number) => Promise<EditorCommandResponse>;
  delay?: (ms: number) => Promise<void>;
  now?: () => number;
}

export interface WaitUiTextResult {
  success: boolean;
  text: string;
  exact: boolean;
  elapsedMs: number;
  polls: number;
  matchedLine?: string;
  lastUi?: string;
  lastResponse?: EditorCommandResponse;
  error?: {
    message: string;
  };
}

export async function waitForUiText(options: WaitUiTextOptions): Promise<WaitUiTextResult> {
  const now = options.now ?? (() => Date.now());
  const delay = options.delay ?? defaultDelay;
  const exact = options.exact ?? false;
  const pollIntervalMs = options.pollIntervalMs ?? 500;
  const commandTimeoutMs = options.commandTimeoutMs ?? 15000;
  const startedAt = now();
  let polls = 0;
  let lastUi = '';
  let lastResponse: EditorCommandResponse | undefined;

  while (now() - startedAt < options.timeoutMs) {
    lastResponse = await options.execute('dump_ui', {
      includeInactive: options.includeInactive ?? false,
    }, commandTimeoutMs);
    polls += 1;

    if (!lastResponse.success) {
      return {
        success: false,
        text: options.text,
        exact,
        elapsedMs: now() - startedAt,
        polls,
        lastResponse,
        error: {
          message: lastResponse.error?.message ?? 'dump_ui failed while waiting for UI text',
        },
      };
    }

    lastUi = String(normalizeOutputs(lastResponse.outputs).ui ?? '');
    const matchedLine = findUiTextLine(lastUi, options.text, exact);
    if (matchedLine) {
      return {
        success: true,
        text: options.text,
        exact,
        elapsedMs: now() - startedAt,
        polls,
        matchedLine,
        lastUi,
        lastResponse,
      };
    }

    await delay(pollIntervalMs);
  }

  return {
    success: false,
    text: options.text,
    exact,
    elapsedMs: now() - startedAt,
    polls,
    lastUi,
    lastResponse,
    error: {
      message: `Timed out waiting for UI text "${options.text}" after ${options.timeoutMs}ms`,
    },
  };
}

export function findUiTextLine(ui: string, text: string, exact: boolean): string | undefined {
  const normalizedNeedle = text.toLowerCase();
  return ui.split(/\r?\n/)
    .find((line) => {
      const value = readLineText(line);
      if (!value) {
        return false;
      }

      const normalizedValue = value.toLowerCase();
      return exact
        ? normalizedValue === normalizedNeedle
        : normalizedValue.includes(normalizedNeedle);
    });
}

function readLineText(line: string): string | undefined {
  const marker = '\ttext=';
  const index = line.indexOf(marker);
  return index >= 0 ? line.slice(index + marker.length) : undefined;
}

function defaultDelay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
