import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { join, win32 } from 'node:path';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export const DEFAULT_CLOSE_TIMEOUT_MS = 30000;
const POLL_INTERVAL_MS = 500;
const WINDOW_CHECK_INTERVAL_MS = 2000;
const POWERSHELL_TIMEOUT_MS = 20000;
// #32770 is the Win32 dialog class; Unity's save prompt ("Scene(s) Have Been Modified") uses it.
const WIN32_DIALOG_CLASS = '#32770';

export interface UnityProcessRow {
  pid: number;
  parentPid: number;
  commandLine: string;
}

export interface UnityProcessSummary {
  pid: number;
  kind: 'editor' | 'batch' | 'other';
  projectPath?: string;
}

export interface WindowRow {
  handle: string;
  className: string;
  title: string;
  enabled: boolean;
}

export interface ModalDialog {
  title: string;
  className: string;
}

export type TargetSelection =
  | { ok: true; pid: number; projectPath?: string; matchedBy: string[] }
  | { ok: false; error: string; candidates?: UnityProcessSummary[] };

export interface EditorCloseOptions {
  projectPath: string;
  projectPathExplicit: boolean;
  pid?: number;
  timeoutMs: number;
  force: boolean;
}

export function parseProjectPathArg(commandLine: string): string | undefined {
  const match = /(?:^|\s)"?-projectpath"?\s+(?:"([^"]+)"|(\S+))/i.exec(commandLine);
  const value = match?.[1] ?? match?.[2];
  return value ? value : undefined;
}

export function normalizeProjectPath(projectPath: string): string {
  return win32.normalize(projectPath.replace(/\//g, '\\')).replace(/\\+$/, '').toLowerCase();
}

export function isBatchModeCommandLine(commandLine: string): boolean {
  return /(?:^|\s)"?-batchmode"?(?:\s|$)/i.test(commandLine);
}

export function summarizeUnityProcess(row: UnityProcessRow): UnityProcessSummary {
  const projectPath = parseProjectPathArg(row.commandLine);
  if (isBatchModeCommandLine(row.commandLine)) {
    return { pid: row.pid, kind: 'batch', projectPath };
  }

  // Unity Hub ships its own unity.exe ("serve"); it never carries -projectPath.
  return { pid: row.pid, kind: projectPath ? 'editor' : 'other', projectPath };
}

/**
 * Picks exactly one interactive editor for the project. Matching is an exact compare of the
 * normalized -projectPath value, never a substring, so D:\Project\Dungeon or ProjectM2 never match.
 * Batch-mode children (AssetImportWorker, CLI test runs) are skipped: they close with their editor.
 */
export function selectCloseTarget(
  rows: UnityProcessRow[],
  projectPath: string,
  editorInstancePid: number | undefined,
): TargetSelection {
  const target = normalizeProjectPath(projectPath);
  const summaries = rows.map(summarizeUnityProcess);
  const matches = summaries.filter((item) =>
    item.kind === 'editor' && item.projectPath !== undefined && normalizeProjectPath(item.projectPath) === target);

  if (matches.length > 1) {
    return {
      ok: false,
      error: `More than one Unity editor is open for ${projectPath}. Pass pid to choose one.`,
      candidates: matches,
    };
  }

  const instance = editorInstancePid !== undefined
    ? summaries.find((item) => item.pid === editorInstancePid)
    : undefined;

  if (matches.length === 1) {
    const matchedBy = ['commandLine'];
    if (instance && instance.pid === matches[0].pid) {
      matchedBy.push('editorInstance');
    }
    return { ok: true, pid: matches[0].pid, projectPath: matches[0].projectPath, matchedBy };
  }

  // Fallback for an editor whose command line lacks -projectPath: Library/EditorInstance.json
  // belongs to this project, so its live, non-batch process_id is the editor of this project.
  if (instance && instance.kind !== 'batch' && instance.projectPath === undefined) {
    return { ok: true, pid: instance.pid, projectPath, matchedBy: ['editorInstance'] };
  }

  return {
    ok: false,
    error: `No open Unity editor found for ${projectPath}.`,
    candidates: summaries.filter((item) => item.kind !== 'other'),
  };
}

/**
 * A dialog blocks the close when it is a Win32 dialog window, or any enabled window while the main
 * window is disabled. While the editor is still loading the dialog can itself be the "main window"
 * (no editor window exists yet), so the main handle is not excluded.
 */
export function findModalDialogs(windows: WindowRow[], mainHandle: string | undefined): ModalDialog[] {
  const mainWindow = windows.find((item) => item.handle === mainHandle);
  const mainBlocked = mainWindow !== undefined && !mainWindow.enabled;

  return windows
    .filter((item) => item.className === WIN32_DIALOG_CLASS ||
      (mainBlocked && item.enabled && item.handle !== mainHandle))
    .map((item) => ({ title: item.title, className: item.className }));
}

export async function closeUnityEditor(options: EditorCloseOptions): Promise<unknown> {
  if (process.platform !== 'win32') {
    return { success: false, error: 'unity_editor_close supports Windows only.' };
  }

  let rows: UnityProcessRow[];
  try {
    rows = await listUnityProcesses();
  } catch (error) {
    return { success: false, error: `Could not list Unity processes: ${describeError(error)}` };
  }

  const selection = options.pid !== undefined
    ? selectByPid(rows, options)
    : selectCloseTarget(rows, options.projectPath, await readEditorInstancePid(options.projectPath));

  if (!selection.ok) {
    return {
      success: false,
      error: selection.error,
      candidates: selection.candidates,
      remainingUnityProcesses: rows.map(summarizeUnityProcess),
    };
  }

  const targetPid = selection.pid;
  const startedAt = Date.now();
  let modalDialogs: ModalDialog[] = [];
  let windowCheckError: string | undefined;
  // Look before closing: WM_CLOSE to a dialog that stands in for the main window would just
  // press its Cancel and leave the editor running.
  const mainWindow = await requestWindowClose(targetPid, false);
  try {
    modalDialogs = findModalDialogs(await listProcessWindows(targetPid), mainWindow.mainHandle);
  } catch (error) {
    windowCheckError = describeError(error);
  }
  const request = modalDialogs.length === 0 && mainWindow.error === undefined
    ? await requestWindowClose(targetPid, true)
    : mainWindow;
  let closed = await waitForExit(targetPid, startedAt, options.timeoutMs, request.closeRequestSent, async () => {
    try {
      const found = findModalDialogs(await listProcessWindows(targetPid), request.mainHandle);
      if (found.length > 0) {
        modalDialogs = found;
      }
    } catch (error) {
      windowCheckError = describeError(error);
    }
  });
  const timedOut = request.closeRequestSent && !closed;

  let forced = false;
  let killedChildren: number[] = [];
  if (!closed && options.force) {
    killedChildren = await forceKill(targetPid);
    forced = true;
    closed = await waitForExit(targetPid, Date.now(), 5000, true);
  }

  const elapsedMs = Date.now() - startedAt;
  const remaining = await listUnityProcesses().then(
    (items) => items.map(summarizeUnityProcess),
    (error) => `Could not list Unity processes: ${describeError(error)}`,
  );

  return {
    success: closed,
    targetPid,
    projectPath: selection.projectPath,
    matchedBy: selection.matchedBy,
    windowTitle: request.windowTitle,
    closeRequestSent: request.closeRequestSent,
    closeRequestError: request.error,
    closed,
    timedOut,
    forced,
    killedChildren,
    elapsedMs,
    modalDialogs,
    windowCheckError,
    remainingUnityProcesses: remaining,
    note: closed ? undefined : buildNotClosedNote(request.closeRequestSent, modalDialogs, options.force),
  };
}

function selectByPid(rows: UnityProcessRow[], options: EditorCloseOptions): TargetSelection {
  const row = rows.find((item) => item.pid === options.pid);
  if (!row) {
    return { ok: false, error: `pid ${options.pid} is not a running Unity.exe process.` };
  }

  const summary = summarizeUnityProcess(row);
  if (summary.kind !== 'editor') {
    return { ok: false, error: `pid ${options.pid} is not an interactive Unity editor (kind=${summary.kind}).` };
  }

  if (options.projectPathExplicit &&
    normalizeProjectPath(summary.projectPath ?? '') !== normalizeProjectPath(options.projectPath)) {
    return {
      ok: false,
      error: `pid ${options.pid} opened ${summary.projectPath}, not ${options.projectPath}.`,
    };
  }

  return { ok: true, pid: summary.pid, projectPath: summary.projectPath, matchedBy: ['pid'] };
}

function buildNotClosedNote(closeRequestSent: boolean, modalDialogs: ModalDialog[], force: boolean): string {
  if (modalDialogs.length > 0 && !force) {
    return closeRequestSent
      ? 'A dialog is blocking the close (for example a save prompt). Answer it, or pass force=true to kill without saving.'
      : 'A dialog is open, so no close request was sent (it would only dismiss the dialog). Answer it, or pass force=true to kill.';
  }
  if (!closeRequestSent) {
    return force
      ? 'The editor had no main window to close and force kill did not end it.'
      : 'The editor has no main window yet (still loading?). Retry later or pass force=true.';
  }
  return force
    ? 'The editor did not exit even after force kill.'
    : 'The editor did not exit within timeoutMs. Retry with a longer timeoutMs, or pass force=true.';
}

async function waitForExit(
  pid: number,
  startedAt: number,
  timeoutMs: number,
  shouldWait: boolean,
  onWindowCheck?: () => Promise<void>,
): Promise<boolean> {
  if (!isAlive(pid)) {
    return true;
  }
  if (!shouldWait) {
    return false;
  }

  let lastWindowCheck = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    await delay(POLL_INTERVAL_MS);
    if (!isAlive(pid)) {
      return true;
    }
    if (onWindowCheck && Date.now() - lastWindowCheck >= WINDOW_CHECK_INTERVAL_MS) {
      lastWindowCheck = Date.now();
      await onWindowCheck();
    }
  }
  return !isAlive(pid);
}

function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error: any) {
    // EPERM means the process exists but belongs to someone else.
    return error?.code === 'EPERM';
  }
}

async function forceKill(pid: number): Promise<number[]> {
  // Read children before the kill, so AssetImportWorkers of this editor do not outlive it.
  const children = await listUnityProcesses().then(
    (items) => items.filter((item) => item.parentPid === pid).map((item) => item.pid),
    () => [] as number[],
  );
  try {
    process.kill(pid);
  } catch {
    // Already gone between the timeout and the kill.
  }
  for (const child of children) {
    try {
      process.kill(child);
    } catch {
      // The editor took its child down with it.
    }
  }
  return children;
}

async function readEditorInstancePid(projectPath: string): Promise<number | undefined> {
  try {
    const text = await readFile(join(projectPath, 'Library', 'EditorInstance.json'), 'utf8');
    const pid = Number((JSON.parse(text) as { process_id?: unknown }).process_id);
    return Number.isInteger(pid) && pid > 0 ? pid : undefined;
  } catch {
    return undefined;
  }
}

async function listUnityProcesses(): Promise<UnityProcessRow[]> {
  const script = [
    "$rows = @(Get-CimInstance Win32_Process -Filter \"name='Unity.exe'\" | Select-Object ProcessId,ParentProcessId,CommandLine)",
    'ConvertTo-Json -InputObject $rows -Depth 3 -Compress',
  ].join('\n');
  const parsed = await runPowerShellJson(script);
  const rows = Array.isArray(parsed) ? parsed : [];
  return rows.map((row: any) => ({
    pid: Number(row.ProcessId) || 0,
    parentPid: Number(row.ParentProcessId) || 0,
    commandLine: String(row.CommandLine || ''),
  })).filter((row) => row.pid > 0);
}

interface CloseRequest {
  closeRequestSent: boolean;
  windowTitle: string;
  mainHandle?: string;
  error?: string;
}

async function requestWindowClose(pid: number, send: boolean): Promise<CloseRequest> {
  // CloseMainWindow posts WM_CLOSE to the main window: the same as clicking the X button.
  // With send=false it only reads the main window handle and title.
  const script = [
    `$p = Get-Process -Id ${pid} -ErrorAction Stop`,
    '$handle = [string]$p.MainWindowHandle.ToInt64()',
    '$title = $p.MainWindowTitle',
    '$sent = $false',
    `if (${send ? '$true' : '$false'} -and $handle -ne '0') { $sent = $p.CloseMainWindow() }`,
    'ConvertTo-Json -InputObject @{ sent = $sent; title = $title; handle = $handle } -Compress',
  ].join('\n');
  let parsed: { sent?: boolean; title?: string; handle?: string } | undefined;
  try {
    parsed = await runPowerShellJson(script) as typeof parsed;
  } catch (error) {
    return { closeRequestSent: false, windowTitle: '', error: describeError(error) };
  }
  const handle = parsed?.handle && parsed.handle !== '0' ? parsed.handle : undefined;
  return {
    closeRequestSent: parsed?.sent === true,
    windowTitle: parsed?.title ?? '',
    mainHandle: handle,
  };
}

async function listProcessWindows(pid: number): Promise<WindowRow[]> {
  const script = `
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Nx3UnityWindows {
  private delegate bool EnumProc(IntPtr handle, IntPtr param);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
  [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
  public static List<object> List(uint target) {
    var rows = new List<object>();
    EnumWindows((handle, param) => {
      uint owner;
      GetWindowThreadProcessId(handle, out owner);
      if (owner != target || !IsWindowVisible(handle)) { return true; }
      var title = new StringBuilder(512);
      var className = new StringBuilder(256);
      GetWindowText(handle, title, title.Capacity);
      GetClassName(handle, className, className.Capacity);
      rows.Add(new Dictionary<string, object> {
        { "handle", handle.ToInt64().ToString() },
        { "className", className.ToString() },
        { "title", title.ToString() },
        { "enabled", IsWindowEnabled(handle) },
      });
      return true;
    }, IntPtr.Zero);
    return rows;
  }
}
'@
$rows = @([Nx3UnityWindows]::List(${pid}))
ConvertTo-Json -InputObject $rows -Depth 3 -Compress
`;
  const parsed = await runPowerShellJson(script);
  const rows = Array.isArray(parsed) ? parsed : [];
  return rows.map((row: any) => ({
    handle: String(row.handle ?? ''),
    className: String(row.className ?? ''),
    title: String(row.title ?? ''),
    enabled: row.enabled === true,
  }));
}

async function runPowerShellJson(script: string): Promise<unknown> {
  const fullScript = `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n${script}`;
  const encoded = Buffer.from(fullScript, 'utf16le').toString('base64');
  const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], {
    windowsHide: true,
    timeout: POWERSHELL_TIMEOUT_MS,
    encoding: 'utf8',
  });
  const text = stdout.trim();
  return text.length === 0 ? undefined : JSON.parse(text);
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
