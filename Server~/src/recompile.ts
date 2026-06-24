import { EditorCommandResponse } from './commandBridge.js';
import { normalizeOutputs, readBooleanOutput } from './playMode.js';

export interface RecompileOptions {
  refresh?: boolean;
  timeoutMs: number;
  pollIntervalMs?: number;
  commandTimeoutMs?: number;
  settleConfirm?: number;
  execute: (command: string, parameters: Record<string, unknown>, timeoutMs: number) => Promise<EditorCommandResponse>;
  delay?: (ms: number) => Promise<void>;
  now?: () => number;
}

export interface RecompileResult {
  success: boolean;
  command: string;
  triggered: boolean;
  sawCompiling: boolean;
  isCompiling: boolean | undefined;
  isUpdating: boolean | undefined;
  compileErrorCount: number;
  compileErrors: string[];
  elapsedMs: number;
  polls: number;
  timedOut: boolean;
  triggerResponse: EditorCommandResponse;
  lastStatus?: EditorCommandResponse;
  error?: {
    message: string;
  };
}

// Requests a script recompile (or asset refresh), then polls compile_status until the
// editor settles. The domain reload that recompilation triggers takes the file bridge
// down briefly, so a failed/timed-out status poll is treated as "still busy", not an
// error — completion is only declared once the bridge answers again with idle status.
export async function recompileAndWait(options: RecompileOptions): Promise<RecompileResult> {
  const now = options.now ?? (() => Date.now());
  const delay = options.delay ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const pollIntervalMs = options.pollIntervalMs ?? 750;
  const commandTimeoutMs = options.commandTimeoutMs ?? 15000;
  const settleConfirm = options.settleConfirm ?? 2;
  const command = options.refresh ? 'refresh_assets' : 'recompile_scripts';
  const startedAt = now();

  const triggerResponse = await options.execute(command, {}, commandTimeoutMs);
  if (!triggerResponse.success) {
    return {
      success: false,
      command,
      triggered: false,
      sawCompiling: false,
      isCompiling: undefined,
      isUpdating: undefined,
      compileErrorCount: 0,
      compileErrors: [],
      elapsedMs: now() - startedAt,
      polls: 0,
      timedOut: false,
      triggerResponse,
      error: {
        message: triggerResponse.error?.message ?? `${command} failed`,
      },
    };
  }

  let polls = 0;
  let sawBusy = false;
  let idleConfirms = 0;
  let lastStatus: EditorCommandResponse | undefined;

  // Give compilation a tick to actually begin before sampling.
  await delay(pollIntervalMs);

  while (now() - startedAt < options.timeoutMs) {
    const status = await options.execute('compile_status', {}, commandTimeoutMs);
    polls += 1;

    if (!status.success) {
      // Bridge is most likely mid-reload; that itself proves compilation is happening.
      sawBusy = true;
      idleConfirms = 0;
      await delay(pollIntervalMs);
      continue;
    }

    lastStatus = status;
    const isCompiling = readBooleanOutput(status.outputs, 'isCompiling');
    const isUpdating = readBooleanOutput(status.outputs, 'isUpdating');
    const busy = isCompiling === true || isUpdating === true;

    if (busy) {
      sawBusy = true;
      idleConfirms = 0;
      await delay(pollIntervalMs);
      continue;
    }

    idleConfirms += 1;
    // Done when we have seen the editor go busy and then return idle, or when it has
    // stayed idle long enough to conclude there was nothing to compile.
    if (sawBusy || idleConfirms >= settleConfirm) {
      const errors = readCompileErrors(status.outputs);
      return {
        success: true,
        command,
        triggered: true,
        sawCompiling: sawBusy,
        isCompiling,
        isUpdating,
        compileErrorCount: errors.length,
        compileErrors: errors,
        elapsedMs: now() - startedAt,
        polls,
        timedOut: false,
        triggerResponse,
        lastStatus,
      };
    }

    await delay(pollIntervalMs);
  }

  const errors = lastStatus ? readCompileErrors(lastStatus.outputs) : [];
  return {
    success: false,
    command,
    triggered: true,
    sawCompiling: sawBusy,
    isCompiling: lastStatus ? readBooleanOutput(lastStatus.outputs, 'isCompiling') : undefined,
    isUpdating: lastStatus ? readBooleanOutput(lastStatus.outputs, 'isUpdating') : undefined,
    compileErrorCount: errors.length,
    compileErrors: errors,
    elapsedMs: now() - startedAt,
    polls,
    timedOut: true,
    triggerResponse,
    lastStatus,
    error: {
      message: `Timed out waiting for compilation to settle after ${options.timeoutMs}ms`,
    },
  };
}

export function readCompileErrors(outputs: unknown): string[] {
  const map = normalizeOutputs(outputs);
  const raw = map['compileErrors'];
  if (typeof raw !== 'string' || raw.length === 0) {
    return [];
  }

  return raw.split('\n').map((line) => line.trim()).filter((line) => line.length > 0);
}
