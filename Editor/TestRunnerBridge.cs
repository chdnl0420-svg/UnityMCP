using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

#if PROJECTM_TEST_FRAMEWORK
using UnityEditor.TestTools.TestRunner.Api;
#endif

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Runs Unity Test Framework tests inside the already-open editor and reports results over the bridge.
    ///
    /// The pre-existing CLI test tools spawn a second Unity in batch mode, which cannot work while a normal
    /// editor holds the project lock - exactly the situation MCP work happens in. TestRunnerApi runs in the
    /// live editor instead, so tests can be driven in the same session that just edited a tool.
    ///
    /// A run outlives the request that started it (and PlayMode runs cross a domain reload), so results are
    /// written to disk keyed by run id rather than kept in memory, and callbacks are re-registered on every
    /// load through <see cref="InitializeOnLoadAttribute"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestRunnerBridge
    {
        private const string LogPrefix = "[ProjectMQaMcp]";
        private const string CurrentRunKey = "ProjectMQaMcp.CurrentTestRunId";

        internal static readonly string[] SupportedCommands =
        {
            "run_tests",
            "get_test_results",
            "list_tests",
        };

        static TestRunnerBridge()
        {
#if PROJECTM_TEST_FRAMEWORK
            try
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(new BridgeCallbacks());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{LogPrefix} test callback registration failed: {e.Message}");
            }
#endif
        }

        internal static bool TryExecute(string command, CommandParameters p, CommandResponse response)
        {
            switch (command)
            {
                case "run_tests": RunTests(p, response); return true;
                case "get_test_results": GetTestResults(p, response); return true;
                case "list_tests": ListTests(p, response); return true;
                default: return false;
            }
        }

        internal static string ResultsDirectory()
        {
            var dir = Path.Combine(CommandRunner.GetCommandRoot(), "test-runs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string ResultPath(string runId)
        {
            return Path.Combine(ResultsDirectory(), runId + ".json");
        }

        private static void RunTests(CommandParameters p, CommandResponse response)
        {
#if !PROJECTM_TEST_FRAMEWORK
            throw new InvalidOperationException(
                "Unity Test Framework is not available. Add com.unity.test-framework to the project so the " +
                "PROJECTM_TEST_FRAMEWORK define is set, then recompile.");
#else
            var modeText = string.IsNullOrEmpty(p.testMode) ? "EditMode" : p.testMode.Trim();
            TestMode mode;
            if (string.Equals(modeText, "EditMode", StringComparison.OrdinalIgnoreCase))
            {
                mode = TestMode.EditMode;
            }
            else if (string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                mode = TestMode.PlayMode;
            }
            else
            {
                throw new ArgumentException($"Unknown testMode '{modeText}'. Use EditMode or PlayMode.");
            }

            var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                        Guid.NewGuid().ToString("N").Substring(0, 8);

            var filter = new Filter { testMode = mode };
            var names = SplitList(p.testFilter);
            if (names.Length > 0) filter.testNames = names;

            var assemblies = SplitList(p.assemblyNames);
            if (assemblies.Length > 0) filter.assemblyNames = assemblies;

            var categories = SplitList(p.categoryNames);
            if (categories.Length > 0) filter.categoryNames = categories;

            SessionState.SetString(CurrentRunKey, runId);
            WriteStatus(runId, "running", modeText, null);

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(filter));

            response.AddOutput("runId", runId);
            response.AddOutput("testMode", modeText);
            response.AddOutput("status", "running");
            response.AddOutput("resultPath", ResultPath(runId));
            response.AddOutput("testFilter", p.testFilter ?? string.Empty);
            response.AddOutput("note", "Run started. Poll get_test_results with this runId until status is 'finished'.");
#endif
        }

        private static void GetTestResults(CommandParameters p, CommandResponse response)
        {
            var runId = !string.IsNullOrEmpty(p.runId) ? p.runId : SessionState.GetString(CurrentRunKey, string.Empty);
            if (string.IsNullOrEmpty(runId))
            {
                throw new InvalidOperationException("No runId given and no test run has been started in this editor session.");
            }

            var path = ResultPath(runId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No result file for run '{runId}'. Expected: {path}");
            }

            var json = File.ReadAllText(path);
            response.AddOutput("runId", runId);
            response.AddOutput("resultPath", path);
            response.AddOutput("result", json);

            // Surface the fields a caller branches on without making them parse the payload first.
            response.AddOutput("status", ExtractJsonValue(json, "status"));
            response.AddOutput("passed", ExtractJsonValue(json, "passCount"));
            response.AddOutput("failed", ExtractJsonValue(json, "failCount"));
            response.AddOutput("skipped", ExtractJsonValue(json, "skipCount"));
        }

        private static void ListTests(CommandParameters p, CommandResponse response)
        {
#if !PROJECTM_TEST_FRAMEWORK
            throw new InvalidOperationException(
                "Unity Test Framework is not available. Add com.unity.test-framework to the project so the " +
                "PROJECTM_TEST_FRAMEWORK define is set, then recompile.");
#else
            var modeText = string.IsNullOrEmpty(p.testMode) ? "EditMode" : p.testMode.Trim();
            var mode = string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var collected = new List<string>();
            var done = false;

            api.RetrieveTestList(mode, root =>
            {
                CollectTestNames(root, collected, p.maxEntries > 0 ? p.maxEntries : 2000);
                done = true;
            });

            // RetrieveTestList answers synchronously when the test tree is already built; if it does not,
            // say so plainly rather than reporting an empty list as "no tests".
            response.AddOutput("testMode", modeText);
            response.AddOutput("resolved", done ? "true" : "false");
            response.AddOutput("count", collected.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("tests", "[" + string.Join(",", collected.ToArray()) + "]");
            if (!done)
            {
                response.AddOutput("note", "Test list was still building; call list_tests again.");
            }
#endif
        }

        private static string[] SplitList(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void WriteStatus(string runId, string status, string mode, string extra)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"runId\":\"").Append(EditorToolBridge.Esc(runId)).Append("\",");
            sb.Append("\"status\":\"").Append(EditorToolBridge.Esc(status)).Append("\",");
            sb.Append("\"testMode\":\"").Append(EditorToolBridge.Esc(mode)).Append("\",");
            sb.Append("\"startedAtUtc\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"passCount\":0,\"failCount\":0,\"skipCount\":0,\"inconclusiveCount\":0,");
            sb.Append("\"tests\":[]");
            if (!string.IsNullOrEmpty(extra))
            {
                sb.Append(",\"note\":\"").Append(EditorToolBridge.Esc(extra)).Append('"');
            }
            sb.Append('}');

            File.WriteAllText(ResultPath(runId), sb.ToString());
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var needle = "\"" + key + "\":";
            var index = json.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0)
            {
                return string.Empty;
            }

            var start = index + needle.Length;
            if (start < json.Length && json[start] == '"')
            {
                start++;
                var end = json.IndexOf('"', start);
                return end > start ? json.Substring(start, end - start) : string.Empty;
            }

            var stop = start;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}')
            {
                stop++;
            }

            return json.Substring(start, stop - start).Trim();
        }

#if PROJECTM_TEST_FRAMEWORK
        private static void CollectTestNames(ITestAdaptor node, List<string> into, int limit)
        {
            if (node == null || into.Count >= limit)
            {
                return;
            }

            if (!node.HasChildren)
            {
                into.Add("{\"fullName\":\"" + EditorToolBridge.Esc(node.FullName) +
                         "\",\"name\":\"" + EditorToolBridge.Esc(node.Name) + "\"}");
                return;
            }

            foreach (var child in node.Children)
            {
                CollectTestNames(child, into, limit);
            }
        }

        private sealed class BridgeCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                var runId = SessionState.GetString(CurrentRunKey, string.Empty);
                if (string.IsNullOrEmpty(runId))
                {
                    return;
                }

                WriteStatus(runId, "running", testsToRun != null && testsToRun.HasChildren ? "mixed" : "unknown", null);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var runId = SessionState.GetString(CurrentRunKey, string.Empty);
                if (string.IsNullOrEmpty(runId))
                {
                    return;
                }

                try
                {
                    File.WriteAllText(ResultPath(runId), BuildResultJson(runId, result));
                    UnityEngine.Debug.Log($"{LogPrefix} test run {runId} finished: " +
                                          $"{result.PassCount} passed, {result.FailCount} failed, {result.SkipCount} skipped.");
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"{LogPrefix} failed to write test results: {e}");
                }
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
        }

        private static string BuildResultJson(string runId, ITestResultAdaptor result)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"runId\":\"").Append(EditorToolBridge.Esc(runId)).Append("\",");
            sb.Append("\"status\":\"finished\",");
            sb.Append("\"finishedAtUtc\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"resultState\":\"").Append(EditorToolBridge.Esc(result.ResultState)).Append("\",");
            sb.Append("\"passCount\":").Append(result.PassCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"failCount\":").Append(result.FailCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"skipCount\":").Append(result.SkipCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"inconclusiveCount\":").Append(result.InconclusiveCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"durationSeconds\":").Append(result.Duration.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');

            var leaves = new List<string>();
            CollectResults(result, leaves, 1000);
            sb.Append("\"testCount\":").Append(leaves.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"tests\":[").Append(string.Join(",", leaves.ToArray())).Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static void CollectResults(ITestResultAdaptor node, List<string> into, int limit)
        {
            if (node == null || into.Count >= limit)
            {
                return;
            }

            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    CollectResults(child, into, limit);
                }

                return;
            }

            var sb = new StringBuilder("{");
            sb.Append("\"fullName\":\"").Append(EditorToolBridge.Esc(node.FullName)).Append("\",");
            sb.Append("\"status\":\"").Append(EditorToolBridge.Esc(node.TestStatus.ToString())).Append("\",");
            sb.Append("\"resultState\":\"").Append(EditorToolBridge.Esc(node.ResultState)).Append("\",");
            sb.Append("\"durationSeconds\":").Append(node.Duration.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"message\":\"").Append(EditorToolBridge.Esc(Shorten(node.Message))).Append("\",");
            sb.Append("\"stackTrace\":\"").Append(EditorToolBridge.Esc(Shorten(node.StackTrace))).Append('"');
            sb.Append('}');
            into.Add(sb.ToString());
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 1200 ? text : text.Substring(0, 1200) + "...";
        }
#endif
    }
}
