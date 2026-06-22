import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export interface ProcessInfo {
  pid: number;
  name: string;
  executablePath: string;
  commandLine: string;
}

export interface ProcessCandidate extends ProcessInfo {
  reason: string;
  killableByDefault: boolean;
}

export async function listUnityRelatedProcesses(): Promise<ProcessInfo[]> {
  if (process.platform !== 'win32') {
    return [];
  }

  const command = [
    'Get-CimInstance Win32_Process',
    "-Filter \"name='Unity.exe' or name='UnityCrashHandler64.exe' or name='node.exe'\"",
    '| Select-Object ProcessId,Name,ExecutablePath,CommandLine',
    '| ConvertTo-Json -Depth 3',
  ].join(' ');

  try {
    const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-Command', command], {
      windowsHide: true,
      timeout: 10000,
    });
    if (stdout.trim().length === 0) {
      return [];
    }

    const parsed = JSON.parse(stdout) as unknown;
    const rows = Array.isArray(parsed) ? parsed : [parsed];
    return rows.map((row: any) => ({
      pid: Number(row.ProcessId) || 0,
      name: String(row.Name || ''),
      executablePath: String(row.ExecutablePath || ''),
      commandLine: String(row.CommandLine || ''),
    })).filter((row) => row.pid > 0);
  } catch {
    return [];
  }
}

export function findStaleCandidates(processes: ProcessInfo[], projectPath: string): ProcessCandidate[] {
  const normalizedProject = projectPath.toLowerCase();

  return processes.flatMap<ProcessCandidate>((item) => {
    const text = `${item.executablePath}\n${item.commandLine}`.toLowerCase();
    if (item.name.toLowerCase() === 'unitycrashhandler64.exe') {
      return [{ ...item, reason: 'Unity crash handler leftover process', killableByDefault: true }];
    }

    if (item.name.toLowerCase() === 'node.exe' &&
      (text.includes('projectm-qa-mcp') || text.includes('unitymcp') || text.includes('mcp-unity'))) {
      return [{ ...item, reason: 'Unity MCP-related node process', killableByDefault: true }];
    }

    if (item.name.toLowerCase() === 'unity.exe' && text.includes(normalizedProject)) {
      return [{ ...item, reason: 'Unity process opened for target project', killableByDefault: false }];
    }

    return [];
  });
}

export async function killProcess(pid: number): Promise<void> {
  process.kill(pid, 'SIGTERM');
}
