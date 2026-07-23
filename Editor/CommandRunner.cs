using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMQaMcp.Editor
{
    [InitializeOnLoad]
    public static class CommandRunner
    {
        private const string LogPrefix = "[ProjectMQaMcp]";
        private const double PollIntervalSeconds = 1.0;

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

                    throw new NotSupportedException($"Unsupported command: {request.command}");
            }
        }

        private static void AddEditorStatus(CommandResponse response)
        {
            response.AddOutput("projectPath", Application.dataPath.Replace("/Assets", ""));
            response.AddOutput("unityVersion", Application.unityVersion);
            response.AddOutput("isBatchMode", Application.isBatchMode.ToString());
            response.AddOutput("activeScene", EditorSceneManager.GetActiveScene().path);

            // bridgeVersion is how a caller tells whether the package pin it just changed actually took
            // effect: manifest.json can say one commit while UPM is still running an older checkout.
            response.AddOutput("bridgeVersion", EditorToolBridge.BridgeVersion);
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
                .Concat(TestRunnerBridge.SupportedCommands);
        }

        private static void CaptureScreenshot(CommandParameters parameters, CommandResponse response)
        {
            var outputPath = Require(parameters.outputPath, "outputPath");
            var width = parameters.width > 0 ? parameters.width : 1280;
            var height = parameters.height > 0 ? parameters.height : 720;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

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
            recordWidth = parameters.width > 0 ? parameters.width : 1280;
            recordHeight = parameters.height > 0 ? parameters.height : 720;

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

                var camera = FindCamera(recordCameraName);
                if (camera == null || recordRenderTexture == null || recordTexture == null)
                {
                    return;
                }

                var previousTarget = camera.targetTexture;
                var previousActive = RenderTexture.active;
                try
                {
                    camera.targetTexture = recordRenderTexture;
                    RenderTexture.active = recordRenderTexture;
                    camera.Render();
                    recordTexture.ReadPixels(new Rect(0, 0, recordWidth, recordHeight), 0, 0);
                    recordTexture.Apply();
                    var framePath = Path.Combine(recordFramesDir, $"frame_{recordFrameIndex:D5}.png");
                    File.WriteAllBytes(framePath, recordTexture.EncodeToPNG());
                    recordFrameIndex++;
                }
                finally
                {
                    camera.targetTexture = previousTarget;
                    RenderTexture.active = previousActive;
                }
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
        public int maxDepth;

        public string fieldPath;
        public string fieldValue;
        public string methodName;
        public string methodArgs;

        public string targetMode;
        public int entryIndex;
        public float x;
        public float y;
        public int button;
        public int clickCount;
        public string modifiers;
        public string keyCode;
        public string text;

        public string filter;
        public int maxEntries;
        public bool noFlipY;
        public float originX;
        public float originY;

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
