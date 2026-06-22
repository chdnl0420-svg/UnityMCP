export interface EditorLogSummary {
  tail: string[];
  warningCount: number;
  errorCount: number;
  bridgeLines: string[];
}

export function summarizeEditorLog(logText: string, maxLines: number): EditorLogSummary {
  const lines = logText.split(/\r?\n/).filter((line) => line.length > 0);
  const tail = lines.slice(-maxLines);

  return {
    tail,
    warningCount: countMatching(tail, /\bwarning\b/i),
    errorCount: countMatching(tail, /\berror\b|\bexception\b/i),
    bridgeLines: tail.filter((line) => line.includes('[ProjectMQaMcp]')),
  };
}

function countMatching(lines: string[], pattern: RegExp): number {
  return lines.reduce((count, line) => count + (pattern.test(line) ? 1 : 0), 0);
}
