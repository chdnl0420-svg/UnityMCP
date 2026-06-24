using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMQaMcp.Editor
{
    [InitializeOnLoad]
    public static class CommandRunner
    {
        private const string LogPrefix = "[ProjectMQaMcp]";
        // Monotonic sentinel: bump on every deploy so callers can verify a re-resolved
        // package actually loaded the new bridge code (absence->presence is unambiguous).
        private const int BridgeProtocolVersion = 4;
        private const double PollIntervalSeconds = 1.0;
        private const float FallbackClickMaxNormalizedDistanceSqr = 0.18f;
        private const int FallbackClickMinSharedHierarchy = 3;
        private const float VisibleBoundsPadding = 0.02f;

        private static double nextPollTime;
        private static bool isProcessing;

        static CommandRunner()
        {
            EditorApplication.update += Poll;
            // Buffer compile errors to a file so they survive the domain reload that a
            // recompile triggers (static fields reset on reload, a file does not).
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        public static void RunOnce()
        {
            ProcessPendingRequests();
        }

        private static void Poll()
        {
            if (isProcessing || EditorApplication.timeSinceStartup < nextPollTime)
            {
                return;
            }

            nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            ProcessPendingRequests();
        }

        private static void ProcessPendingRequests()
        {
            var requestsDir = Path.Combine(GetCommandRoot(), "requests");
            if (!Directory.Exists(requestsDir))
            {
                return;
            }

            isProcessing = true;
            try
            {
                foreach (var requestPath in Directory.GetFiles(requestsDir, "*.json").OrderBy(File.GetCreationTimeUtc))
                {
                    ProcessRequest(requestPath);
                }
            }
            finally
            {
                isProcessing = false;
            }
        }

        private static void ProcessRequest(string requestPath)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = new CommandResponse();
            try
            {
                var request = JsonUtility.FromJson<CommandRequest>(File.ReadAllText(requestPath));
                if (request == null || string.IsNullOrEmpty(request.id) || string.IsNullOrEmpty(request.command))
                {
                    throw new InvalidOperationException("Invalid request JSON.");
                }

                response.id = request.id;
                response.command = request.command;
                response.logs.Add($"{LogPrefix} processing {request.command}");

                Execute(request, response);
                if (response.error == null && !response.success)
                {
                    response.success = true;
                }
            }
            catch (Exception e)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = e.Message,
                    details = e.ToString()
                };
                response.logs.Add($"{LogPrefix} failed: {e.Message}");
            }
            finally
            {
                stopwatch.Stop();
                response.elapsedMs = stopwatch.ElapsedMilliseconds;
                WriteResponse(response);
                TryArchiveRequest(requestPath);
            }
        }

        private static void Execute(CommandRequest request, CommandResponse response)
        {
            var parameters = request.parameters ?? new CommandParameters();
            switch (request.command)
            {
                case "ping":
                    AddEditorStatus(response);
                    break;
                case "editor_status":
                    AddEditorStatus(response);
                    break;
                case "capture_screenshot":
                case "capture_game_view":
                    CaptureScreenshot(parameters, response);
                    break;
                case "open_scene":
                    OpenScene(parameters, response);
                    break;
                case "load_prefab":
                    LoadPrefab(parameters, response);
                    break;
                case "find_ngui_object":
                    FindNguiObject(parameters, response);
                    break;
                case "click_ngui_object":
                    ClickNguiObject(parameters, response);
                    break;
                case "click_at":
                    ClickAt(parameters, response);
                    break;
                case "click_ui_text":
                    ClickUiText(parameters, response);
                    break;
                case "enter_play_mode":
                    SetPlayMode(true, response);
                    break;
                case "exit_play_mode":
                    SetPlayMode(false, response);
                    break;
                case "dump_ui":
                    DumpUi(parameters, response);
                    break;
                case "batch":
                    BatchExecute(parameters, response);
                    break;
                case "resolve_packages":
                    ResolvePackages(response);
                    break;
                case "refresh_assets":
                    RefreshAssets(response);
                    break;
                case "recompile_scripts":
                    RecompileScripts(response);
                    break;
                case "compile_status":
                    CompileStatus(response);
                    break;
                case "get_console_logs":
                    GetConsoleLogs(parameters, response);
                    break;
                case "clear_console":
                    ClearConsole(response);
                    break;
                case "inspect_object":
                    InspectObject(parameters, response);
                    break;
                case "find_objects":
                    FindObjects(parameters, response);
                    break;
                case "set_active":
                    SetActive(parameters, response);
                    break;
                case "set_label_text":
                    SetLabelText(parameters, response);
                    break;
                case "set_input_text":
                    SetInputText(parameters, response);
                    break;
                case "set_sprite":
                    SetSprite(parameters, response);
                    break;
                case "scene_info":
                    SceneInfo(response);
                    break;
                case "get_hierarchy":
                    GetHierarchy(parameters, response);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported command: {request.command}");
            }
        }

        private static void AddEditorStatus(CommandResponse response)
        {
            response.AddOutput("bridgeVersion", BridgeProtocolVersion.ToString());
            response.AddOutput("projectPath", Application.dataPath.Replace("/Assets", ""));
            response.AddOutput("unityVersion", Application.unityVersion);
            response.AddOutput("isBatchMode", Application.isBatchMode.ToString());
            response.AddOutput("isPlaying", Application.isPlaying.ToString());
            response.AddOutput("activeScene", EditorSceneManager.GetActiveScene().path);
        }

        private static void BatchExecute(CommandParameters parameters, CommandResponse response)
        {
            if (parameters.commands == null || parameters.commands.Count == 0)
            {
                throw new ArgumentException("batch requires a non-empty commands array.");
            }

            response.steps = new List<CommandStepResponse>();
            var index = 0;
            foreach (var sub in parameters.commands)
            {
                var subRequest = ToCommandRequest(sub, index);
                var stepStopwatch = Stopwatch.StartNew();
                var stepResponse = new CommandResponse
                {
                    id = subRequest.id,
                    command = subRequest.command
                };

                try
                {
                    if (string.IsNullOrEmpty(subRequest.command))
                    {
                        throw new InvalidOperationException("batch step is missing a command.");
                    }

                    Execute(subRequest, stepResponse);
                    if (stepResponse.error == null && !stepResponse.success)
                    {
                        stepResponse.success = true;
                    }
                }
                catch (Exception e)
                {
                    stepResponse.success = false;
                    stepResponse.error = new CommandError
                    {
                        message = e.Message,
                        details = e.ToString()
                    };
                }
                finally
                {
                    stepStopwatch.Stop();
                    stepResponse.elapsedMs = stepStopwatch.ElapsedMilliseconds;
                }

                response.steps.Add(ToStepResponse(stepResponse));
                index++;
            }

            response.AddOutput("stepCount", response.steps.Count.ToString());
        }

        private static void ResolvePackages(CommandResponse response)
        {
            UnityEditor.PackageManager.Client.Resolve();
            response.AddOutput("requestedResolve", "true");
        }

        private static void RefreshAssets(CommandResponse response)
        {
            // Reimports changed assets and triggers script recompilation. Unity defers
            // compilation while in PlayMode, so report that so callers can exit first.
            AssetDatabase.Refresh(ImportAssetOptions.Default);
            response.AddOutput("requestedRefresh", "true");
            response.AddOutput("isPlaying", Application.isPlaying.ToString());
            response.AddOutput("isCompiling", EditorApplication.isCompiling.ToString());
        }

        private static void RecompileScripts(CommandResponse response)
        {
            if (Application.isPlaying)
            {
                // Scripts cannot compile during PlayMode; make the no-op explicit.
                response.success = false;
                response.error = new CommandError
                {
                    message = "Cannot recompile scripts while in PlayMode. Exit play mode first."
                };
                response.AddOutput("isPlaying", "True");
                return;
            }

            ClearCompileErrorFile();
            CompilationPipeline.RequestScriptCompilation();
            response.AddOutput("requestedRecompile", "true");
            response.AddOutput("isCompiling", EditorApplication.isCompiling.ToString());
        }

        private static void CompileStatus(CommandResponse response)
        {
            var errors = ReadCompileErrorFile();
            response.AddOutput("isCompiling", EditorApplication.isCompiling.ToString());
            response.AddOutput("isUpdating", EditorApplication.isUpdating.ToString());
            response.AddOutput("isPlaying", Application.isPlaying.ToString());
            response.AddOutput("compileErrorCount", errors.Count.ToString());
            if (errors.Count > 0)
            {
                response.AddOutput("compileErrors", string.Join("\n", errors));
            }
        }

        private static void OnCompilationStarted(object context)
        {
            ClearCompileErrorFile();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            var errors = messages
                .Where(m => m.type == CompilerMessageType.Error)
                .Select(m => $"{m.file}({m.line},{m.column}): {m.message}")
                .ToList();
            if (errors.Count > 0)
            {
                AppendCompileErrors(errors);
            }
        }

        private static string CompileErrorFilePath()
        {
            return Path.Combine(GetCommandRoot(), "compile-errors.json");
        }

        private static void ClearCompileErrorFile()
        {
            try
            {
                Directory.CreateDirectory(GetCommandRoot());
                File.WriteAllText(CompileErrorFilePath(), JsonUtility.ToJson(new CompileErrorLog()));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{LogPrefix} failed to clear compile-errors file: {e.Message}");
            }
        }

        private static void AppendCompileErrors(List<string> newErrors)
        {
            try
            {
                var log = ReadCompileErrorLog();
                log.errors.AddRange(newErrors);
                File.WriteAllText(CompileErrorFilePath(), JsonUtility.ToJson(log));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{LogPrefix} failed to append compile errors: {e.Message}");
            }
        }

        private static List<string> ReadCompileErrorFile()
        {
            return ReadCompileErrorLog().errors;
        }

        private static CompileErrorLog ReadCompileErrorLog()
        {
            try
            {
                var path = CompileErrorFilePath();
                if (!File.Exists(path))
                {
                    return new CompileErrorLog();
                }

                var log = JsonUtility.FromJson<CompileErrorLog>(File.ReadAllText(path));
                return log ?? new CompileErrorLog();
            }
            catch
            {
                return new CompileErrorLog();
            }
        }

        private static void GetConsoleLogs(CommandParameters parameters, CommandResponse response)
        {
            // Reads the actual Editor console via the internal LogEntries API. This is
            // version-specific reflection, so every step is guarded: a reflection miss
            // degrades to an empty result with a note instead of throwing.
            var typeFilter = string.IsNullOrEmpty(parameters.logType) ? "all" : parameters.logType.ToLowerInvariant();
            var maxCount = parameters.maxCount > 0 ? parameters.maxCount : 100;
            var entries = new List<string>();
            var counts = new Dictionary<string, int> { { "error", 0 }, { "warning", 0 }, { "log", 0 } };

            try
            {
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                var logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null)
                {
                    response.AddOutput("reflectionAvailable", "false");
                    response.AddOutput("count", "0");
                    return;
                }

                var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                var startGetting = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var endGetting = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);
                var messageField = logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance);
                var modeField = logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance);
                if (getCount == null || startGetting == null || endGetting == null || getEntry == null ||
                    messageField == null || modeField == null)
                {
                    response.AddOutput("reflectionAvailable", "false");
                    response.AddOutput("count", "0");
                    return;
                }

                var total = (int)getCount.Invoke(null, null);
                startGetting.Invoke(null, null);
                try
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    for (var i = 0; i < total; i++)
                    {
                        getEntry.Invoke(null, new[] { i, entry });
                        var message = messageField.GetValue(entry) as string ?? string.Empty;
                        var mode = (int)modeField.GetValue(entry);
                        var kind = ClassifyLogMode(mode);
                        if (counts.ContainsKey(kind))
                        {
                            counts[kind]++;
                        }

                        if (typeFilter != "all" && typeFilter != kind)
                        {
                            continue;
                        }

                        var firstLine = message.Replace("\r", " ").Split('\n')[0];
                        entries.Add($"[{kind}] {firstLine}");
                    }
                }
                finally
                {
                    endGetting.Invoke(null, null);
                }

                // Most recent entries are last; keep the tail and present newest first.
                if (entries.Count > maxCount)
                {
                    entries = entries.GetRange(entries.Count - maxCount, maxCount);
                }
                entries.Reverse();

                response.AddOutput("reflectionAvailable", "true");
                response.AddOutput("totalEntries", total.ToString());
                response.AddOutput("errorCount", counts["error"].ToString());
                response.AddOutput("warningCount", counts["warning"].ToString());
                response.AddOutput("logCount", counts["log"].ToString());
                response.AddOutput("count", entries.Count.ToString());
                response.AddOutput("logs", string.Join("\n", entries));
            }
            catch (Exception e)
            {
                response.AddOutput("reflectionAvailable", "false");
                response.AddOutput("reflectionError", e.Message);
                response.AddOutput("count", entries.Count.ToString());
                response.AddOutput("logs", string.Join("\n", entries));
            }
        }

        private static string ClassifyLogMode(int mode)
        {
            // Mode is a bitmask of UnityEditor console flags. We only need a coarse
            // error/warning/log split, so we test the well-known error/warning bits.
            const int errorBits = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) |
                (1 << 11) | (1 << 13) | (1 << 15) | (1 << 17);
            const int warningBits = (1 << 7) | (1 << 9) | (1 << 12);
            if ((mode & errorBits) != 0)
            {
                return "error";
            }

            if ((mode & warningBits) != 0)
            {
                return "warning";
            }

            return "log";
        }

        private static void ClearConsole(CommandResponse response)
        {
            try
            {
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                var clear = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
                if (clear == null)
                {
                    response.success = false;
                    response.error = new CommandError { message = "LogEntries.Clear not available." };
                    return;
                }

                clear.Invoke(null, null);
                response.AddOutput("cleared", "true");
            }
            catch (Exception e)
            {
                response.success = false;
                response.error = new CommandError { message = e.Message };
            }
        }

        private static void InspectObject(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("name", target.name);
            response.AddOutput("activeSelf", target.activeSelf.ToString());
            response.AddOutput("activeInHierarchy", target.activeInHierarchy.ToString());
            response.AddOutput("tag", target.tag);
            response.AddOutput("layer", LayerMask.LayerToName(target.layer));
            var pos = target.transform.position;
            response.AddOutput("worldPosition", $"{pos.x:F3},{pos.y:F3},{pos.z:F3}");
            var local = target.transform.localPosition;
            response.AddOutput("localPosition", $"{local.x:F3},{local.y:F3},{local.z:F3}");
            response.AddOutput("childCount", target.transform.childCount.ToString());

            var components = target.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToList();
            response.AddOutput("components", string.Join(",", components));

            var label = GetNguiLabelText(target);
            if (!string.IsNullOrEmpty(label))
            {
                response.AddOutput("labelText", label);
            }

            var collider = target.GetComponent<Collider>();
            if (collider != null)
            {
                var b = collider.bounds;
                response.AddOutput("colliderCenter", $"{b.center.x:F3},{b.center.y:F3},{b.center.z:F3}");
                response.AddOutput("colliderSize", $"{b.size.x:F3},{b.size.y:F3},{b.size.z:F3}");
            }

            var spriteName = GetNguiStringProperty(target, "UISprite", "spriteName");
            if (!string.IsNullOrEmpty(spriteName))
            {
                response.AddOutput("spriteName", spriteName);
            }

            var inputValue = GetNguiStringProperty(target, "UIInput", "value");
            if (inputValue != null)
            {
                response.AddOutput("inputValue", inputValue);
            }
        }

        private static void FindObjects(CommandParameters parameters, CommandResponse response)
        {
            var query = parameters.nameQuery ?? parameters.targetName ?? string.Empty;
            if (string.IsNullOrEmpty(query))
            {
                throw new ArgumentException("find_objects requires nameQuery.");
            }

            var maxCount = parameters.maxCount > 0 ? parameters.maxCount : 50;
            var matches = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .Where(x => parameters.includeInactive || x.activeInHierarchy)
                .Where(x => x.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var lines = new List<string>();
            foreach (var go in matches)
            {
                if (lines.Count >= maxCount)
                {
                    break;
                }

                var hasCollider = go.GetComponent<Collider>() != null ? "\tclickable" : string.Empty;
                var label = GetNguiLabelText(go);
                var text = string.IsNullOrEmpty(label) ? string.Empty : $"\ttext={label}";
                lines.Add($"{GetHierarchyPath(go)}\tactive={go.activeInHierarchy}{hasCollider}{text}");
            }

            response.AddOutput("query", query);
            response.AddOutput("totalMatches", matches.Count.ToString());
            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("objects", string.Join("\n", lines));
        }

        private static void SetActive(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            var value = Require(parameters.value, "value");
            if (!bool.TryParse(value, out var active))
            {
                throw new ArgumentException($"set_active value must be 'true' or 'false', got '{value}'.");
            }

            target.SetActive(active);
            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("activeSelf", target.activeSelf.ToString());
            response.AddOutput("activeInHierarchy", target.activeInHierarchy.ToString());
        }

        private static void SetLabelText(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            var value = parameters.value ?? string.Empty;
            if (!SetNguiStringProperty(target, "UILabel", "text", value))
            {
                response.success = false;
                response.error = new CommandError { message = $"No UILabel component on {GetHierarchyPath(target)}." };
                return;
            }

            EditorUtility.SetDirty(target);
            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("text", GetNguiLabelText(target));
        }

        private static void SetInputText(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            var value = parameters.value ?? string.Empty;
            if (!SetNguiStringProperty(target, "UIInput", "value", value))
            {
                response.success = false;
                response.error = new CommandError { message = $"No UIInput component on {GetHierarchyPath(target)}." };
                return;
            }

            EditorUtility.SetDirty(target);
            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("value", GetNguiStringProperty(target, "UIInput", "value") ?? string.Empty);
        }

        private static void SetSprite(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            var value = parameters.value ?? string.Empty;
            if (!SetNguiStringProperty(target, "UISprite", "spriteName", value))
            {
                response.success = false;
                response.error = new CommandError { message = $"No UISprite component on {GetHierarchyPath(target)}." };
                return;
            }

            EditorUtility.SetDirty(target);
            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("spriteName", GetNguiStringProperty(target, "UISprite", "spriteName") ?? string.Empty);
        }

        private static void SceneInfo(CommandResponse response)
        {
            var active = EditorSceneManager.GetActiveScene();
            response.AddOutput("activeScene", active.path);
            response.AddOutput("activeSceneName", active.name);
            response.AddOutput("isPlaying", Application.isPlaying.ToString());

            var sceneCount = EditorSceneManager.sceneCount;
            var sceneLines = new List<string>();
            for (var i = 0; i < sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                sceneLines.Add($"{scene.name}\t{scene.path}\tloaded={scene.isLoaded}");
            }

            response.AddOutput("loadedSceneCount", sceneCount.ToString());
            response.AddOutput("scenes", string.Join("\n", sceneLines));

            if (active.isLoaded)
            {
                var roots = active.GetRootGameObjects();
                response.AddOutput("rootCount", roots.Length.ToString());
                response.AddOutput("roots", string.Join("\n", roots.Select(r => $"{r.name}\tactive={r.activeInHierarchy}")));
            }
        }

        private static void GetHierarchy(CommandParameters parameters, CommandResponse response)
        {
            var maxDepth = parameters.maxDepth > 0 ? parameters.maxDepth : 4;
            var maxNodes = parameters.maxCount > 0 ? parameters.maxCount : 200;
            var lines = new List<string>();
            var roots = new List<Transform>();

            var target = FindTarget(parameters);
            if (target != null)
            {
                roots.Add(target.transform);
            }
            else
            {
                var active = EditorSceneManager.GetActiveScene();
                if (active.isLoaded)
                {
                    foreach (var go in active.GetRootGameObjects())
                    {
                        roots.Add(go.transform);
                    }
                }
            }

            foreach (var root in roots)
            {
                WalkHierarchy(root, 0, maxDepth, maxNodes, lines);
                if (lines.Count >= maxNodes)
                {
                    break;
                }
            }

            response.AddOutput("rootCount", roots.Count.ToString());
            response.AddOutput("maxDepth", maxDepth.ToString());
            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("truncated", (lines.Count >= maxNodes).ToString());
            response.AddOutput("hierarchy", string.Join("\n", lines));
        }

        private static void WalkHierarchy(Transform transform, int depth, int maxDepth, int maxNodes, List<string> lines)
        {
            if (lines.Count >= maxNodes)
            {
                return;
            }

            var go = transform.gameObject;
            var indent = new string(' ', depth * 2);
            var clickable = go.GetComponent<Collider>() != null ? " [clickable]" : string.Empty;
            var label = GetNguiLabelText(go);
            var text = string.IsNullOrEmpty(label) ? string.Empty : $" \"{label}\"";
            lines.Add($"{indent}{go.name}\tactive={go.activeInHierarchy}{clickable}{text}");

            if (depth >= maxDepth)
            {
                return;
            }

            for (var i = 0; i < transform.childCount; i++)
            {
                WalkHierarchy(transform.GetChild(i), depth + 1, maxDepth, maxNodes, lines);
                if (lines.Count >= maxNodes)
                {
                    return;
                }
            }
        }

        private static string GetNguiStringProperty(GameObject go, string componentName, string propertyName)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component.GetType().Name != componentName)
                {
                    continue;
                }

                var property = component.GetType().GetProperty(propertyName);
                if (property != null && property.PropertyType == typeof(string))
                {
                    return property.GetValue(component, null) as string ?? string.Empty;
                }
            }

            return null;
        }

        private static bool SetNguiStringProperty(GameObject go, string componentName, string propertyName, string value)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component.GetType().Name != componentName)
                {
                    continue;
                }

                var property = component.GetType().GetProperty(propertyName);
                if (property != null && property.CanWrite && property.PropertyType == typeof(string))
                {
                    property.SetValue(component, value, null);
                    return true;
                }
            }

            return false;
        }

        private static CommandRequest ToCommandRequest(BatchCommand command, int index)
        {
            if (command == null)
            {
                throw new InvalidOperationException($"batch step {index} is missing.");
            }

            return new CommandRequest
            {
                id = string.IsNullOrEmpty(command.id) ? $"step{index}" : command.id,
                command = command.command,
                parameters = new CommandParameters
                {
                    outputPath = command.outputPath,
                    cameraName = command.cameraName,
                    width = command.width,
                    height = command.height,
                    scenePath = command.scenePath,
                    prefabPath = command.prefabPath,
                    targetName = command.targetName,
                    targetPath = command.targetPath,
                    includeInactive = command.includeInactive,
                    includeOffscreen = command.includeOffscreen,
                    actionableOnly = command.actionableOnly,
                    pointX = command.pointX,
                    pointY = command.pointY,
                    x = command.x,
                    y = command.y,
                    clickX = command.clickX,
                    clickY = command.clickY,
                    text = command.text,
                    labelText = command.labelText,
                    targetText = command.targetText,
                    expectHitContains = command.expectHitContains,
                    logType = command.logType,
                    maxCount = command.maxCount,
                    maxDepth = command.maxDepth,
                    value = command.value,
                    nameQuery = command.nameQuery
                }
            };
        }

        private static CommandStepResponse ToStepResponse(CommandResponse response)
        {
            return new CommandStepResponse
            {
                id = response.id,
                command = response.command,
                success = response.success,
                elapsedMs = response.elapsedMs,
                logs = response.logs,
                outputs = response.outputs,
                error = response.error
            };
        }

        private static void CaptureScreenshot(CommandParameters parameters, CommandResponse response)
        {
            var outputPath = Require(parameters.outputPath, "outputPath");
            var width = parameters.width > 0 ? parameters.width : 1280;
            var height = parameters.height > 0 ? parameters.height : 720;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            if (Application.isPlaying)
            {
                var screenTexture = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    File.WriteAllBytes(outputPath, screenTexture.EncodeToPNG());
                    response.AddOutput("mode", "screenCapture");
                    response.AddOutput("width", screenTexture.width.ToString());
                    response.AddOutput("height", screenTexture.height.ToString());
                }
                finally
                {
                    Object.DestroyImmediate(screenTexture);
                }

                var playInfo = new FileInfo(outputPath);
                response.AddOutput("outputPath", outputPath);
                response.AddOutput("pngBytes", playInfo.Exists ? playInfo.Length.ToString() : "0");
                return;
            }

            var camera = FindCamera(parameters.cameraName);
            if (camera == null)
            {
                throw new InvalidOperationException("No camera found for screenshot capture.");
            }

            var renderTexture = new RenderTexture(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(renderTexture);
            }

            var info = new FileInfo(outputPath);
            response.AddOutput("outputPath", outputPath);
            response.AddOutput("pngBytes", info.Exists ? info.Length.ToString() : "0");
        }

        private static void OpenScene(CommandParameters parameters, CommandResponse response)
        {
            var scenePath = Require(parameters.scenePath, "scenePath");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            response.AddOutput("scenePath", scene.path);
            response.AddOutput("sceneName", scene.name);
        }

        private static void LoadPrefab(CommandParameters parameters, CommandResponse response)
        {
            var prefabPath = Require(parameters.prefabPath, "prefabPath");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new FileNotFoundException($"Prefab not found: {prefabPath}");
            }

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException($"Failed to instantiate prefab: {prefabPath}");
            }

            Selection.activeGameObject = instance;
            response.AddOutput("prefabPath", prefabPath);
            response.AddOutput("instancePath", GetHierarchyPath(instance));
        }

        private static void FindNguiObject(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = "NGUI target object not found"
                };
                return;
            }

            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("name", target.name);
            response.AddOutput("components", string.Join(",", target.GetComponents<Component>().Select(x => x.GetType().Name)));
        }

        private static void ClickNguiObject(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                throw new InvalidOperationException("NGUI target object not found.");
            }

            if (!NotifyNgui(target, "OnClick", null))
            {
                target.SendMessage("OnClick", null, SendMessageOptions.DontRequireReceiver);
                response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
            }

            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("clicked", "true");
        }

        private static void ClickAt(CommandParameters parameters, CommandResponse response)
        {
            var pointX = ResolvePointX(parameters);
            var pointY = ResolvePointY(parameters);
            var screenX = Mathf.Clamp01(pointX) * Screen.width;
            var screenY = (1f - Mathf.Clamp01(pointY)) * Screen.height;
            var screenPos = new Vector3(screenX, screenY, 0f);
            response.AddOutput("screenPos", $"{screenX:F1},{screenY:F1}");
            response.AddOutput("normalizedPos", $"{pointX:F3},{pointY:F3}");
            response.AddOutput("screenSize", $"{Screen.width}x{Screen.height}");
            response.AddOutput("isPlaying", Application.isPlaying.ToString());

            var target = NguiRaycast(screenPos);
            if (target == null)
            {
                target = PhysicsPick(parameters, response, pointX, pointY);
            }

            if (target == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No UI target hit at normalized ({pointX}, {pointY})."
                };
                return;
            }

            // Optional guard against silent mis-clicks: when the caller knows which
            // element it intends to hit (e.g. from a prior dump_ui), it can pass
            // expectHitContains. If the actual raycast hit does not contain that
            // substring (path or name), the target screen probably is not ready yet
            // and the click would land on a background. Do NOT click; report the
            // mismatch so QA can wait and retry instead of trusting a false success.
            var hitPath = GetHierarchyPath(target);
            var expect = parameters.expectHitContains;
            if (!string.IsNullOrEmpty(expect) &&
                hitPath.IndexOf(expect, StringComparison.OrdinalIgnoreCase) < 0 &&
                target.name.IndexOf(expect, StringComparison.OrdinalIgnoreCase) < 0)
            {
                response.AddOutput("hitPath", hitPath);
                response.AddOutput("hitName", target.name);
                response.AddOutput("clicked", "false");
                response.AddOutput("expectHitContains", expect);
                response.AddOutput("expectMatched", "false");
                response.success = false;
                response.error = new CommandError
                {
                    message = $"Hit '{target.name}' does not match expected '{expect}' (screen may not be ready)."
                };
                return;
            }

            if (!NotifyNgui(target, "OnClick", null))
            {
                target.SendMessage("OnClick", null, SendMessageOptions.DontRequireReceiver);
                response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
            }

            response.AddOutput("hitPath", hitPath);
            response.AddOutput("hitName", target.name);
            response.AddOutput("clicked", "true");
            if (!string.IsNullOrEmpty(expect))
            {
                response.AddOutput("expectMatched", "true");
            }
        }

        private static void ClickUiText(CommandParameters parameters, CommandResponse response)
        {
            var text = ResolveText(parameters);
            var label = FindLabelByText(text, parameters.includeInactive);
            if (label == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"UI text not found: {text}"
                };
                return;
            }

            var uiCamera = FindUiCamera();
            if (uiCamera == null)
            {
                throw new InvalidOperationException("No camera found for UI text click.");
            }

            var labelScreen = uiCamera.WorldToScreenPoint(label.transform.position);
            var clickTarget = ResolveClickableTarget(label, labelScreen);
            if (clickTarget == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No clickable target found for UI text: {text}"
                };
                response.AddOutput("labelPath", GetHierarchyPath(label));
                return;
            }

            var clickCollider = clickTarget.Target.GetComponent<Collider>();
            var clickWorld = clickCollider != null ? clickCollider.bounds.center : clickTarget.Target.transform.position;
            var clickScreen = uiCamera.WorldToScreenPoint(clickWorld);
            var clickX = clickScreen.x / Screen.width;
            var clickY = 1f - clickScreen.y / Screen.height;

            if (!NotifyNgui(clickTarget.Target, "OnClick", null))
            {
                clickTarget.Target.SendMessage("OnClick", null, SendMessageOptions.DontRequireReceiver);
                response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
            }

            response.AddOutput("text", GetNguiLabelText(label));
            response.AddOutput("labelPath", GetHierarchyPath(label));
            response.AddOutput("hitPath", GetHierarchyPath(clickTarget.Target));
            response.AddOutput("hitName", clickTarget.Target.name);
            response.AddOutput("screenPos", $"{clickScreen.x:F1},{clickScreen.y:F1}");
            response.AddOutput("normalizedPos", $"{clickX:F3},{clickY:F3}");
            response.AddOutput("clickResolution", clickTarget.Resolution);
            if (clickTarget.Distance > 0f)
            {
                response.AddOutput("clickDistance", $"{clickTarget.Distance:F3}");
            }

            response.AddOutput("clicked", "true");
        }

        private static GameObject NguiRaycast(Vector3 screenPos)
        {
            var uiCameraType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UICamera"))
                .FirstOrDefault(type => type != null);
            var method = uiCameraType?.GetMethod("Raycast",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Vector3), typeof(GameObject).MakeByRefType() },
                null);
            if (method == null)
            {
                return null;
            }

            var args = new object[] { screenPos, null };
            var hit = (bool)method.Invoke(null, args);
            return hit ? args[1] as GameObject : null;
        }

        private static GameObject PhysicsPick(CommandParameters parameters,
            CommandResponse response,
            float pointX,
            float pointY)
        {
            var camera = FindCamera(parameters.cameraName);
            if (camera == null)
            {
                return null;
            }

            response.AddOutput("fallbackCamera", camera.name);
            var ray = camera.ViewportPointToRay(new Vector3(
                Mathf.Clamp01(pointX),
                Mathf.Clamp01(1f - pointY),
                0f));
            return Physics.Raycast(ray, out var hit, Mathf.Infinity)
                ? hit.collider.gameObject
                : null;
        }

        private static float ResolvePointX(CommandParameters parameters)
        {
            if (parameters.pointX != 0f)
            {
                return parameters.pointX;
            }

            if (parameters.x != 0f)
            {
                return parameters.x;
            }

            return parameters.clickX;
        }

        private static float ResolvePointY(CommandParameters parameters)
        {
            if (parameters.pointY != 0f)
            {
                return parameters.pointY;
            }

            if (parameters.y != 0f)
            {
                return parameters.y;
            }

            return parameters.clickY;
        }

        private static void SetPlayMode(bool play, CommandResponse response)
        {
            EditorApplication.isPlaying = play;
            response.AddOutput("requestedPlaying", play.ToString());
            response.AddOutput("isPlayingNow", EditorApplication.isPlaying.ToString());
        }

        private static void DumpUi(CommandParameters parameters, CommandResponse response)
        {
            var uiCamera = FindUiCamera();
            var includeOffscreen = parameters.includeOffscreen;
            var actionableOnly = parameters.actionableOnly;
            var lines = new List<string>();
            var omittedOffscreen = 0;
            var omittedNonActionable = 0;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (EditorUtility.IsPersistent(go) || !go.activeInHierarchy)
                {
                    continue;
                }

                var label = GetNguiLabelText(go);
                var collider = go.GetComponent<Collider>();
                if (string.IsNullOrEmpty(label) && collider == null)
                {
                    continue;
                }

                var kind = collider != null ? "clickable" : "label";
                var coord = string.Empty;
                var clickTarget = default(GameObject);
                var clickCoord = string.Empty;
                var resolution = string.Empty;
                if (uiCamera != null)
                {
                    var world = collider != null ? collider.bounds.center : go.transform.position;
                    var screen = uiCamera.WorldToScreenPoint(world);
                    var normalizedX = screen.x / Screen.width;
                    var normalizedY = 1f - screen.y / Screen.height;
                    if (!includeOffscreen && !IsVisibleScreenPoint(normalizedX, normalizedY, screen.z))
                    {
                        omittedOffscreen++;
                        continue;
                    }

                    coord = $"\tx={normalizedX:F3}\ty={normalizedY:F3}";

                    var clickTargetResolution = ResolveClickableTarget(go, screen);
                    clickTarget = clickTargetResolution?.Target;
                    if (clickTarget != null)
                    {
                        resolution = clickTargetResolution.Resolution;
                        if (clickTargetResolution.Resolution == "overlap")
                        {
                            // overlap = this label merely sits over an unrelated widget; it is
                            // not this label's own button. The QA flow treats overlap as
                            // read-only info, so the resolved path/coords are noise. Emit only
                            // the marker to keep dump_ui output small on info-dense screens.
                            clickCoord = "\tclickResolution=overlap";
                        }
                        else
                        {
                            var clickCollider = clickTarget.GetComponent<Collider>();
                            var clickWorld = clickCollider != null ? clickCollider.bounds.center : clickTarget.transform.position;
                            var clickScreen = uiCamera.WorldToScreenPoint(clickWorld);
                            var clickX = clickScreen.x / Screen.width;
                            var clickY = 1f - clickScreen.y / Screen.height;
                            var clickDistance = clickTargetResolution.Distance > 0f
                                ? $"\tclickDistance={clickTargetResolution.Distance:F3}"
                                : string.Empty;
                            clickCoord = $"\tclickPath={GetHierarchyPath(clickTarget)}\tclickX={clickX:F3}\tclickY={clickY:F3}" +
                                $"\tclickResolution={clickTargetResolution.Resolution}{clickDistance}";
                        }
                    }
                }

                // actionableOnly = QA wants just the things it can click. An item is
                // actionable when it has its own collider (kind=clickable) or its label
                // resolves directly to a button (clickResolution=direct). overlap/nearest
                // and pure info labels are dropped to keep the dump small on dense screens.
                if (actionableOnly && kind != "clickable" && resolution != "direct")
                {
                    omittedNonActionable++;
                    continue;
                }

                var text = string.IsNullOrEmpty(label) ? string.Empty : $"\ttext={label}";
                lines.Add($"{GetHierarchyPath(go)}\t{kind}{coord}{clickCoord}{text}");
            }

            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("omittedOffscreen", omittedOffscreen.ToString());
            response.AddOutput("omittedNonActionable", omittedNonActionable.ToString());
            response.AddOutput("includeOffscreen", includeOffscreen.ToString());
            response.AddOutput("actionableOnly", actionableOnly.ToString());
            response.AddOutput("ui", string.Join("\n", lines));
        }

        private static bool IsVisibleScreenPoint(float normalizedX, float normalizedY, float z)
        {
            if (z < 0f)
            {
                return false;
            }

            return normalizedX >= -VisibleBoundsPadding &&
                normalizedX <= 1f + VisibleBoundsPadding &&
                normalizedY >= -VisibleBoundsPadding &&
                normalizedY <= 1f + VisibleBoundsPadding;
        }

        private static GameObject FindClickableTarget(GameObject go, Vector3 screenPos)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                return go;
            }

            var hit = NguiRaycast(screenPos);
            if (hit != null)
            {
                return hit;
            }

            var ancestor = FindClickableAncestor(go);
            return ancestor != null ? ancestor.gameObject : null;
        }

        private static ClickTargetResolution ResolveClickableTarget(GameObject go, Vector3 screenPos)
        {
            // 1. The object itself is clickable.
            if (go.GetComponent<Collider>() != null)
            {
                return new ClickTargetResolution
                {
                    Target = go,
                    Resolution = "direct",
                    Distance = 0f
                };
            }

            // 2. A clickable ancestor — the label is part of this button.
            var ancestor = FindClickableAncestor(go);
            if (ancestor != null)
            {
                return new ClickTargetResolution
                {
                    Target = ancestor.gameObject,
                    Resolution = "direct",
                    Distance = 0f
                };
            }

            // 3. A clickable widget sits under this object's own position.
            //    NGUI often puts the BoxCollider on a child of the button while the
            //    click handler lives on the parent, so the ancestor-collider walk in
            //    step 2 can miss a button that this label genuinely belongs to.
            //    Classify by hierarchy: if the hit is an ancestor of this object the
            //    label is part of that button -> "direct"; otherwise an unrelated
            //    widget merely overlaps it (e.g. a version/copyright label sitting over
            //    a full-screen background button) -> "overlap".
            var hit = NguiRaycast(screenPos);
            if (hit != null)
            {
                return new ClickTargetResolution
                {
                    Target = hit,
                    Resolution = hit == go || IsAncestorOf(hit.transform, go.transform) ? "direct" : "overlap",
                    Distance = 0f
                };
            }

            // 4. Nearest clickable sharing hierarchy with this object.
            return FindNearestClickableTarget(go, screenPos);
        }

        private static ClickTargetResolution FindNearestClickableTarget(GameObject source, Vector3 sourceScreen)
        {
            var uiCamera = FindUiCamera();
            if (uiCamera == null)
            {
                return null;
            }

            var bestTarget = default(GameObject);
            var bestAffinity = -1;
            var bestDistanceSqr = float.MaxValue;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == source || EditorUtility.IsPersistent(go) || !go.activeInHierarchy)
                {
                    continue;
                }

                var label = GetNguiLabelText(go);
                var collider = go.GetComponent<Collider>();
                if (string.IsNullOrEmpty(label) && collider == null)
                {
                    continue;
                }

                var world = collider != null ? collider.bounds.center : go.transform.position;
                var screen = uiCamera.WorldToScreenPoint(world);
                var distanceSqr = GetNormalizedScreenDistanceSqr(sourceScreen, screen);
                if (distanceSqr > FallbackClickMaxNormalizedDistanceSqr)
                {
                    continue;
                }

                var target = FindClickableTarget(go, screen);
                if (target == null || target == source)
                {
                    continue;
                }

                var affinity = CountSharedHierarchy(source.transform, target.transform);
                if (affinity < FallbackClickMinSharedHierarchy)
                {
                    continue;
                }

                if (bestTarget == null || affinity > bestAffinity ||
                    affinity == bestAffinity && distanceSqr < bestDistanceSqr)
                {
                    bestTarget = target;
                    bestAffinity = affinity;
                    bestDistanceSqr = distanceSqr;
                }
            }

            if (bestTarget == null)
            {
                return null;
            }

            return new ClickTargetResolution
            {
                Target = bestTarget,
                Resolution = "nearest",
                Distance = Mathf.Sqrt(bestDistanceSqr)
            };
        }

        private static float GetNormalizedScreenDistanceSqr(Vector3 a, Vector3 b)
        {
            var x = (a.x - b.x) / Screen.width;
            var y = (a.y - b.y) / Screen.height;
            return x * x + y * y;
        }

        private static int CountSharedHierarchy(Transform a, Transform b)
        {
            var aPath = GetTransformPath(a);
            var bPath = GetTransformPath(b);
            var count = 0;
            var max = Math.Min(aPath.Count, bPath.Count);
            for (var i = 0; i < max; i++)
            {
                if (aPath[i] != bPath[i])
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private static List<Transform> GetTransformPath(Transform transform)
        {
            var path = new List<Transform>();
            var current = transform;
            while (current != null)
            {
                path.Add(current);
                current = current.parent;
            }

            path.Reverse();
            return path;
        }

        private static GameObject FindLabelByText(string text, bool includeInactive)
        {
            var labels = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .Where(x => includeInactive || x.activeInHierarchy)
                .Select(x => new
                {
                    Object = x,
                    Text = GetNguiLabelText(x)
                })
                .Where(x => !string.IsNullOrEmpty(x.Text))
                .ToArray();

            var exact = labels.FirstOrDefault(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact.Object;
            }

            var contains = labels.FirstOrDefault(x => x.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            return contains?.Object;
        }

        private static bool IsAncestorOf(Transform ancestor, Transform node)
        {
            if (ancestor == null || node == null)
            {
                return false;
            }

            var current = node.parent;
            while (current != null)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static Collider FindClickableAncestor(GameObject go)
        {
            var current = go.transform.parent;
            while (current != null)
            {
                var collider = current.GetComponent<Collider>();
                if (collider != null && current.gameObject.activeInHierarchy)
                {
                    return collider;
                }

                current = current.parent;
            }

            return null;
        }

        private static Camera FindUiCamera()
        {
            var uiCameraType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UICamera"))
                .FirstOrDefault(type => type != null);
            if (uiCameraType == null)
            {
                return FindCamera(null);
            }

            var uiCameraComponent = Resources.FindObjectsOfTypeAll(uiCameraType)
                .Cast<Component>()
                .FirstOrDefault(x => !EditorUtility.IsPersistent(x) && x.gameObject.activeInHierarchy);
            return uiCameraComponent != null ? uiCameraComponent.GetComponent<Camera>() : FindCamera(null);
        }

        private static string GetNguiLabelText(GameObject go)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component.GetType().Name != "UILabel")
                {
                    continue;
                }

                var textProperty = component.GetType().GetProperty("text");
                var value = textProperty?.GetValue(component, null) as string;
                if (!string.IsNullOrEmpty(value))
                {
                    return value.Replace("\n", " ").Replace("\t", " ");
                }
            }

            return null;
        }

        private static Camera FindCamera(string cameraName)
        {
            var cameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .ToArray();
            if (!string.IsNullOrEmpty(cameraName))
            {
                return cameras.FirstOrDefault(x => x.name == cameraName);
            }

            return Camera.main ?? cameras.FirstOrDefault();
        }

        private static GameObject FindTarget(CommandParameters parameters)
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .Where(x => parameters.includeInactive || x.activeInHierarchy);

            if (!string.IsNullOrEmpty(parameters.targetPath))
            {
                return objects.FirstOrDefault(x => GetHierarchyPath(x) == parameters.targetPath);
            }

            if (!string.IsNullOrEmpty(parameters.targetName))
            {
                return objects.FirstOrDefault(x => x.name == parameters.targetName);
            }

            return null;
        }

        private static string ResolveText(CommandParameters parameters)
        {
            if (!string.IsNullOrEmpty(parameters.text))
            {
                return parameters.text;
            }

            if (!string.IsNullOrEmpty(parameters.labelText))
            {
                return parameters.labelText;
            }

            return Require(parameters.targetText, "text");
        }

        private static bool NotifyNgui(GameObject target, string functionName, object value)
        {
            var uiCameraType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UICamera"))
                .FirstOrDefault(type => type != null);
            var notifyMethod = uiCameraType?.GetMethod("Notify", BindingFlags.Public | BindingFlags.Static);
            if (notifyMethod == null)
            {
                return false;
            }

            notifyMethod.Invoke(null, new[] { target, functionName, value });
            return true;
        }

        private static string GetHierarchyPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"{name} is required.");
            }

            return value;
        }

        private static string GetCommandRoot()
        {
            var fromEnv = Environment.GetEnvironmentVariable("PROJECTM_COMMAND_ROOT");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }

            return Path.Combine(Application.dataPath.Replace("/Assets", ""), ".codex", "unity-commands");
        }

        private static void WriteResponse(CommandResponse response)
        {
            var responsesDir = Path.Combine(GetCommandRoot(), "responses");
            Directory.CreateDirectory(responsesDir);
            var id = string.IsNullOrEmpty(response.id) ? Guid.NewGuid().ToString("N") : response.id;
            var responsePath = Path.Combine(responsesDir, id + ".json");
            File.WriteAllText(responsePath, JsonUtility.ToJson(response, true));
            UnityEngine.Debug.Log($"{LogPrefix} wrote response {responsePath}");
        }

        private static void TryArchiveRequest(string requestPath)
        {
            try
            {
                var processedDir = Path.Combine(GetCommandRoot(), "processed");
                Directory.CreateDirectory(processedDir);
                var archivePath = Path.Combine(processedDir, Path.GetFileName(requestPath));
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                File.Move(requestPath, archivePath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{LogPrefix} failed to archive request: {e.Message}");
            }
        }
    }

    public sealed class ClickTargetResolution
    {
        public GameObject Target;
        public string Resolution;
        public float Distance;
    }

    [Serializable]
    public sealed class CommandRequest
    {
        public string id;
        public string command;
        public string createdAtUtc;
        public CommandParameters parameters;
    }

    [Serializable]
    public sealed class CommandParameters
    {
        public string outputPath;
        public string cameraName;
        public int width;
        public int height;
        public string scenePath;
        public string prefabPath;
        public string targetName;
        public string targetPath;
        public bool includeInactive;
        public bool includeOffscreen;
        public bool actionableOnly;
        public float pointX;
        public float pointY;
        public float x;
        public float y;
        public float clickX;
        public float clickY;
        public string text;
        public string labelText;
        public string targetText;
        public string expectHitContains;
        public string logType;
        public int maxCount;
        public int maxDepth;
        public string value;
        public string nameQuery;
        public List<BatchCommand> commands;
    }

    [Serializable]
    public sealed class BatchCommand
    {
        public string id;
        public string command;
        public string outputPath;
        public string cameraName;
        public int width;
        public int height;
        public string scenePath;
        public string prefabPath;
        public string targetName;
        public string targetPath;
        public bool includeInactive;
        public bool includeOffscreen;
        public bool actionableOnly;
        public float pointX;
        public float pointY;
        public float x;
        public float y;
        public float clickX;
        public float clickY;
        public string text;
        public string labelText;
        public string targetText;
        public string expectHitContains;
        public string logType;
        public int maxCount;
        public int maxDepth;
        public string value;
        public string nameQuery;
    }

    [Serializable]
    public sealed class CompileErrorLog
    {
        public List<string> errors = new List<string>();
    }

    [Serializable]
    public sealed class CommandStepResponse
    {
        public string id;
        public string command;
        public bool success;
        public long elapsedMs;
        public List<string> logs = new List<string>();
        public List<OutputEntry> outputs = new List<OutputEntry>();
        public CommandError error;
    }

    [Serializable]
    public sealed class CommandResponse
    {
        public string id;
        public string command;
        public bool success;
        public long elapsedMs;
        public List<string> logs = new List<string>();
        public List<OutputEntry> outputs = new List<OutputEntry>();
        public CommandError error;
        public List<CommandStepResponse> steps;

        public void AddOutput(string key, string value)
        {
            outputs.Add(new OutputEntry
            {
                key = key,
                value = value
            });
        }
    }

    [Serializable]
    public sealed class OutputEntry
    {
        public string key;
        public string value;
    }

    [Serializable]
    public sealed class CommandError
    {
        public string message;
        public string details;
    }
}
