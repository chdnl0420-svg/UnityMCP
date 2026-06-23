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
        private const float FallbackClickMaxNormalizedDistanceSqr = 0.18f;
        private const int FallbackClickMinSharedHierarchy = 3;
        private const float VisibleBoundsPadding = 0.02f;

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
                default:
                    throw new NotSupportedException($"Unsupported command: {request.command}");
            }
        }

        private static void AddEditorStatus(CommandResponse response)
        {
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
                    pointX = command.pointX,
                    pointY = command.pointY,
                    x = command.x,
                    y = command.y,
                    clickX = command.clickX,
                    clickY = command.clickY,
                    text = command.text,
                    labelText = command.labelText,
                    targetText = command.targetText
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

            if (!NotifyNgui(target, "OnClick", null))
            {
                target.SendMessage("OnClick", null, SendMessageOptions.DontRequireReceiver);
                response.logs.Add($"{LogPrefix} UICamera.Notify not found; used SendMessage fallback.");
            }

            response.AddOutput("hitPath", GetHierarchyPath(target));
            response.AddOutput("hitName", target.name);
            response.AddOutput("clicked", "true");
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
            var lines = new List<string>();
            var omittedOffscreen = 0;
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

                var text = string.IsNullOrEmpty(label) ? string.Empty : $"\ttext={label}";
                lines.Add($"{GetHierarchyPath(go)}\t{kind}{coord}{clickCoord}{text}");
            }

            response.AddOutput("count", lines.Count.ToString());
            response.AddOutput("omittedOffscreen", omittedOffscreen.ToString());
            response.AddOutput("includeOffscreen", includeOffscreen.ToString());
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
        public float pointX;
        public float pointY;
        public float x;
        public float y;
        public float clickX;
        public float clickY;
        public string text;
        public string labelText;
        public string targetText;
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
        public float pointX;
        public float pointY;
        public float x;
        public float y;
        public float clickX;
        public float clickY;
        public string text;
        public string labelText;
        public string targetText;
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
