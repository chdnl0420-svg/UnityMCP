import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { MAX_TIMEOUT_MS, clampTimeout } from '../timeouts.js';
import { waitForUiText } from '../uiText.js';
import { setPlayModeAndWait } from '../playMode.js';

test('clampTimeout trims anything above the ceiling and leaves shorter waits alone', () => {
  // Arrange / Act / Assert
  assert.equal(clampTimeout(300000), MAX_TIMEOUT_MS);
  assert.equal(clampTimeout(30 * 60 * 1000), MAX_TIMEOUT_MS);
  assert.equal(clampTimeout(1500), 1500);
  assert.equal(MAX_TIMEOUT_MS, 60000);
});

test('waitForUiText stops at the ceiling even when asked to wait five minutes', async () => {
  // Arrange - a fake clock that only advances when the loop sleeps.
  let clock = 0;
  let polls = 0;

  // Act
  const result = await waitForUiText({
    text: 'NEVER APPEARS',
    timeoutMs: 300000,
    pollIntervalMs: 1000,
    now: () => clock,
    delay: async (ms) => { clock += ms; },
    execute: async (command) => {
      polls += 1;
      return response(command, true, [{ key: 'ui', value: 'GUIRoot/Other\tlabel\ttext=SOMETHING ELSE' }]);
    },
  });

  // Assert - 60 polls of 1s, not 300.
  assert.equal(result.success, false);
  assert.equal(result.elapsedMs, MAX_TIMEOUT_MS);
  assert.equal(polls, 60);
});

test('setPlayModeAndWait stops at the ceiling even when asked to wait five minutes', async () => {
  // Arrange
  let clock = 0;

  // Act
  const result = await setPlayModeAndWait({
    targetPlaying: true,
    timeoutMs: 300000,
    pollIntervalMs: 1000,
    now: () => clock,
    delay: async (ms) => { clock += ms; },
    execute: async (command) => response(command, true, [{ key: 'isPlaying', value: 'false' }]),
  });

  // Assert
  assert.equal(result.success, false);
  assert.equal(result.elapsedMs, MAX_TIMEOUT_MS);
  assert.equal(result.polls, 60);
});

function response(command: string, success: boolean, outputs: unknown): EditorCommandResponse {
  return {
    id: 'test',
    command,
    success,
    elapsedMs: 1,
    logs: [],
    outputs,
  };
}
