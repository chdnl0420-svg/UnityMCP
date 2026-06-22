import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { runUiTextQaFlow } from '../qaFlow.js';

test('runUiTextQaFlow runs PlayMode text QA steps in order', async () => {
  const calls: string[] = [];
  const dumps = [
    'GUIRoot/Intro\tlabel\ttext=SKIP',
    'GUIRoot/Login\tlabel\ttext=TAP TO START',
  ];
  let expectedPlaying = false;

  const result = await runUiTextQaFlow({
    initialText: 'SKIP',
    clickText: 'SKIP',
    expectedText: 'TAP TO START',
    outputRoot: 'C:\\Project\\ProjectM\\.qa\\flow\\screenshots',
    exact: true,
    timeoutMs: 30000,
    pollIntervalMs: 1,
    delay: async () => undefined,
    now: () => 0,
    getFileSize: async () => 1234,
    execute: async (command, parameters) => {
      calls.push(`${command}:${String(parameters.text ?? parameters.outputPath ?? '')}`);

      if (command === 'enter_play_mode') {
        expectedPlaying = true;
        return response(command, true, {});
      }

      if (command === 'exit_play_mode') {
        expectedPlaying = false;
        return response(command, true, {});
      }

      if (command === 'editor_status') {
        return response(command, true, [
          { key: 'isPlaying', value: String(expectedPlaying) },
        ]);
      }

      if (command === 'dump_ui') {
        return response(command, true, [
          { key: 'ui', value: dumps.shift() ?? '' },
        ]);
      }

      if (command === 'click_ui_text') {
        return response(command, true, [
          { key: 'hitPath', value: 'GUIRoot/UI Root/IntroPanel/TopR/btn_left' },
        ]);
      }

      return response(command, true, [
        { key: 'outputPath', value: parameters.outputPath },
        { key: 'pngBytes', value: 1234 },
      ]);
    },
  });

  assert.equal(result.success, true);
  assert.deepEqual(result.steps.map((step) => step.name), [
    'enter_play_mode',
    'wait_initial_text',
    'capture_before',
    'click_and_wait',
    'capture_after',
    'exit_play_mode',
  ]);
  assert.match(result.screenshots.before, /before\.png$/);
  assert.match(result.screenshots.after, /after\.png$/);
  assert.deepEqual(calls, [
    'enter_play_mode:',
    'editor_status:',
    'dump_ui:',
    'capture_screenshot:C:\\Project\\ProjectM\\.qa\\flow\\screenshots\\before.png',
    'click_ui_text:SKIP',
    'dump_ui:',
    'capture_screenshot:C:\\Project\\ProjectM\\.qa\\flow\\screenshots\\after.png',
    'exit_play_mode:',
    'editor_status:',
  ]);
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
