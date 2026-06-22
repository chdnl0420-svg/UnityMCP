import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EditorCommandResponse } from '../commandBridge.js';
import { verifyScreenshotResponse } from '../screenshot.js';

test('verifyScreenshotResponse reports requested and actual dimensions', () => {
  const result = verifyScreenshotResponse({
    response: response('capture_screenshot', true, [
      { key: 'width', value: '910' },
      { key: 'height', value: '729' },
    ]),
    outputPath: 'C:\\Project\\ProjectM\\.qa\\shot.png',
    pngBytes: 1024,
    requestedWidth: 1280,
    requestedHeight: 720,
  });

  const outputs = result.outputs as Record<string, unknown>;
  assert.equal(result.success, true);
  assert.equal(outputs.actualWidth, 910);
  assert.equal(outputs.actualHeight, 729);
  assert.equal(outputs.requestedWidth, 1280);
  assert.equal(outputs.requestedHeight, 720);
  assert.equal(outputs.matchesRequestedSize, false);
});

test('verifyScreenshotResponse can fail when requested dimensions are required', () => {
  const result = verifyScreenshotResponse({
    response: response('capture_screenshot', true, { width: '910', height: '729' }),
    outputPath: 'C:\\Project\\ProjectM\\.qa\\shot.png',
    pngBytes: 1024,
    requestedWidth: 1280,
    requestedHeight: 720,
    requireRequestedSize: true,
  });

  assert.equal(result.success, false);
  assert.match(result.error?.message ?? '', /1280x720/);
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
