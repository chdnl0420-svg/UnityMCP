export interface EditorLogSummary {
  tail: string[];
  warningCount: number;
  errorCount: number;
  bridgeLines: string[];
  warningLines: string[];
  errorLines: string[];
}

const warningPatterns = [
  /\bwarning\b/i,
];

const errorPatterns = [
  /\berror\b/i,
  /\bexception\b/i,
  /trying to create a MonoBehaviour using the 'new' keyword/i,
];

export function summarizeEditorLog(logText: string, maxLines: number): EditorLogSummary {
  const lines = logText.split(/\r?\n/).filter((line) => line.length > 0);
  const tail = lines.slice(-maxLines);
  const warningLines = tail.filter((line) => matchesAny(line, warningPatterns));
  const errorLines = tail.filter((line) => matchesAny(line, errorPatterns));

  return {
    tail,
    warningCount: warningLines.length,
    errorCount: errorLines.length,
    bridgeLines: tail.filter((line) => line.includes('[ProjectMQaMcp]')),
    warningLines,
    errorLines,
  };
}

function matchesAny(line: string, patterns: RegExp[]): boolean {
  return patterns.some((pattern) => pattern.test(line));
}
