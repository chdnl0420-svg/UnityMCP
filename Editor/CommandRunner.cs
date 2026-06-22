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
                default:
                    throw new NotSupportedException($"Unsupported command: {request.command}");
            }
        }

        private static void AddEditorStatus(CommandResponse response)
        {
            response.AddOutput("projectPath", Application.dataPath.Replace("/Assets", ""));
            response.AddOutput("unityVersion", Application.unityVersion);
            response.AddOutput("isBatchMode", Application.isBatchMode.ToString());
            response.AddOutput("activeScene", EditorSceneManager.GetActiveScene().path);
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
            var camera = FindCamera(parameters.cameraName);
            if (camera == null)
            {
                throw new InvalidOperationException("No camera found for coordinate click.");
            }

            var cameraNames = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .Select(x => x.name);
            response.AddOutput("camera", camera.name);
            response.AddOutput("cameras", string.Join(",", cameraNames));

            var viewportX = Mathf.Clamp01(parameters.pointX);
            var viewportY = Mathf.Clamp01(1f - parameters.pointY);
            var ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
            if (!Physics.Raycast(ray, out var hit, Mathf.Infinity))
            {
                response.success = false;
                response.error = new CommandError
                {
                    message = $"No collider hit at normalized ({parameters.pointX}, {parameters.pointY})."
                };
                return;
            }

            var target = hit.collider.gameObject;
            if (!NotifyNgui(target, "OnClick", null))
            {
                target.SendMessage("OnClick", null, SendMessageOptions.DontRequireReceiver);
                response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
            }

            response.AddOutput("hitPath", GetHierarchyPath(target));
            response.AddOutput("hitName", target.name);
            response.AddOutput("hitPoint", hit.point.ToString("F3"));
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
        public float pointX;
        public float pointY;
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
