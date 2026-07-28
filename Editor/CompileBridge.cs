using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Recompilation as a command, and the compiler's verdict as something a caller can read.
    ///
    /// Two problems make this less trivial than "call AssetDatabase.Refresh". First, a refresh ends in a
    /// domain reload that wipes every static in this process, so a result kept in memory is gone by the
    /// time anyone asks for it - the state therefore lives on disk, written as each assembly finishes.
    /// Second, the reload can happen while the bridge is mid-request, so the refresh is deferred to the
    /// next editor tick: the response for editor_refresh is written first, and the caller polls
    /// editor_compile_status afterwards instead of waiting on a request that may never answer.
    ///
    /// The errors reported are the compiler's own CompilerMessages, which is what makes this usable as a
    /// gate: "no errors" means the assemblies really built, not that no exception was thrown.
    /// </summary>
    [InitializeOnLoad]
    internal static class CompileBridge
    {
        internal static readonly string[] SupportedCommands =
        {
            "editor_refresh",
            "editor_compile_status",
        };

        private const int MaxRecordedMessages = 200;

        static CompileBridge()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // A reload lands here with compilation already over, so a state file still claiming
            // "compiling" would strand every poller.
            if (!EditorApplication.isCompiling)
            {
                var state = ReadState();
                if (state.status == "compiling")
                {
                    state.status = "finished";
                    state.finishedAtUtc = UtcNow();
                    state.note = "Completed across a domain reload; per-assembly messages above are the ones recorded before it.";
                    WriteState(state);
                }
            }
        }

        internal static bool TryExecute(string command, CommandParameters p, CommandResponse response)
        {
            switch (command)
            {
                case "editor_refresh": Refresh(p, response); return true;
                case "editor_compile_status": Status(response); return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------------ commands

        /// <summary>
        /// Reimports changed assets and, on request, forces a script recompilation.
        ///
        /// The work is deferred by one editor tick on purpose: a refresh can trigger the domain reload
        /// immediately, and this command's own response has not been written yet at this point.
        /// </summary>
        private static void Refresh(CommandParameters p, CommandResponse response)
        {
            var force = !string.IsNullOrEmpty(p.forceRecompile) && ParseBool(p.forceRecompile);
            var paths = EditorToolBridge.SplitPaths(p.assetPaths).ToList();

            // Clear first, so a poller cannot read the previous run's verdict and call it this one's.
            var state = new CompileState
            {
                status = "requested",
                requestedAtUtc = UtcNow(),
                forced = force,
            };
            WriteState(state);

            // Deferred by one tick, because a refresh can trigger the domain reload that would kill this
            // request before its response is written. EditorApplication.update rather than delayCall:
            // update is the same tick the command bridge itself runs on, so it is known to fire even
            // when the editor is unfocused or a modal window is up - delayCall was observed not to.
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                EditorApplication.update -= once;
                try
                {
                    foreach (var path in paths)
                    {
                        // A targeted reimport is the reliable way to pick up an edit the refresh scan
                        // would not notice on its own, a file inside an installed package for instance.
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.Default);
                    if (force)
                    {
                        CompilationPipeline.RequestScriptCompilation();
                    }
                }
                catch (Exception e)
                {
                    var failed = ReadState();
                    failed.status = "failed";
                    failed.note = "Refresh threw: " + e.Message;
                    failed.finishedAtUtc = UtcNow();
                    WriteState(failed);
                }
            };
            EditorApplication.update += once;

            response.AddOutput("scheduled", "true");
            response.AddOutput("forceRecompile", force ? "true" : "false");
            response.AddOutput("reimportPaths", string.Join(", ", paths));
            response.AddOutput("statePath", StatePath());
            response.AddOutput("note",
                "The refresh runs on the next editor tick, because it can trigger a domain reload that would kill this request. " +
                "Poll editor_compile_status until status is 'finished' or 'idle'; errorCount there is the compiler's own verdict. " +
                "An editor that is not focused can defer compiling until it is - the status says which state it is really in.");
        }


        private static void Status(CommandResponse response)
        {
            var state = ReadState();

            // isCompiling is the live truth and outranks a state file that a reload may have frozen.
            var compiling = EditorApplication.isCompiling;
            var status = compiling ? "compiling" : state.status;
            if (!compiling && (status == "requested" || status == "compiling") && state.finishedAtUtc == null)
            {
                // Requested but nothing started: nothing needed recompiling.
                status = state.status == "requested" ? "idle" : status;
            }

            response.AddOutput("status", status ?? "idle");
            response.AddOutput("isCompiling", compiling ? "true" : "false");
            response.AddOutput("isUpdating", EditorApplication.isUpdating ? "true" : "false");
            response.AddOutput("errorCount", state.errorCount.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("warningCount", state.warningCount.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("assembliesCompiled", state.assemblies.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("assemblies", JsonArray(state.assemblies.Select(Quote)));
            response.AddOutput("messages", MessagesJson(state.messages));
            response.AddOutput("requestedAtUtc", state.requestedAtUtc ?? string.Empty);
            response.AddOutput("startedAtUtc", state.startedAtUtc ?? string.Empty);
            response.AddOutput("finishedAtUtc", state.finishedAtUtc ?? string.Empty);
            response.AddOutput("forced", state.forced ? "true" : "false");
            response.AddOutput("statePath", StatePath());
            if (!string.IsNullOrEmpty(state.note))
            {
                response.AddOutput("note", state.note);
            }
        }

        // ------------------------------------------------------------------ compilation events

        private static void OnCompilationStarted(object context)
        {
            var state = ReadState();
            state.status = "compiling";
            state.startedAtUtc = UtcNow();
            state.finishedAtUtc = null;
            state.errorCount = 0;
            state.warningCount = 0;
            state.assemblies.Clear();
            state.messages.Clear();
            state.note = null;
            WriteState(state);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var state = ReadState();
            var assembly = Path.GetFileNameWithoutExtension(assemblyPath);
            if (!state.assemblies.Contains(assembly))
            {
                state.assemblies.Add(assembly);
            }

            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                if (message.type == CompilerMessageType.Error) state.errorCount++;
                else state.warningCount++;

                // Errors are what a gate acts on, so they are never dropped for warnings.
                if (state.messages.Count < MaxRecordedMessages || message.type == CompilerMessageType.Error)
                {
                    state.messages.Add(new CompileMessage
                    {
                        assembly = assembly,
                        type = message.type == CompilerMessageType.Error ? "error" : "warning",
                        file = message.file,
                        line = message.line,
                        column = message.column,
                        message = message.message,
                    });
                }
            }

            // Written per assembly rather than at the end, because the reload can cut the run short.
            WriteState(state);
        }

        private static void OnCompilationFinished(object context)
        {
            var state = ReadState();
            state.status = "finished";
            state.finishedAtUtc = UtcNow();
            WriteState(state);
        }

        // ------------------------------------------------------------------ state on disk

        internal static string StatePath()
        {
            var dir = Path.Combine(CommandRunner.GetCommandRoot(), "compile");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state.json");
        }

        private static CompileState ReadState()
        {
            try
            {
                var path = StatePath();
                if (!File.Exists(path))
                {
                    return new CompileState();
                }

                var state = JsonUtility.FromJson<CompileState>(File.ReadAllText(path));
                return state ?? new CompileState();
            }
            catch (Exception)
            {
                // A corrupt state file must not take the editor's compile pipeline down with it.
                return new CompileState();
            }
        }

        private static void WriteState(CompileState state)
        {
            try
            {
                File.WriteAllText(StatePath(), JsonUtility.ToJson(state, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ProjectMQaMcp] failed to write compile state: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string MessagesJson(List<CompileMessage> messages)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = messages[i];
                sb.Append("{\"type\":\"").Append(Escape(m.type)).Append('"');
                sb.Append(",\"assembly\":\"").Append(Escape(m.assembly)).Append('"');
                sb.Append(",\"file\":\"").Append(Escape(m.file)).Append('"');
                sb.Append(",\"line\":").Append(m.line.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"column\":").Append(m.column.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"message\":\"").Append(Escape(m.message)).Append("\"}");
            }

            return sb.Append(']').ToString();
        }

        private static string JsonArray(IEnumerable<string> items)
        {
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Quote(string value)
        {
            return "\"" + Escape(value) + "\"";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", " ").Replace("\n", "\\n").Replace("\t", " ");
        }

        private static bool ParseBool(string text)
        {
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.Ordinal);
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        [Serializable]
        private sealed class CompileState
        {
            public string status = "idle";
            public string requestedAtUtc;
            public string startedAtUtc;
            public string finishedAtUtc;
            public bool forced;
            public int errorCount;
            public int warningCount;
            public string note;
            public List<string> assemblies = new List<string>();
            public List<CompileMessage> messages = new List<CompileMessage>();
        }

        [Serializable]
        private sealed class CompileMessage
        {
            public string type;
            public string assembly;
            public string file;
            public int line;
            public int column;
            public string message;
        }
    }
}
