import { EditorCommandResponse } from './commandBridge.js';
import { normalizeOutputs } from './playMode.js';

export interface ScreenshotVerificationOptions {
  response: EditorCommandResponse;
  outputPath: string;
  pngBytes: number;
  requestedWidth?: number;
  requestedHeight?: number;
  requireRequestedSize?: boolean;
}

export function verifyScreenshotResponse(options: ScreenshotVerificationOptions): EditorCommandResponse {
  const outputs = normalizeOutputs(options.response.outputs);
  const actualWidth = readNumberOutput(outputs.width);
  const actualHeight = readNumberOutput(outputs.height);
  const hasRequestedSize = options.requestedWidth !== undefined && options.requestedHeight !== undefined;
  const hasActualSize = actualWidth !== undefined && actualHeight !== undefined;
  const matchesRequestedSize = hasRequestedSize && hasActualSize
    ? actualWidth === options.requestedWidth && actualHeight === options.requestedHeight
    : undefined;
  const sizeMismatch = options.requireRequestedSize === true && matchesRequestedSize !== true;
  const emptyPng = options.pngBytes <= 0;

  return {
    ...options.response,
    success: options.response.success && !emptyPng && !sizeMismatch,
    outputs: {
      ...outputs,
      outputPath: options.outputPath,
      pngExists: options.pngBytes > 0,
      pngBytes: options.pngBytes,
      requestedWidth: options.requestedWidth,
      requestedHeight: options.requestedHeight,
      actualWidth,
      actualHeight,
      matchesRequestedSize,
      requireRequestedSize: options.requireRequestedSize ?? false,
    },
    error: makeScreenshotError(options.response, options.outputPath, emptyPng, sizeMismatch, options),
  };
}

function makeScreenshotError(
  response: EditorCommandResponse,
  outputPath: string,
  emptyPng: boolean,
  sizeMismatch: boolean,
  options: ScreenshotVerificationOptions,
): EditorCommandResponse['error'] {
  if (!response.success) {
    return response.error;
  }

  if (emptyPng) {
    return {
      message: `Screenshot PNG was not created or is empty: ${outputPath}`,
    };
  }

  if (sizeMismatch) {
    return {
      message: `Screenshot size did not match requested ${options.requestedWidth}x${options.requestedHeight}`,
    };
  }

  return response.error;
}

function readNumberOutput(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  return undefined;
}
