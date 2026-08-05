using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        // 10: both command families in one bridge (runtime/NGUI + EditorToolBridge), and the
        // protocol version moved off the "bridgeVersion" key, which now carries build identity.
        private const int BridgeProtocolVersion = 10;
        private const double PollIntervalSeconds = 0.1;
        private const float FallbackClickMaxNormalizedDistanceSqr = 0.18f;
        private const int FallbackClickMinSharedHierarchy = 3;
        private const float VisibleBoundsPadding = 0.02f;

        private static double nextPollTime;
        private static bool isProcessing;

        // --- Frame-sequence recording state (for capturing fast motion a single screenshot misses) ---
        private static bool isRecording;
        private static string recordFramesDir;
        private static int recordFrameIndex;
        private static int recordStride;
        private static int recordStrideCounter;
        private static int recordMaxFrames;
        private static double recordMaxDurationSeconds;
        private static double recordStartTime;
        private static string recordCameraName;
        private static int recordWidth;
        private static int recordHeight;
        private static RenderTexture recordRenderTexture;
        private static Texture2D recordTexture;

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
                case "maximize_game_view":
                case "set_game_view_maximized":
                    MaximizeGameView(parameters, response);
                    break;
                case "set_game_view_resolution":
                    SetGameViewResolution(parameters, response);
                    break;
                case "start_frame_capture":
                    StartFrameCapture(parameters, response);
                    break;
                case "stop_frame_capture":
                    StopFrameCapture(parameters, response);
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
                case "scroll":
                    ScrollAt(parameters, response);
                    break;
                case "drag":
                    DragBetween(parameters, response);
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
                case "get_component":
                    GetComponent(parameters, response);
                    break;
                case "execute_menu_item":
                    ExecuteMenuItemCommand(parameters, response);
                    break;
                case "invoke_static_method":
                    InvokeStaticMethod(parameters, response);
                    break;
                case "list_editor_windows":
                    ListEditorWindows(parameters, response);
                    break;
                case "get_editor_window_info":
                    GetEditorWindowInfo(parameters, response);
                    break;
                case "capture_editor_window":
                    CaptureEditorWindow(parameters, response);
                    break;
                case "get_editor_prefs":
                    GetEditorPrefs(parameters, response);
                    break;
                case "set_editor_prefs":
                    SetEditorPrefs(parameters, response);
                    break;
                case "get_asset_guid":
                    GetAssetGuid(parameters, response);
                    break;
                case "import_asset":
                    ImportAsset(parameters, response);
                    break;
                default:
                    // Editor-tool and test-runner commands live in their own files; the runtime NGUI
                    // commands above stay untouched so existing callers keep working unchanged.
                    if (EditorToolBridge.TryExecute(request.command, parameters, response))
                    {
                        break;
                    }

                    if (TestRunnerBridge.TryExecute(request.command, parameters, response))
                    {
                        break;
                    }

                    if (CompileBridge.TryExecute(request.command, parameters, response))
                    {
                        break;
                    }

                    throw new NotSupportedException($"Unsupported command: {request.command}");
            }
        }

        private static void AddEditorStatus(CommandResponse response)
        {
            // Two different versions, under two different keys on purpose. Outputs travel as a list of
            // pairs that the Node side folds into an object last-wins, so putting both under one key
            // silently hands the caller whichever line happens to run second.

            // Capability gate: which commands this bridge understands.
            response.AddOutput("bridgeProtocolVersion", BridgeProtocolVersion.ToString());
            // Build identity: how a caller tells whether the package pin it just changed actually took
            // effect, since manifest.json can say one commit while UPM still runs an older checkout.
            response.AddOutput("bridgeVersion", EditorToolBridge.BridgeVersion);

            response.AddOutput("projectPath", Application.dataPath.Replace("/Assets", ""));
            response.AddOutput("unityVersion", Application.unityVersion);
            response.AddOutput("isBatchMode", Application.isBatchMode.ToString());
            response.AddOutput("activeScene", EditorSceneManager.GetActiveScene().path);
            response.AddOutput("isPlaying", EditorApplication.isPlaying.ToString());
            response.AddOutput("isCompiling", EditorApplication.isCompiling.ToString());
            response.AddOutput("commands", string.Join(",", SupportedCommands()));
        }

        private static IEnumerable<string> SupportedCommands()
        {
            var builtIn = new[]
            {
                "ping", "editor_status", "capture_screenshot", "capture_game_view",
                "start_frame_capture", "stop_frame_capture", "open_scene", "load_prefab",
                "find_ngui_object", "click_ngui_object",
            };

            return builtIn
                .Concat(EditorToolBridge.SupportedCommands)
                .Concat(TestRunnerBridge.SupportedCommands)
                .Concat(CompileBridge.SupportedCommands);
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

            // CompilerMessage.message already includes the "file(line,col): error CSxxxx:"
            // prefix, so emit it as-is; fall back to a built prefix only if it does not.
            var errors = messages
                .Where(m => m.type == CompilerMessageType.Error)
                .Select(m => string.IsNullOrEmpty(m.file) || m.message.Contains(m.file)
                    ? m.message
                    : $"{m.file}({m.line},{m.column}): {m.message}")
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

                // LogEntries is cleared by Unity's "Clear on Play" setting, so it may return
                // 0 entries during PlayMode even when the game is actively logging. Fall back to
                // scanning the Editor.log file for exception/error lines so PlayMode errors are
                // not silently lost.
                var usedFallback = false;
                if (total == 0 && (typeFilter == "all" || typeFilter == "error"))
                {
                    var fallbackErrors = ScanEditorLogForErrors(maxCount);
                    if (fallbackErrors.Count > 0)
                    {
                        entries.AddRange(fallbackErrors);
                        counts["error"] += fallbackErrors.Count;
                        usedFallback = true;
                    }
                }

                response.AddOutput("reflectionAvailable", "true");
                response.AddOutput("totalEntries", total.ToString());
                if (usedFallback) response.AddOutput("fallbackSource", "editor.log");
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

        private static List<string> ScanEditorLogForErrors(int maxCount)
        {
            var results = new List<string>();
            try
            {
                var logPath = Application.consoleLogPath;
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return results;

                // Read the last 80 KB — enough to capture recent PlayMode errors without
                // loading the entire (potentially very large) log file.
                const int tailBytes = 81920;
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var offset = Math.Max(0, fs.Length - tailBytes);
                    fs.Seek(offset, SeekOrigin.Begin);
                    using var reader = new System.IO.StreamReader(fs);
                    var content = reader.ReadToEnd();
                    foreach (var raw in content.Split('\n'))
                    {
                        var line = raw.TrimEnd();
                        if (IsEditorLogErrorHeader(line))
                        {
                            results.Add("[error] " + line.TrimStart());
                            if (results.Count >= maxCount) break;
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        private static bool IsEditorLogErrorHeader(string line)
        {
            if (line.Length == 0) return false;
            // Stack trace lines start with whitespace — skip them.
            if (char.IsWhiteSpace(line[0])) return false;
            // Skip MCP bridge diagnostics.
            if (line.StartsWith("[ProjectMQaMcp]")) return false;
            // Match common Unity exception/error header patterns.
            return line.Contains("Exception:") ||
                   line.StartsWith("Error ") ||
                   line.StartsWith("Error:") ||
                   line.StartsWith("FATAL ");
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

        private static void GetComponent(CommandParameters parameters, CommandResponse response)
        {
            var target = FindTarget(parameters);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError { message = "Target object not found (pass targetPath or targetName)." };
                return;
            }

            var componentName = parameters.componentName;
            if (string.IsNullOrEmpty(componentName))
            {
                componentName = parameters.value;
            }
            componentName = Require(componentName, "componentName");

            var component = target.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name == componentName);
            if (component == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No component '{componentName}' on {GetHierarchyPath(target)}."
                };
                response.AddOutput("availableComponents", string.Join(",", target.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name)));
                return;
            }

            var type = component.GetType();
            var lines = new List<string>();
            var skipped = 0;

            // Only emit simple, side-effect-free members. Each read is guarded so a
            // throwing Unity property degrades to a skip instead of failing the command.
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!TryFormatMember(field.Name, () => field.GetValue(component), lines))
                {
                    skipped++;
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!TryFormatMember(property.Name, () => property.GetValue(component, null), lines))
                {
                    skipped++;
                }
            }

            response.AddOutput("component", componentName);
            response.AddOutput("path", GetHierarchyPath(target));
            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("skipped", skipped.ToString());
            response.AddOutput("fields", string.Join("\n", lines));
        }

        // --- Editor tool QA commands ---------------------------------------------------------
        // These drive Unity's Editor layer (menus, tool logic, EditorPrefs, AssetDatabase) so
        // editor-tool CLs can be QA'd headlessly. The bridge runs on the main thread from
        // EditorApplication.update, so anything that opens a modal dialog (EditorUtility.Display*
        // or a native OpenFilePanel) would block this poll loop. The intended pattern is to skip
        // the dialog and drive the underlying logic directly via invoke_static_method.

        private static void ExecuteMenuItemCommand(CommandParameters parameters, CommandResponse response)
        {
            var menuPath = Require(parameters.menuItemPath, "menuItemPath");
            var executed = EditorApplication.ExecuteMenuItem(menuPath);
            response.AddOutput("menuItemPath", menuPath);
            response.AddOutput("executed", executed.ToString());
            if (!executed)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"ExecuteMenuItem returned false for '{menuPath}' (menu item not found or currently disabled)."
                };
            }
        }

        // Reflection-invokes a public OR non-public static method with string arguments converted
        // to each parameter's type. This is the key that lets QA bypass a modal dialog: instead of
        // clicking "OK" in a DisplayDialogComplex, call the static logic the dialog would have run
        // (e.g. BuildTool.Build(2) for "mode=2 / skip reverse-dependency check") directly.
        private static void InvokeStaticMethod(CommandParameters parameters, CommandResponse response)
        {
            var typeName = Require(parameters.typeName, "typeName");
            var methodName = Require(parameters.methodName, "methodName");
            var args = parameters.methodArgs ?? new List<string>();

            var type = ResolveType(typeName);
            if (type == null)
            {
                throw new InvalidOperationException($"Type not found: {typeName}");
            }

            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var candidates = type.GetMethods(staticFlags)
                .Where(m => m.Name == methodName && m.GetParameters().Length == args.Count)
                .ToList();
            if (candidates.Count == 0)
            {
                var arities = type.GetMethods(staticFlags)
                    .Where(m => m.Name == methodName)
                    .Select(m => m.GetParameters().Length + "-arg")
                    .Distinct();
                throw new InvalidOperationException(
                    $"No static method '{methodName}' taking {args.Count} argument(s) on {type.FullName}. " +
                    $"Overloads found: {string.Join(", ", arities)}");
            }

            MethodInfo chosen = null;
            object[] converted = null;
            var conversionErrors = new List<string>();
            foreach (var candidate in candidates)
            {
                if (TryConvertArgs(candidate.GetParameters(), args, out var values, out var err))
                {
                    chosen = candidate;
                    converted = values;
                    break;
                }

                conversionErrors.Add(err);
            }

            if (chosen == null)
            {
                throw new InvalidOperationException(
                    $"Could not bind arguments for '{methodName}': {string.Join("; ", conversionErrors)}");
            }

            var result = chosen.Invoke(null, converted);
            response.AddOutput("type", type.FullName);
            response.AddOutput("method", methodName);
            response.AddOutput("argCount", args.Count.ToString());
            response.AddOutput("returnType", chosen.ReturnType.Name);
            response.AddOutput("returnValue", result == null ? "null" : result.ToString());
        }

        private static bool TryConvertArgs(ParameterInfo[] paramInfos, List<string> args, out object[] values, out string error)
        {
            values = new object[paramInfos.Length];
            error = null;
            for (var i = 0; i < paramInfos.Length; i++)
            {
                var targetType = paramInfos[i].ParameterType;
                try
                {
                    values[i] = ConvertArg(args[i], targetType);
                }
                catch (Exception e)
                {
                    error = $"arg {i} ('{args[i]}') -> {targetType.Name}: {e.Message}";
                    return false;
                }
            }

            return true;
        }

        private static object ConvertArg(string raw, Type targetType)
        {
            if (targetType == typeof(string)) return raw;
            if (targetType.IsEnum) return Enum.Parse(targetType, raw, true);
            if (targetType == typeof(bool)) return bool.Parse(raw);
            if (targetType == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(byte)) return byte.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }

        private static Type ResolveType(string typeName)
        {
            var direct = Type.GetType(typeName);
            if (direct != null) return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var t = assembly.GetType(typeName);
                if (t != null) return t;
            }

            // Fall back to a full-name / simple-name scan so callers do not need the
            // assembly-qualified name for editor tool types.
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                var match = types.FirstOrDefault(x => x.FullName == typeName || x.Name == typeName);
                if (match != null) return match;
            }

            return null;
        }

        private static void ListEditorWindows(CommandParameters parameters, CommandResponse response)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var lines = new List<string>();
            foreach (var window in windows)
            {
                if (window == null) continue;
                var title = window.titleContent != null ? window.titleContent.text : string.Empty;
                var pos = window.position;
                lines.Add($"{window.GetType().FullName}\ttitle={title}\trect={pos.x:F0},{pos.y:F0},{pos.width:F0},{pos.height:F0}\thasFocus={window.hasFocus}");
            }

            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("windows", string.Join("\n", lines));
        }

        private static void GetEditorWindowInfo(CommandParameters parameters, CommandResponse response)
        {
            var typeName = Require(parameters.typeName, "typeName");
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null && (w.GetType().FullName == typeName || w.GetType().Name == typeName))
                .ToList();
            if (windows.Count == 0)
            {
                response.AddOutput("open", "false");
                response.success = false;
                response.error = new CommandError { message = $"No open EditorWindow matching '{typeName}'." };
                return;
            }

            var window = windows[0];
            var pos = window.position;
            response.AddOutput("open", "true");
            response.AddOutput("type", window.GetType().FullName);
            response.AddOutput("title", window.titleContent != null ? window.titleContent.text : string.Empty);
            response.AddOutput("rect", $"{pos.x:F1},{pos.y:F1},{pos.width:F1},{pos.height:F1}");
            response.AddOutput("hasFocus", window.hasFocus.ToString());
            response.AddOutput("matchCount", windows.Count.ToString());
        }

        // Captures the pixels of an Editor tool window (not the game camera) by reading the
        // desktop screen region the window occupies. Coordinate math is DPI-aware but assumes the
        // window is on the primary display; the computed read rect is returned so callers can
        // eyeball-verify the capture. Best-effort: minor offsets on multi-monitor setups are
        // possible and detectable from the output image.
        private static void CaptureEditorWindow(CommandParameters parameters, CommandResponse response)
        {
            var typeName = Require(parameters.typeName, "typeName");
            var outputPath = Require(parameters.outputPath, "outputPath");
            var window = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .FirstOrDefault(w => w != null && (w.GetType().FullName == typeName || w.GetType().Name == typeName));
            if (window == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No open EditorWindow matching '{typeName}'. Open it first (e.g. execute_menu_item)."
                };
                return;
            }

            window.Focus();
            window.Repaint();

            var ppp = EditorGUIUtility.pixelsPerPoint;
            var pos = window.position; // top-left origin, in points, editor screen space
            var widthPx = Mathf.Max(1, Mathf.RoundToInt(pos.width * ppp));
            var heightPx = Mathf.Max(1, Mathf.RoundToInt(pos.height * ppp));

            // ReadScreenPixel reads the OS desktop with a bottom-left origin in pixels, while
            // EditorWindow.position is top-left origin in points; flip Y using the desktop height.
            var screenHeightPx = Screen.currentResolution.height;
            var pixelX = pos.x * ppp;
            var pixelY = screenHeightPx - (pos.y + pos.height) * ppp;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
                new Vector2(pixelX, pixelY), widthPx, heightPx);
            if (pixels == null || pixels.Length < widthPx * heightPx)
            {
                response.success = false;
                response.error = new CommandError { message = "ReadScreenPixel returned insufficient pixel data." };
                return;
            }

            var texture = new Texture2D(widthPx, heightPx, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply();
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            var info = new FileInfo(outputPath);
            response.AddOutput("type", window.GetType().FullName);
            response.AddOutput("outputPath", outputPath);
            response.AddOutput("width", widthPx.ToString());
            response.AddOutput("height", heightPx.ToString());
            response.AddOutput("pixelsPerPoint", ppp.ToString("F2"));
            response.AddOutput("readOrigin", $"{pixelX:F0},{pixelY:F0}");
            response.AddOutput("pngBytes", info.Exists ? info.Length.ToString() : "0");
        }

        private static void GetEditorPrefs(CommandParameters parameters, CommandResponse response)
        {
            var key = Require(parameters.prefsKey, "prefsKey");
            var type = (parameters.prefsType ?? "string").ToLowerInvariant();
            var hasKey = EditorPrefs.HasKey(key);
            string value;
            switch (type)
            {
                case "int": value = EditorPrefs.GetInt(key).ToString(); break;
                case "float": value = EditorPrefs.GetFloat(key).ToString(CultureInfo.InvariantCulture); break;
                case "bool": value = EditorPrefs.GetBool(key).ToString(); break;
                case "string": value = EditorPrefs.GetString(key); break;
                default: throw new ArgumentException($"prefsType must be string|int|float|bool, got '{type}'.");
            }

            response.AddOutput("key", key);
            response.AddOutput("type", type);
            response.AddOutput("hasKey", hasKey.ToString());
            response.AddOutput("value", value);
        }

        private static void SetEditorPrefs(CommandParameters parameters, CommandResponse response)
        {
            var key = Require(parameters.prefsKey, "prefsKey");
            var type = (parameters.prefsType ?? "string").ToLowerInvariant();
            var raw = parameters.prefsValue ?? string.Empty;
            switch (type)
            {
                case "int": EditorPrefs.SetInt(key, int.Parse(raw, CultureInfo.InvariantCulture)); break;
                case "float": EditorPrefs.SetFloat(key, float.Parse(raw, CultureInfo.InvariantCulture)); break;
                case "bool": EditorPrefs.SetBool(key, bool.Parse(raw)); break;
                case "string": EditorPrefs.SetString(key, raw); break;
                default: throw new ArgumentException($"prefsType must be string|int|float|bool, got '{type}'.");
            }

            response.AddOutput("key", key);
            response.AddOutput("type", type);
            response.AddOutput("value", raw);
            response.AddOutput("set", "true");
        }

        private static void GetAssetGuid(CommandParameters parameters, CommandResponse response)
        {
            var assetPath = Require(parameters.assetPath, "assetPath");
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            response.AddOutput("assetPath", assetPath);
            response.AddOutput("guid", guid ?? string.Empty);
            response.AddOutput("exists", string.IsNullOrEmpty(guid) ? "false" : "true");
            if (!string.IsNullOrEmpty(guid))
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                if (type != null) response.AddOutput("assetType", type.Name);
            }
        }

        private static void ImportAsset(CommandParameters parameters, CommandResponse response)
        {
            var assetPath = Require(parameters.assetPath, "assetPath");
            var options = ImportAssetOptions.Default;
            if (parameters.forceUpdate) options |= ImportAssetOptions.ForceUpdate;
            if (parameters.importRecursive) options |= ImportAssetOptions.ImportRecursive;
            AssetDatabase.ImportAsset(assetPath, options);
            response.AddOutput("assetPath", assetPath);
            response.AddOutput("forceUpdate", parameters.forceUpdate.ToString());
            response.AddOutput("recursive", parameters.importRecursive.ToString());
            response.AddOutput("imported", "true");
            response.AddOutput("isPlaying", Application.isPlaying.ToString());
        }

        private static bool TryFormatMember(string name, Func<object> read, List<string> lines)
        {
            try
            {
                var value = read();
                if (!TryFormatSimpleValue(value, out var formatted))
                {
                    return false;
                }

                lines.Add($"{name}={formatted}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFormatSimpleValue(object value, out string formatted)
        {
            formatted = null;
            if (value == null)
            {
                formatted = "null";
                return true;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            {
                formatted = value.ToString();
            }
            else if (value is Vector2 || value is Vector3 || value is Vector4 ||
                value is Quaternion || value is Color || value is Color32 ||
                value is Rect || value is Bounds)
            {
                formatted = value.ToString();
            }
            else
            {
                return false;
            }

            if (formatted != null && formatted.Length > 200)
            {
                formatted = formatted.Substring(0, 200) + "...";
            }

            return true;
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
                    nameQuery = command.nameQuery,
                    componentName = command.componentName,
                    menuItemPath = command.menuItemPath,
                    typeName = command.typeName,
                    methodName = command.methodName,
                    methodArgs = command.methodArgs,
                    prefsKey = command.prefsKey,
                    prefsType = command.prefsType,
                    prefsValue = command.prefsValue,
                    assetPath = command.assetPath,
                    forceUpdate = command.forceUpdate,
                    importRecursive = command.importRecursive
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

        // Renders the live view into a caller-owned RenderTexture by drawing every active camera in
        // depth order (world cameras first, NGUI/UI overlay cameras last), so the result is the full
        // composited frame including UI.
        //
        // Why not ScreenCapture.CaptureScreenshotAsTexture: in the Editor it reads back the editor
        // window framebuffer, not the Game view's own rect. The Game view render sits at an offset
        // inside the docked window, so the readback lands off-target - editor gray on one side, the
        // game clipped off on the other - and that offset shifts with window layout, docking, game
        // view scale and DPI. Rendering cameras into our own RenderTexture is exact, matches
        // Screen.width/height (so normalized click_at coords map 1:1 onto the image), and does not
        // depend on window layout or focus at all.
        private static void RenderCamerasToTexture(RenderTexture target, string cameraName)
        {
            var cameras = string.IsNullOrEmpty(cameraName)
                ? Camera.allCameras.Where(x => x.targetTexture == null).OrderBy(x => x.depth).ToArray()
                : Camera.allCameras.Where(x => x.name == cameraName).ToArray();

            if (cameras.Length == 0)
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(cameraName)
                    ? "No active camera found for capture."
                    : $"No active camera named '{cameraName}' found for capture.");
            }

            var previousActive = RenderTexture.active;
            try
            {
                // Clear once up front: the lowest-depth camera may use ClearFlags.Depth and would
                // otherwise composite onto whatever the texture held before.
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);

                foreach (var camera in cameras)
                {
                    var previousTarget = camera.targetTexture;
                    try
                    {
                        camera.targetTexture = target;
                        camera.Render();
                    }
                    finally
                    {
                        camera.targetTexture = previousTarget;
                    }
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        private static void WriteTextureToPng(RenderTexture source, Texture2D scratch, string outputPath)
        {
            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                scratch.ReadPixels(new Rect(0, 0, scratch.width, scratch.height), 0, 0);
                scratch.Apply();
                File.WriteAllBytes(outputPath, scratch.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        private static void CaptureScreenshot(CommandParameters parameters, CommandResponse response)
        {
            var outputPath = Require(parameters.outputPath, "outputPath");
            // Default to the live Screen size so the capture matches the resolution the game laid its
            // UI out for, which keeps normalized click_at coordinates consistent with the image.
            var width = parameters.width > 0 ? parameters.width
                : (Application.isPlaying && Screen.width > 0 ? Screen.width : 1280);
            var height = parameters.height > 0 ? parameters.height
                : (Application.isPlaying && Screen.height > 0 ? Screen.height : 720);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var renderTexture = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                RenderCamerasToTexture(renderTexture, parameters.cameraName);
                WriteTextureToPng(renderTexture, texture, outputPath);
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(renderTexture);
            }

            response.AddOutput("mode", "cameraComposite");
            response.AddOutput("width", width.ToString());
            response.AddOutput("height", height.ToString());
            var info = new FileInfo(outputPath);
            response.AddOutput("outputPath", outputPath);
            response.AddOutput("pngBytes", info.Exists ? info.Length.ToString() : "0");
        }

        // Maximizes (or restores) the Editor Game view so the running game renders at a larger
        // resolution. This lets screenCapture grab the full UI (detail panels no longer clipped by
        // the small default game view). Screen.width/height update on the next frame, so callers
        // should maximize first and capture_screenshot on a following call.
        private static void MaximizeGameView(CommandParameters parameters, CommandResponse response)
        {
            var gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                throw new InvalidOperationException("UnityEditor.GameView type not found.");
            }

            var window = EditorWindow.GetWindow(gameViewType, false, "Game", true);
            if (window == null)
            {
                throw new InvalidOperationException("Game view window not found.");
            }

            var maximize = !parameters.restore;
            window.maximized = maximize;
            window.Focus();
            window.Repaint();

            response.AddOutput("requestedMaximized", maximize.ToString());
            response.AddOutput("windowMaximized", window.maximized.ToString());
            response.AddOutput("screenSize", Screen.width + "x" + Screen.height);
            response.AddOutput("note", "screenSize updates next frame; call capture_screenshot on a following request.");
        }

        // Sets the Editor Game view to a fixed pixel resolution so the running game renders at that
        // size (Screen.width/height). Unlike maximize (which uses the editor window aspect and can
        // clip tall panels), a fixed square-ish resolution lets screenCapture grab the full UI —
        // tall detail panels and wide trees alike. Adds a reusable custom size and selects it.
        // width/height default to 1440x1440. Screen updates next frame, so capture on a later call.
        private static void SetGameViewResolution(CommandParameters parameters, CommandResponse response)
        {
            int w = parameters.width > 0 ? parameters.width : 1440;
            int h = parameters.height > 0 ? parameters.height : 1440;

            var asm = typeof(EditorWindow).Assembly;
            var sizesType = asm.GetType("UnityEditor.GameViewSizes");
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var instance = singleton.GetProperty("instance").GetValue(null, null);
            var currentGroup = sizesType.GetProperty("currentGroup").GetValue(instance, null);
            var groupType = currentGroup.GetType();

            var gvSizeType = asm.GetType("UnityEditor.GameViewSize");
            var gvSizeTypeEnum = asm.GetType("UnityEditor.GameViewSizeType");
            var fixedRes = System.Enum.Parse(gvSizeTypeEnum, "FixedResolution");
            string label = "QA_" + w + "x" + h;

            var getTotalCount = groupType.GetMethod("GetTotalCount");
            var getGameViewSize = groupType.GetMethod("GetGameViewSize");
            var baseTextProp = gvSizeType.GetProperty("baseText");

            int total = (int)getTotalCount.Invoke(currentGroup, null);
            int idx = -1;
            for (int i = 0; i < total; i++)
            {
                var s = getGameViewSize.Invoke(currentGroup, new object[] { i });
                if ((string)baseTextProp.GetValue(s, null) == label) { idx = i; break; }
            }
            if (idx < 0)
            {
                var ctor = gvSizeType.GetConstructor(new[] { gvSizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                var newSize = ctor.Invoke(new object[] { fixedRes, w, h, label });
                groupType.GetMethod("AddCustomSize").Invoke(currentGroup, new[] { newSize });
                idx = (int)getTotalCount.Invoke(currentGroup, null) - 1;
            }

            var gvWndType = asm.GetType("UnityEditor.GameView");
            var window = EditorWindow.GetWindow(gvWndType, false, "Game", true);
            var sizeCb = gvWndType.GetMethod("SizeSelectionCallback",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (sizeCb == null)
            {
                throw new InvalidOperationException("GameView.SizeSelectionCallback not found (Unity version mismatch).");
            }
            sizeCb.Invoke(window, new object[] { idx, null });
            window.Repaint();

            response.AddOutput("requestedResolution", w + "x" + h);
            response.AddOutput("sizeIndex", idx.ToString());
            response.AddOutput("screenSize", Screen.width + "x" + Screen.height);
            response.AddOutput("note", "screenSize updates next frame; capture on a following request.");
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

        // Mouse-wheel scroll over a normalized screen point.
        // Same path as ClickAt: raycast the point, then hand the event to UICamera.Notify.
        // NGUI widgets subscribe via UIEventListener (onScroll/onDrag/onPress), so notifying
        // the hit object is all a real wheel does. Do NOT look up a scroll component by type
        // name and call its methods directly - custom lists (e.g. UITableView, which derives
        // from MonoBehaviour rather than UIScrollView) do not match, and the driver silently
        // fails on exactly the screens that need scrolling.
        private static void ScrollAt(CommandParameters parameters, CommandResponse response)
        {
            var pointX = ResolvePointX(parameters);
            var pointY = ResolvePointY(parameters);
            var screenPos = new Vector3(Mathf.Clamp01(pointX) * Screen.width,
                (1f - Mathf.Clamp01(pointY)) * Screen.height, 0f);

            var amount = Mathf.Approximately(parameters.amount, 0f) ? 1f : parameters.amount;
            var dir = (parameters.direction ?? "down").ToLowerInvariant();
            // NGUI wheel convention: positive delta scrolls the content toward the top.
            var delta = dir == "up" ? amount : -amount;

            var target = NguiRaycast(screenPos);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No UI target hit at normalized ({pointX}, {pointY}) - nothing to scroll."
                };
                return;
            }

            var steps = parameters.steps > 0 ? parameters.steps : 1;
            for (var i = 0; i < steps; i++)
            {
                if (!NotifyNgui(target, "OnScroll", delta))
                {
                    target.SendMessage("OnScroll", delta, SendMessageOptions.DontRequireReceiver);
                    response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
                }
            }

            response.AddOutput("hitPath", GetHierarchyPath(target));
            response.AddOutput("hitName", target.name);
            response.AddOutput("direction", dir);
            response.AddOutput("delta", delta.ToString("F3"));
            response.AddOutput("steps", steps.ToString());
            response.AddOutput("scrolled", "true");
        }

        // Press-move-release drag between two normalized points, via UICamera.Notify.
        // NGUI delivers a drag as OnPress(true) -> repeated OnDrag(delta) -> OnPress(false).
        // The travel is split into steps because listeners apply momentum per OnDrag; one huge
        // delta scrolls differently than a real swipe of the same distance.
        private static void DragBetween(CommandParameters parameters, CommandResponse response)
        {
            var fromX = ResolvePointX(parameters);
            var fromY = ResolvePointY(parameters);
            var toX = Mathf.Clamp01(parameters.toX);
            var toY = Mathf.Clamp01(parameters.toY);
            var fromScreen = new Vector3(Mathf.Clamp01(fromX) * Screen.width,
                (1f - Mathf.Clamp01(fromY)) * Screen.height, 0f);
            var toScreen = new Vector3(toX * Screen.width, (1f - toY) * Screen.height, 0f);

            var target = NguiRaycast(fromScreen);
            if (target == null)
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No UI target hit at normalized ({fromX}, {fromY}) - nothing to drag."
                };
                return;
            }

            var steps = parameters.steps > 0 ? parameters.steps : 10;
            var total = new Vector2(toScreen.x - fromScreen.x, toScreen.y - fromScreen.y);
            var step = total / steps;

            NotifyNgui(target, "OnPress", true);
            for (var i = 0; i < steps; i++)
            {
                NotifyNgui(target, "OnDrag", step);
            }
            NotifyNgui(target, "OnPress", false);

            response.AddOutput("hitPath", GetHierarchyPath(target));
            response.AddOutput("hitName", target.name);
            response.AddOutput("fromScreen", $"{fromScreen.x:F1},{fromScreen.y:F1}");
            response.AddOutput("toScreen", $"{toScreen.x:F1},{toScreen.y:F1}");
            response.AddOutput("totalDelta", $"{total.x:F1},{total.y:F1}");
            response.AddOutput("steps", steps.ToString());
            response.AddOutput("dragged", "true");
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

            if (clickTarget.Resolution == "overlap")
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"UI text \"{text}\" is an info-only label (overlap) — it sits over an unrelated widget and is not directly clickable. Click the button text instead."
                };
                response.AddOutput("labelPath", GetHierarchyPath(label));
                response.AddOutput("overlapTarget", GetHierarchyPath(clickTarget.Target));
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

            response.AddOutput("text", StripNguiBbCode(GetNguiLabelText(label)));
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

                var strippedLabel = StripNguiBbCode(label);
                var text = string.IsNullOrEmpty(strippedLabel) ? string.Empty : $"\ttext={strippedLabel}";
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

        // Starts capturing the game view camera into a PNG frame sequence on every editor update,
        // so fast motion that a single screenshot round-trip misses is recorded at editor frame rate.
        // Returns immediately; the caller runs the fast action, then calls stop_frame_capture.
        private static void StartFrameCapture(CommandParameters parameters, CommandResponse response)
        {
            // Restart cleanly if a previous recording is still running.
            if (isRecording)
            {
                StopRecordingInternal();
            }

            var framesDir = Require(parameters.framesDir, "framesDir");
            Directory.CreateDirectory(framesDir);

            recordFramesDir = framesDir;
            recordFrameIndex = 0;
            recordStride = parameters.captureEveryNthUpdate > 0 ? parameters.captureEveryNthUpdate : 1;
            recordStrideCounter = 0;
            recordMaxFrames = parameters.maxFrames > 0 ? parameters.maxFrames : 600;
            recordMaxDurationSeconds = parameters.maxDurationSeconds > 0 ? parameters.maxDurationSeconds : 30.0;
            recordStartTime = EditorApplication.timeSinceStartup;
            recordCameraName = parameters.cameraName;
            // Match the live Screen size by default so frames line up with normalized click coords.
            recordWidth = parameters.width > 0 ? parameters.width
                : (Application.isPlaying && Screen.width > 0 ? Screen.width : 1280);
            recordHeight = parameters.height > 0 ? parameters.height
                : (Application.isPlaying && Screen.height > 0 ? Screen.height : 720);

            // Reuse one RenderTexture + Texture2D across all frames to avoid per-frame allocation.
            recordRenderTexture = new RenderTexture(recordWidth, recordHeight, 24);
            recordTexture = new Texture2D(recordWidth, recordHeight, TextureFormat.RGB24, false);

            isRecording = true;
            EditorApplication.update += CaptureRecordingFrame;

            response.AddOutput("recording", "true");
            response.AddOutput("framesDir", framesDir);
            response.AddOutput("captureEveryNthUpdate", recordStride.ToString());
            response.AddOutput("maxFrames", recordMaxFrames.ToString());
            response.AddOutput("maxDurationSeconds", recordMaxDurationSeconds.ToString("F1"));
        }

        // Update-driven capture. Auto-stops on max frames / max duration / errors so a forgotten
        // stop never fills the disk or leaks the update subscription.
        private static void CaptureRecordingFrame()
        {
            if (!isRecording)
            {
                return;
            }

            try
            {
                if (recordFrameIndex >= recordMaxFrames ||
                    (EditorApplication.timeSinceStartup - recordStartTime) >= recordMaxDurationSeconds)
                {
                    StopRecordingInternal();
                    return;
                }

                recordStrideCounter++;
                if (recordStrideCounter < recordStride)
                {
                    return;
                }
                recordStrideCounter = 0;

                if (recordRenderTexture == null || recordTexture == null)
                {
                    return;
                }

                // Composite every camera, not just Camera.main: the world camera alone renders no
                // NGUI UI, so buff/debuff icons, damage numbers and HUD state would be missing from
                // the frames that are supposed to prove an effect fired.
                RenderCamerasToTexture(recordRenderTexture, recordCameraName);
                var framePath = Path.Combine(recordFramesDir, $"frame_{recordFrameIndex:D5}.png");
                WriteTextureToPng(recordRenderTexture, recordTexture, framePath);
                recordFrameIndex++;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{LogPrefix} frame capture error, stopping recording: {e.Message}");
                StopRecordingInternal();
            }
        }

        private static void StopFrameCapture(CommandParameters parameters, CommandResponse response)
        {
            var framesDir = !string.IsNullOrEmpty(recordFramesDir) ? recordFramesDir : parameters.framesDir;
            var frameCount = recordFrameIndex;
            var elapsedSeconds = isRecording ? (EditorApplication.timeSinceStartup - recordStartTime) : 0.0;

            StopRecordingInternal();

            // Prefer the on-disk count as the source of truth.
            if (!string.IsNullOrEmpty(framesDir) && Directory.Exists(framesDir))
            {
                frameCount = Directory.GetFiles(framesDir, "frame_*.png").Length;
            }

            response.AddOutput("recording", "false");
            response.AddOutput("framesDir", framesDir ?? string.Empty);
            response.AddOutput("frameCount", frameCount.ToString());
            response.AddOutput("elapsedSeconds", elapsedSeconds.ToString("F2"));
            response.AddOutput("fps", elapsedSeconds > 0 ? (frameCount / elapsedSeconds).ToString("F1") : "0");
        }

        private static void StopRecordingInternal()
        {
            if (isRecording)
            {
                EditorApplication.update -= CaptureRecordingFrame;
            }
            isRecording = false;

            if (recordRenderTexture != null)
            {
                Object.DestroyImmediate(recordRenderTexture);
                recordRenderTexture = null;
            }
            if (recordTexture != null)
            {
                Object.DestroyImmediate(recordTexture);
                recordTexture = null;
            }
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
                    Text = StripNguiBbCode(GetNguiLabelText(x))
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

        private static string StripNguiBbCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(
                text, @"\[[A-Fa-f0-9]{6}\]|\[-\]|\[/?(i|b|u|s)\]", "");
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

        internal static string GetCommandRoot()
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
        public string framesDir;
        public int captureEveryNthUpdate;
        public int maxFrames;
        public double maxDurationSeconds;
        public bool restore;
        public bool includeOffscreen;
        public bool actionableOnly;
        public float pointX;
        public float pointY;
        public float toX;
        public float toY;
        public float amount;
        public int steps;
        public string direction;
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
        public string componentName;
        // Editor tool QA command fields.
        public string menuItemPath;
        public string typeName;
        public string methodName;
        public List<string> methodArgs;
        public string prefsKey;
        public string prefsType;
        public string prefsValue;
        public string assetPath;
        public bool forceUpdate;
        public bool importRecursive;

        // --- editor tool (EditorWindow) automation ---
        // JsonUtility only fills flat public fields, so every parameter the editor commands accept has to
        // be declared here rather than passed as a free-form dictionary.
        public string windowType;
        public string windowTitle;
        public int instanceId;
        public string menuPath;
        public bool utility;
        public bool noFocus;
        public bool publicOnly;

        public string fieldPath;
        public string fieldValue;
        // editor_invoke_method sends its arguments as one Unit-Separator-joined string.
        // Kept separate from methodArgs, which invoke_static_method fills as a real list.
        public string methodArgsText;

        public string targetMode;
        public int entryIndex;
        // The point for editor_click and editor_move is the x/y pair declared above.
        public int button;
        public int clickCount;
        public string modifiers;
        public string keyCode;

        public string filter;
        public int maxEntries;
        public bool noFlipY;
        public float originX;
        public float originY;

        // --- per-window pixel capture (editor_window_capture / editor_drag_capture) ---
        public string captureBackend;
        public bool includeChrome;
        public int captureSettleMs;
        public bool allowUniform;
        // Composite this process's popups and modal dialogs that sit over the target into the frame,
        // so a confirmation box or a context menu is in the same picture as the window it covers.
        public bool includePopups;

        // --- modal dialogs (editor_dialog_click) ---
        // Which dialog to act on, which button to press, and how long to wait for it to appear. The
        // wait exists because the editor stops ticking while a modal is up, so the watcher has to be
        // armed before the command that raises it.
        public string dialogTitle;
        public string buttonLabel;
        public int buttonIndex;
        public string buttonIndexText;
        public int armMs;
        // What to do when no button matches: "cancel" (default) presses the last button so the editor
        // is handed back, "leave" presses nothing and leaves the editor blocked.
        public string onMiss;

        // --- drag (editor_drag / editor_drag_capture). The to/from target is toX/toY above. ---
        public float fromX;
        public float fromY;
        public int durationMs;
        public int moveStepCount;
        // Shared with editor_move, which uses the same content/host convention as the drag commands.
        public string coordinateSpace;
        // Text rather than bool because the default is true and JsonUtility cannot tell "absent" from "false".
        public string captureEveryMove;

        // --- editor drag and drop (editor_drag_drop) ---
        // How long to hold at the destination before the drop is performed, so the receiver has a frame
        // to paint its highlight. The capture of that highlight is the whole point of the pause.
        public int hoverMs;
        // Text rather than bool: the default is true and JsonUtility cannot tell "absent" from "false".
        public string performDrop;
        // Also hand the drag events to the UI Toolkit element under the point. Default on: an IMGUI-only
        // send never reaches a DragUpdatedEvent callback registered on a VisualElement.
        public string panelEvents;
        // The DragAndDrop.SetGenericData key the receiving tool reads. Naming it turns on the report of
        // whether the source armed the drag, which is the first thing to know when a drop does nothing.
        public string genericDataKey;
        // Payload to stand in with when the source did not arm one. Deserialised into genericDataType,
        // or passed through as a raw string when no type is named.
        public string genericDataJson;
        public string genericDataType;

        // --- UI Toolkit element targeting (editor_element_query, targetMode "element") ---
        public string elementName;
        public string elementClass;
        public string elementType;
        public string elementText;
        public int elementIndex;

        // --- scroll (editor_scroll) ---
        public float scrollX;
        public float scrollY;

        // --- hover (editor_move). Text, because the useful default is true and JsonUtility cannot
        // tell "absent" from "false". ---
        public string ensureWantsMouseMove;

        // --- refresh/compile (editor_refresh) ---
        // Text rather than bool for the same reason captureEveryMove is: JsonUtility cannot express "absent".
        public string forceRecompile;

        // --- selection (editor_selection_set) ---
        public string assetPaths;

        public string prefKey;
        public string prefStore;
        public string prefType;
        public string playModeAction;

        // --- test runner ---
        public string testMode;
        public string testFilter;
        public string assemblyNames;
        public string categoryNames;
        public string runId;
        public bool refresh;

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
        public float toX;
        public float toY;
        public float amount;
        public int steps;
        public string direction;
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
        public string componentName;
        public string menuItemPath;
        public string typeName;
        public string methodName;
        public List<string> methodArgs;
        public string prefsKey;
        public string prefsType;
        public string prefsValue;
        public string assetPath;
        public bool forceUpdate;
        public bool importRecursive;
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
