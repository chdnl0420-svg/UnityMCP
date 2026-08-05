using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEditor;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Answers the handful of bridge commands that need no Unity API, on a thread that is not Unity's.
    ///
    /// The bridge normally reads its requests from <c>EditorApplication.update</c>, which is a
    /// main-thread callback. A modal dialog runs its own message loop on that thread, so while one is
    /// up the callback never fires and requests simply pile up unread. That is why editor_dialog_click
    /// originally had to be armed *before* the dialog appeared - by the time it was on screen, nothing
    /// could take the command that would dismiss it.
    ///
    /// A plain background thread keeps running through all of that; the dialog watcher proved it by
    /// pressing a button while the editor was frozen. So the fix is to read requests from here too.
    /// It cannot be everything: almost every Unity API is main-thread only, so a frozen editor really
    /// cannot answer editor_get_field or compile_status, and no amount of threading changes that.
    /// What it can answer is the set below - all of them Win32 and file IO only - which is exactly the
    /// set that is useful when the editor is stuck: find the dialog, press it, and photograph it.
    ///
    /// Both threads claim a request by renaming it before reading it, so the same request can never be
    /// answered twice. A rename is atomic on Windows, and the loser of a race simply moves on.
    /// </summary>
    internal static class OffThreadBridge
    {
        internal const string ClaimSuffix = ".claim";

        private static readonly HashSet<string> HandledCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "editor_dialog_click",
            "editor_dialog_status",
            "editor_dialog_list",
            "editor_dialog_capture",
        };

        private static string commandRoot;
        private static Thread worker;
        private static volatile bool stopRequested;

        [InitializeOnLoadMethod]
        private static void Start()
        {
            // Resolved here rather than on the thread: GetCommandRoot falls back to Application.dataPath,
            // which is a Unity API.
            commandRoot = CommandRunner.GetCommandRoot();
            stopRequested = false;

            worker = new Thread(Loop)
            {
                IsBackground = true,
                Name = "MCP off-thread bridge",
            };
            worker.Start();

            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        private static void Stop()
        {
            stopRequested = true;
        }

        private static void Loop()
        {
            while (!stopRequested)
            {
                try
                {
                    Sweep();
                }
                catch (Exception)
                {
                    // A bad sweep must not kill the thread - the next one may be the request that
                    // unblocks a frozen editor.
                }

                Thread.Sleep(120);
            }
        }

        private static void Sweep()
        {
            var requestsDir = Path.Combine(commandRoot, "requests");
            if (!Directory.Exists(requestsDir))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(requestsDir, "*.json"))
            {
                string json;
                try
                {
                    json = File.ReadAllText(path);
                }
                catch (Exception)
                {
                    // Still being written, or already claimed by the main thread.
                    continue;
                }

                Dictionary<string, string> fields;
                try
                {
                    fields = ThreadSafeJson.Flatten(json);
                }
                catch (Exception)
                {
                    // Malformed JSON is the main thread's to report, with its better error path.
                    continue;
                }

                string command;
                if (!fields.TryGetValue("command", out command) || !HandledCommands.Contains(command))
                {
                    continue;
                }

                // Read first, claim second: claiming everything would starve the main thread of the
                // commands only it can run.
                var claimed = TryClaim(path);
                if (claimed == null)
                {
                    continue;
                }

                Handle(claimed, fields, command);
            }
        }

        /// <summary>
        /// Takes ownership of a request by renaming it. Returns the new path, or null when someone
        /// else got there first.
        /// </summary>
        internal static string TryClaim(string requestPath)
        {
            var claimed = requestPath + ClaimSuffix;
            try
            {
                File.Move(requestPath, claimed);
                return claimed;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Handle(string claimedPath, Dictionary<string, string> fields, string command)
        {
            var stopwatch = Stopwatch.StartNew();
            string id;
            fields.TryGetValue("id", out id);

            var response = new CommandResponse { id = id ?? string.Empty, command = command };
            response.logs.Add("[ProjectMQaMcp] processing " + command + " off the main thread");

            string errorMessage = null;
            string errorDetails = null;

            try
            {
                var parameters = BuildParameters(fields);

                if (command == "editor_dialog_capture")
                {
                    DialogCapture(parameters, response);
                }
                else if (!EditorDialogBridge.TryExecute(command, parameters, response))
                {
                    throw new NotSupportedException("Unsupported off-thread command: " + command);
                }

                response.success = true;
            }
            catch (Exception e)
            {
                response.success = false;
                errorMessage = e.Message;
                errorDetails = e.ToString();
                response.logs.Add("[ProjectMQaMcp] failed: " + e.Message);
            }
            finally
            {
                stopwatch.Stop();
                WriteResponse(response, stopwatch.ElapsedMilliseconds, errorMessage, errorDetails);
                Archive(claimedPath);
            }
        }

        /// <summary>
        /// Fills only the parameters the off-thread commands read. JsonUtility would do this by
        /// reflection, but it is main-thread only - and a handful of named fields is a small price for
        /// a path that works when nothing else does.
        /// </summary>
        private static CommandParameters BuildParameters(Dictionary<string, string> fields)
        {
            var p = new CommandParameters();

            p.dialogTitle = Get(fields, "dialogTitle");
            p.buttonLabel = Get(fields, "buttonLabel");
            p.buttonIndexText = Get(fields, "buttonIndexText");
            p.onMiss = Get(fields, "onMiss");
            p.armMs = GetInt(fields, "armMs");

            p.outputPath = Get(fields, "outputPath");
            p.windowTitle = Get(fields, "windowTitle");
            var includePopups = Get(fields, "includePopups");
            // Absent means on for this command: a capture taken while a dialog is up that left the
            // dialog out would be answering the wrong question.
            p.includePopups = string.IsNullOrEmpty(includePopups) ||
                              string.Equals(includePopups, "true", StringComparison.OrdinalIgnoreCase);

            return p;
        }

        private static string Get(Dictionary<string, string> fields, string name)
        {
            string value;
            return fields.TryGetValue("parameters." + name, out value) ? value : null;
        }

        private static int GetInt(Dictionary<string, string> fields, string name)
        {
            int parsed;
            var raw = Get(fields, name);
            return !string.IsNullOrEmpty(raw) &&
                   int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0;
        }

        // ------------------------------------------------------------------ the capture

        /// <summary>
        /// Photographs a window and everything this process has stacked on top of it, while the editor
        /// is blocked.
        ///
        /// editor_window_capture cannot do this. Not because of how it captures - PrintWindow is Win32
        /// and would be fine - but because it starts from an EditorWindow, and turning one into an OS
        /// window handle takes Unity. So the subject here is a handle: the one the last normal capture
        /// resolved, or a window named by title, or the largest window of the process.
        /// </summary>
        private static void DialogCapture(CommandParameters p, CommandResponse response)
        {
#if UNITY_EDITOR_WIN
            var outputPath = p.outputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(commandRoot, "screenshots",
                    "editor-dialog-capture-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ".png");
            }

            var basis = "last-capture";
            var hwnd = EditorWindowCapture.LastCapturedHwnd;

            if (!string.IsNullOrEmpty(p.windowTitle))
            {
                hwnd = EditorWindowCapture.FindProcessWindowByTitle(p.windowTitle);
                basis = "title";
                if (hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "No visible window of this process has a title containing '" + p.windowTitle + "'.");
                }
            }
            else
            {
                // With a dialog up, the subject has to be a window the dialog is actually over -
                // otherwise the frame comes back without it and looks identical to "no dialog
                // appeared". The remembered window is used when the dialog is on it; otherwise the
                // biggest window is, which is the main editor one and where a stray modal lands.
                var dialog = EditorWindowCapture.FindProcessDialog();

                if (hwnd == IntPtr.Zero)
                {
                    hwnd = EditorWindowCapture.LargestProcessWindow();
                    basis = "largest-window";
                }
                else if (dialog != IntPtr.Zero && !EditorWindowCapture.WindowsOverlap(dialog, hwnd))
                {
                    var largest = EditorWindowCapture.LargestProcessWindow();
                    if (largest != IntPtr.Zero && largest != hwnd)
                    {
                        hwnd = largest;
                        basis = "largest-window (the dialog is not over the remembered window)";
                    }
                }
            }

            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "No window could be chosen to capture. Name one with windowTitle, or run a normal " +
                    "editor_window_capture first so its handle is remembered.");
            }

            // Reported whether or not it mattered: "the dialog is not in the picture" and "the dialog
            // never opened" look the same in a PNG, and these three lines tell them apart.
            var reportedDialog = EditorWindowCapture.FindProcessDialog();
            response.AddOutput("dialogFound", EditorWindowCapture.DescribeTitle(reportedDialog));
            response.AddOutput("dialogRect", EditorWindowCapture.DescribeRect(reportedDialog));
            response.AddOutput("targetTitle", EditorWindowCapture.DescribeTitle(hwnd));
            response.AddOutput("targetRect", EditorWindowCapture.DescribeRect(hwnd));
            response.AddOutput("dialogOverlapsTarget",
                reportedDialog != IntPtr.Zero && EditorWindowCapture.WindowsOverlap(reportedDialog, hwnd)
                    ? "true" : "false");

            var notes = new List<string>();
            int width, height, composited;
            string titles, failure;

            var ok = EditorWindowCapture.CaptureHandle(hwnd, p.includePopups, outputPath, notes,
                out width, out height, out composited, out titles, out failure);

            response.AddOutput("outputPath", outputPath);
            response.AddOutput("osWindowHandle", "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture));
            response.AddOutput("targetBasis", basis);
            response.AddOutput("imageWidth", width.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("imageHeight", height.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("offThread", "true");

            if (composited >= 0)
            {
                response.AddOutput("popupsComposited", composited.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("popupTitles", titles ?? string.Empty);
            }

            if (notes.Count > 0)
            {
                response.AddOutput("captureNotes", string.Join(" | ", notes.ToArray()));
            }

            if (!ok)
            {
                throw new InvalidOperationException("Off-thread capture failed: " + failure);
            }

            var info = new FileInfo(outputPath);
            response.AddOutput("pngExists", info.Exists ? "true" : "false");
            response.AddOutput("pngBytes", (info.Exists ? info.Length : 0L).ToString(CultureInfo.InvariantCulture));

            if (!info.Exists || info.Length == 0)
            {
                throw new InvalidOperationException("The capture reported success but wrote no bytes to " + outputPath);
            }
#else
            throw new NotSupportedException("editor_dialog_capture needs the Windows editor.");
#endif
        }

        // ------------------------------------------------------------------ response plumbing

        private static void WriteResponse(CommandResponse response, long elapsedMs,
            string errorMessage, string errorDetails)
        {
            try
            {
                var responsesDir = Path.Combine(commandRoot, "responses");
                Directory.CreateDirectory(responsesDir);

                var id = string.IsNullOrEmpty(response.id) ? Guid.NewGuid().ToString("N") : response.id;
                var outputs = new List<KeyValuePair<string, string>>();
                foreach (var entry in response.outputs)
                {
                    outputs.Add(new KeyValuePair<string, string>(entry.key, entry.value));
                }

                var json = ThreadSafeJson.WriteResponse(id, response.command, response.success, elapsedMs,
                    response.logs, outputs, errorMessage, errorDetails);

                // Written beside the target and renamed in, so a reader polling for the file never
                // catches it half-written - the same rule the MCP server writes requests by.
                var finalPath = Path.Combine(responsesDir, id + ".json");
                var tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
                File.Move(tempPath, finalPath);
            }
            catch (Exception)
            {
                // Nothing useful to do from here: Debug.Log is not worth the thread-safety question,
                // and the caller will time out with its own message.
            }
        }

        private static void Archive(string claimedPath)
        {
            try
            {
                var processedDir = Path.Combine(commandRoot, "processed");
                Directory.CreateDirectory(processedDir);

                var name = Path.GetFileName(claimedPath);
                if (name.EndsWith(ClaimSuffix, StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - ClaimSuffix.Length);
                }

                var archivePath = Path.Combine(processedDir, name);
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                File.Move(claimedPath, archivePath);
            }
            catch (Exception)
            {
                // A request left in the folder is recoverable; a thrown exception here is not.
            }
        }
    }
}
