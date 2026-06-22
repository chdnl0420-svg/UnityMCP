import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { clickUiTextAndWait, findUiTextLine, waitForUiText } from '../uiText.js';

test('findUiTextLine matches dump_ui text entries', () => {
  const ui = [
    'GUIRoot/Label\tlabel\tx=0.500\ty=0.500\ttext=TAP TO START',
    'GUIRoot/Version\tlabel\ttext=1.7.1',
  ].join('\n');

  assert.match(findUiTextLine(ui, 'tap to start', true) ?? '', /TAP TO START/);
  assert.match(findUiTextLine(ui, 'START', false) ?? '', /TAP TO START/);
  assert.equal(findUiTextLine(ui, 'START', true), undefined);
});

test('waitForUiText polls dump_ui until requested text appears', async () => {
  const dumps = [
    'GUIRoot/Splash\tlabel\ttext=SKIP',
    'GUIRoot/Login\tlabel\ttext=TAP TO START',
  ];
  const calls: string[] = [];

  const result = await waitForUiText({
    text: 'TAP TO START',
    exact: true,
    timeoutMs: 5000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command) => {
      calls.push(command);
      return response(command, true, [
        { key: 'ui', value: dumps.shift() ?? '' },
      ]);
    },
  });

  assert.equal(result.success, true);
  assert.equal(result.polls, 2);
  assert.match(result.matchedLine ?? '', /TAP TO START/);
  assert.deepEqual(calls, ['dump_ui', 'dump_ui']);
});

test('clickUiTextAndWait clicks first, then waits for expected text', async () => {
  const calls: string[] = [];

  const result = await clickUiTextAndWait({
    clickText: 'SKIP',
    waitText: 'TAP TO START',
    exact: true,
    timeoutMs: 5000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    execute: async (command, parameters) => {
      calls.push(`${command}:${parameters.text ?? ''}`);
      if (command === 'click_ui_text') {
        return response(command, true, [
          { key: 'hitPath', value: 'GUIRoot/UI Root/IntroPanel/TopR/btn_left' },
        ]);
      }

      return response(command, true, [
        { key: 'ui', value: 'GUIRoot/Login\tlabel\ttext=TAP TO START' },
      ]);
    },
  });

  assert.equal(result.success, true);
  assert.equal(result.polls, 1);
  assert.match(result.matchedLine ?? '', /TAP TO START/);
  assert.deepEqual(calls, ['click_ui_text:SKIP', 'dump_ui:']);
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
