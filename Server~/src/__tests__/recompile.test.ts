import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { readCompileErrors, recompileAndWait } from '../recompile.js';

test('recompileAndWait waits for busy->idle and reports no errors on a clean compile', async () => {
  const calls: string[] = [];
  const statuses = [
    [{ key: 'isCompiling', value: 'True' }, { key: 'isUpdating', value: 'False' }],
    [{ key: 'isCompiling', value: 'False' }, { key: 'isUpdating', value: 'False' }, { key: 'compileErrorCount', value: '0' }],
  ];

  const result = await recompileAndWait({
    timeoutMs: 60000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => {
      calls.push(command);
      if (command === 'recompile_scripts') {
        return response(command, true, [{ key: 'requestedRecompile', value: 'true' }]);
      }
      return response(command, true, statuses.shift() ?? []);
    },
  });

  assert.equal(result.success, true);
  assert.equal(result.command, 'recompile_scripts');
  assert.equal(result.sawCompiling, true);
  assert.equal(result.compileErrorCount, 0);
  assert.deepEqual(calls, ['recompile_scripts', 'compile_status', 'compile_status']);
});

test('recompileAndWait treats a failed poll as mid-reload busy and then parses compile errors', async () => {
  const polls: Array<EditorCommandResponse> = [
    response('compile_status', false, []),
    response('compile_status', true, [
      { key: 'isCompiling', value: 'False' },
      { key: 'isUpdating', value: 'False' },
      { key: 'compileErrorCount', value: '2' },
      { key: 'compileErrors', value: 'A.cs(1,2): err1\nB.cs(3,4): err2' },
    ]),
  ];

  const result = await recompileAndWait({
    timeoutMs: 60000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => {
      if (command === 'recompile_scripts') {
        return response(command, true, []);
      }
      return polls.shift() ?? response('compile_status', true, []);
    },
  });

  assert.equal(result.success, true);
  assert.equal(result.sawCompiling, true);
  assert.equal(result.compileErrorCount, 2);
  assert.deepEqual(result.compileErrors, ['A.cs(1,2): err1', 'B.cs(3,4): err2']);
});

test('recompileAndWait fails fast when the trigger command is rejected (e.g. PlayMode)', async () => {
  const result = await recompileAndWait({
    timeoutMs: 60000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => response(command, false, [], 'Cannot recompile scripts while in PlayMode.'),
  });

  assert.equal(result.success, false);
  assert.equal(result.triggered, false);
  assert.match(result.error?.message ?? '', /PlayMode/);
});

test('recompileAndWait uses refresh_assets when refresh=true', async () => {
  const calls: string[] = [];
  const result = await recompileAndWait({
    refresh: true,
    timeoutMs: 60000,
    pollIntervalMs: 1,
    settleConfirm: 2,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => {
      calls.push(command);
      if (command === 'refresh_assets') {
        return response(command, true, []);
      }
      return response(command, true, [
        { key: 'isCompiling', value: 'False' },
        { key: 'isUpdating', value: 'False' },
      ]);
    },
  });

  assert.equal(result.command, 'refresh_assets');
  assert.equal(result.success, true);
  // Idle from the start: needs settleConfirm consecutive idle reads.
  assert.equal(calls[0], 'refresh_assets');
  assert.equal(result.sawCompiling, false);
});

test('readCompileErrors splits newline-joined errors and ignores empty', () => {
  assert.deepEqual(readCompileErrors([{ key: 'compileErrors', value: 'x\ny' }]), ['x', 'y']);
  assert.deepEqual(readCompileErrors([{ key: 'compileErrorCount', value: '0' }]), []);
  assert.deepEqual(readCompileErrors({}), []);
});

function response(command: string, success: boolean, outputs: unknown, errorMessage = ''): EditorCommandResponse {
  return {
    id: command,
    command,
    success,
    elapsedMs: 0,
    logs: [],
    outputs,
    error: {
      message: errorMessage,
    },
  };
}
