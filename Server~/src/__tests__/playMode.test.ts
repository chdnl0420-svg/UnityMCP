import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { readBooleanOutput, setPlayModeAndWait } from '../playMode.js';

test('setPlayModeAndWait polls editor_status until requested PlayMode state is reached', async () => {
  const calls: string[] = [];
  const statuses = [false, true];

  const result = await setPlayModeAndWait({
    targetPlaying: true,
    timeoutMs: 5000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => {
      calls.push(command);
      if (command === 'enter_play_mode') {
        return response(command, true, [
          { key: 'requestedPlaying', value: 'True' },
          { key: 'isPlayingNow', value: 'False' },
        ]);
      }

      return response(command, true, [
        { key: 'isPlaying', value: String(statuses.shift()) },
      ]);
    },
  });

  assert.equal(result.success, true);
  assert.equal(result.isPlaying, true);
  assert.equal(result.polls, 2);
  assert.deepEqual(calls, ['enter_play_mode', 'editor_status', 'editor_status']);
});

test('readBooleanOutput accepts Unity output entry arrays and plain objects', () => {
  assert.equal(readBooleanOutput([{ key: 'isPlaying', value: 'True' }], 'isPlaying'), true);
  assert.equal(readBooleanOutput({ isPlaying: 'False' }, 'isPlaying'), false);
  assert.equal(readBooleanOutput({ isPlaying: true }, 'isPlaying'), true);
});

function response(command: string, success: boolean, outputs: unknown): EditorCommandResponse {
  return {
    id: command,
    command,
    success,
    elapsedMs: 0,
    logs: [],
    outputs,
    error: {
      message: '',
    },
  };
}
