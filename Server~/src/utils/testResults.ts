export interface UnityTestFailure {
  name: string;
  message: string;
}

export interface UnityTestResults {
  total: number;
  passed: number;
  failed: number;
  skipped: number;
  inconclusive: number;
  result: string;
  failures: UnityTestFailure[];
}

export function parseUnityTestResultsXml(xml: string): UnityTestResults {
  const testRunTag = xml.match(/<test-run\b[^>]*>/i)?.[0] ?? '';
  const total = readNumberAttribute(testRunTag, ['total', 'testcasecount']);
  const passed = readNumberAttribute(testRunTag, ['passed']);
  const failed = readNumberAttribute(testRunTag, ['failed']);
  const skipped = readNumberAttribute(testRunTag, ['skipped']);
  const inconclusive = readNumberAttribute(testRunTag, ['inconclusive']);
  const result = readStringAttribute(testRunTag, 'result') || 'Unknown';

  return {
    total,
    passed,
    failed,
    skipped,
    inconclusive,
    result,
    failures: readFailures(xml),
  };
}

function readFailures(xml: string): UnityTestFailure[] {
  const failures: UnityTestFailure[] = [];
  const testCasePattern = /<test-case\b[^>]*result="Failed"[^>]*>[\s\S]*?<\/test-case>/gi;
  const matches = xml.matchAll(testCasePattern);

  for (const match of matches) {
    const block = match[0];
    const tag = block.match(/<test-case\b[^>]*>/i)?.[0] ?? '';
    const message = block.match(/<message>([\s\S]*?)<\/message>/i)?.[1] ?? '';

    failures.push({
      name: decodeXml(readStringAttribute(tag, 'name') || 'Unknown'),
      message: decodeXml(message.trim()),
    });
  }

  return failures;
}

function readNumberAttribute(tag: string, names: string[]): number {
  for (const name of names) {
    const value = readStringAttribute(tag, name);
    if (value !== undefined) {
      return Number.parseInt(value, 10) || 0;
    }
  }

  return 0;
}

function readStringAttribute(tag: string, name: string): string | undefined {
  const escapedName = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = tag.match(new RegExp(`${escapedName}="([^"]*)"`, 'i'));
  return match?.[1];
}

function decodeXml(value: string): string {
  return value
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');
}
