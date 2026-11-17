import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  findModalDialogs,
  normalizeProjectPath,
  parseProjectPathArg,
  selectCloseTarget,
  UnityProcessRow,
} from '../editorClose.js';

const UNITY = '"C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.76f1\\Editor\\Unity.exe"';
const PROJECT_M = 'C:\\Project\\CLIENT_KSH_ASIA_L\\client\\ProjectM';

const rows: UnityProcessRow[] = [
  { pid: 10, parentPid: 1, commandLine: '"C:\\Program Files\\Unity Hub\\resources\\unity.exe" serve' },
  { pid: 20, parentPid: 1, commandLine: `${UNITY} -projectpath D:\\Project\\Dungeon -useHub -hubIPC` },
  { pid: 30, parentPid: 1, commandLine: `${UNITY} -projectPath C:\\Project\\CLIENT_KSH_ASIA_L\\client\\ProjectM` },
  {
    pid: 31,
    parentPid: 30,
    commandLine: `${UNITY} "-adb2" "-batchMode" "-noUpm" "-name" "AssetImportWorker0" "-projectPath" "C:/Project/CLIENT_KSH_ASIA_L/client/ProjectM"`,
  },
  { pid: 40, parentPid: 1, commandLine: `${UNITY} -projectPath "C:\\Project\\CLIENT_KSH_ASIA_L\\client\\ProjectM2"` },
];

test('parseProjectPathArg reads quoted and bare values case-insensitively', () => {
  assert.equal(parseProjectPathArg(`${UNITY} -projectpath D:\\Project\\Dungeon -useHub`), 'D:\\Project\\Dungeon');
  assert.equal(parseProjectPathArg(`${UNITY} "-projectPath" "C:/a b/c"`), 'C:/a b/c');
  assert.equal(parseProjectPathArg(`${UNITY} -projectPath "C:\\a b\\c"`), 'C:\\a b\\c');
  assert.equal(parseProjectPathArg(`${UNITY} serve`), undefined);
});

test('normalizeProjectPath treats slashes, case and trailing separators alike', () => {
  assert.equal(normalizeProjectPath('C:/Project/X/'), normalizeProjectPath('c:\\project\\x'));
});

test('selectCloseTarget picks only the interactive editor of the exact project', () => {
  const result = selectCloseTarget(rows, PROJECT_M, undefined);

  assert.equal(result.ok, true);
  assert.equal(result.ok && result.pid, 30);
});

test('selectCloseTarget never matches another project or a longer path with the same prefix', () => {
  const result = selectCloseTarget(rows, 'D:\\Project\\Dungeon', undefined);
  assert.equal(result.ok && result.pid, 20);

  const missing = selectCloseTarget(rows.filter((row) => row.pid !== 30), PROJECT_M, undefined);
  assert.equal(missing.ok, false);
});

test('selectCloseTarget refuses when two editors claim the same project', () => {
  const duplicated = [...rows, { pid: 50, parentPid: 1, commandLine: `${UNITY} -projectPath C:/Project/CLIENT_KSH_ASIA_L/client/ProjectM/` }];

  const result = selectCloseTarget(duplicated, PROJECT_M, undefined);

  assert.equal(result.ok, false);
  assert.deepEqual(!result.ok && result.candidates?.map((item) => item.pid), [30, 50]);
});

test('selectCloseTarget falls back to EditorInstance pid only for an editor without -projectPath', () => {
  const bare = [{ pid: 60, parentPid: 1, commandLine: `${UNITY}` }];
  const fallback = selectCloseTarget(bare, PROJECT_M, 60);
  assert.equal(fallback.ok && fallback.pid, 60);
  assert.deepEqual(fallback.ok && fallback.matchedBy, ['editorInstance']);

  // A stale EditorInstance.json pointing at the Dungeon editor must not select it.
  const stale = selectCloseTarget(rows.filter((row) => row.pid !== 30), PROJECT_M, 20);
  assert.equal(stale.ok, false);
});

test('findModalDialogs reports Win32 dialogs and windows over a disabled main window', () => {
  const dialogs = findModalDialogs([
    { handle: '1', className: 'UnityContainerWndClass', title: 'ProjectM - Main - Windows', enabled: false },
    { handle: '2', className: '#32770', title: 'Scene(s) Have Been Modified', enabled: true },
  ], '1');
  assert.deepEqual(dialogs, [{ title: 'Scene(s) Have Been Modified', className: '#32770' }]);

  const none = findModalDialogs([
    { handle: '1', className: 'UnityContainerWndClass', title: 'ProjectM', enabled: true },
    { handle: '3', className: 'UnityContainerWndClass', title: 'Inspector', enabled: true },
  ], '1');
  assert.deepEqual(none, []);
});

test('findModalDialogs counts a dialog that stands in for the main window during loading', () => {
  const dialogs = findModalDialogs([
    { handle: '7', className: '#32770', title: 'Mobile Dependency Resolver required', enabled: true },
  ], '7');

  assert.deepEqual(dialogs, [{ title: 'Mobile Dependency Resolver required', className: '#32770' }]);
});
