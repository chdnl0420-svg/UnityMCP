import { join } from 'node:path';

export const DEFAULT_UNITY_PATH = 'C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.76f1\\Editor\\Unity.exe';
export const DEFAULT_PROJECT_PATH = 'C:\\Project\\CLIENT_KSH_ASIA_L\\client\\ProjectM';

export interface ProjectConfigInput {
  unityPath?: string;
  projectPath?: string;
  commandRoot?: string;
}

export interface ProjectConfig {
  unityPath: string;
  projectPath: string;
  commandRoot: string;
}

export function resolveProjectConfig(input: ProjectConfigInput = {}): ProjectConfig {
  const unityPath = input.unityPath || process.env.PROJECTM_UNITY_PATH || DEFAULT_UNITY_PATH;
  const projectPath = input.projectPath || process.env.PROJECTM_DEFAULT_PROJECT_PATH || DEFAULT_PROJECT_PATH;
  const commandRoot = input.commandRoot || process.env.PROJECTM_COMMAND_ROOT ||
    join(projectPath, '.codex', 'unity-commands');

  return {
    unityPath,
    projectPath,
    commandRoot,
  };
}
