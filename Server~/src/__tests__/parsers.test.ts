import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseUnityTestResultsXml } from '../utils/testResults.js';
import { summarizeEditorLog } from '../utils/editorLog.js';

test('parseUnityTestResultsXml reads counters from Unity nunit XML', () => {
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<test-run id="2" testcasecount="3" result="Failed" total="3" passed="1" failed="1" inconclusive="0" skipped="1">
  <test-suite type="Assembly" name="ProjectM.Tests" result="Failed">
    <test-case name="Passes" result="Passed" duration="0.01" />
    <test-case name="Fails" result="Failed" duration="0.02">
      <failure><message>Expected true</message></failure>
    </test-case>
    <test-case name="Ignored" result="Skipped" />
  </test-suite>
</test-run>`;

  const result = parseUnityTestResultsXml(xml);

  assert.equal(result.total, 3);
  assert.equal(result.passed, 1);
  assert.equal(result.failed, 1);
  assert.equal(result.skipped, 1);
  assert.equal(result.failures.length, 1);
  assert.equal(result.failures[0]?.name, 'Fails');
  assert.match(result.failures[0]?.message ?? '', /Expected true/);
});

test('summarizeEditorLog counts warnings and errors from recent lines', () => {
  const log = [
    'Info line',
    'WARNING: shader warning',
    'Error: failed to import',
    'Exception: boom',
    '[ProjectMQaMcp] command complete'
  ].join('\n');

  const summary = summarizeEditorLog(log, 10);

  assert.equal(summary.warningCount, 1);
  assert.equal(summary.errorCount, 2);
  assert.deepEqual(summary.bridgeLines, ['[ProjectMQaMcp] command complete']);
  assert.equal(summary.tail.length, 5);
});
