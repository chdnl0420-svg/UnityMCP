using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectMQaMcp.Editor.Tests
{
    /// <summary>
    /// Coverage for the commands that sit alongside editor_move: the wheel, UI Toolkit element lookup
    /// and the element target mode it feeds, the editor selection, and the compile gate.
    ///
    /// Same approach as EditorMoveTests - the bridge is called directly and MoveProbeWindow reports what
    /// the window actually received, rather than trusting the command's own account of itself.
    /// </summary>
    public sealed class EditorToolCommandTests
    {
        private readonly List<EditorWindow> _opened = new List<EditorWindow>();

        [TearDown]
        public void TearDown()
        {
            foreach (var window in _opened.Where(x => x != null))
            {
                EditorToolBridge.ForgetPointerPosition(window);
                window.Close();
            }

            _opened.Clear();
        }

        // ------------------------------------------------------------------ scroll

        [UnityTest]
        public IEnumerator Scroll_sends_a_wheel_event_and_no_click()
        {
            var probe = OpenProbe();
            yield return null;

            var target = HoverCentre(probe);
            var response = Execute("editor_scroll", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                x = target.x,
                y = target.y,
                coordinateSpace = "host",
                scrollY = 3f,
            });
            yield return null;

            Assert.That(probe.WheelCount, Is.GreaterThanOrEqualTo(1),
                "The window received no WheelEvent. Event log: " + probe.EventTypeLog);
            Assert.That(probe.LastWheelDelta.y, Is.EqualTo(3f).Within(0.5f),
                "The wheel delta must arrive as sent; a scroll built from positions alone moves nothing.");
            Assert.That(probe.DownCount, Is.EqualTo(0), "A scroll must not press a button.");
            Assert.That(probe.UpCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseDragCount, Is.EqualTo(0));

            Assert.That(Output(response, "eventType"), Is.EqualTo("ScrollWheel"));
            Assert.That(Number(response, "scrollY"), Is.EqualTo(3f).Within(0.01f));
            Assert.That(Number(response, "x"), Is.EqualTo(target.x).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator Scroll_rejects_a_zero_delta()
        {
            var probe = OpenProbe();
            yield return null;

            var error = Assert.Throws<ArgumentException>(() => Execute("editor_scroll", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                x = 10f,
                y = 10f,
                coordinateSpace = "host",
            }));

            Assert.That(error.Message, Does.Contain("scrollX"));
            Assert.That(probe.WheelCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ element query

        [UnityTest]
        public IEnumerator Element_query_finds_an_element_by_name_and_reports_where_it_is()
        {
            var probe = OpenProbe();
            yield return null;

            var response = Execute("editor_element_query", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                elementName = "hover-area",
            });

            Assert.That(Output(response, "count"), Is.EqualTo("1"),
                "Exactly one element is named hover-area. Elements: " + Output(response, "elements"));

            var elements = Output(response, "elements");
            var bounds = probe.HoverAreaWorldBound;
            // The reported centre is what an input command would aim at, so it has to match the layout.
            Assert.That(JsonNumber(elements, "centerX"), Is.EqualTo(bounds.center.x).Within(1f));
            Assert.That(JsonNumber(elements, "centerY"), Is.EqualTo(bounds.center.y).Within(1f));
            Assert.That(JsonNumber(elements, "w"), Is.EqualTo(bounds.width).Within(1f));
            Assert.That(elements, Does.Contain("\"visible\":true"));
        }

        [UnityTest]
        public IEnumerator Element_query_returns_nothing_for_a_name_that_is_not_there()
        {
            var probe = OpenProbe();
            yield return null;

            var response = Execute("editor_element_query", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                elementName = "no-such-element",
            });

            Assert.That(Output(response, "count"), Is.EqualTo("0"));
            Assert.That(Output(response, "elements"), Is.EqualTo("[]"));
        }

        [UnityTest]
        public IEnumerator Move_can_aim_at_an_element_instead_of_a_pixel()
        {
            var probe = OpenProbe();
            yield return null;

            var expected = probe.HoverAreaWorldBound.center;
            var response = Execute("editor_move", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "hover-area",
            });
            yield return null;

            Assert.That(Output(response, "resolvedFrom"), Is.EqualTo("element"));
            Assert.That(Output(response, "elementName"), Is.EqualTo("hover-area"));
            Assert.That(Number(response, "x"), Is.EqualTo(expected.x).Within(1f));
            Assert.That(Number(response, "y"), Is.EqualTo(expected.y).Within(1f));

            Assert.That(probe.MoveCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(probe.LastPressedButtons, Is.EqualTo(0));
            Assert.That(probe.LastMousePosition.x, Is.EqualTo(expected.x).Within(1f));
        }

        [UnityTest]
        public IEnumerator Aiming_at_an_element_that_is_not_there_fails_with_what_was_asked_for()
        {
            var probe = OpenProbe();
            yield return null;

            var error = Assert.Throws<InvalidOperationException>(() => Execute("editor_move", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "no-such-element",
            }));

            Assert.That(error.Message, Does.Contain("No UI Toolkit element matched"));
            Assert.That(error.Message, Does.Contain("no-such-element"));
            Assert.That(error.Message, Does.Contain("editor_element_query"));
            Assert.That(probe.MoveCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ selection

        [Test]
        public void Selection_can_be_cleared_and_read_back()
        {
            Execute("editor_selection_set", new CommandParameters());
            var response = Execute("editor_selection_get", new CommandParameters());

            Assert.That(Output(response, "count"), Is.EqualTo("0"));
            Assert.That(Output(response, "objects"), Is.EqualTo("[]"));
        }

        [Test]
        public void Selection_set_fails_loudly_on_a_path_that_does_not_exist()
        {
            var error = Assert.Throws<InvalidOperationException>(() => Execute("editor_selection_set",
                new CommandParameters { assetPaths = "Assets/NoSuchFolder/NoSuchAsset.prefab" }));

            Assert.That(error.Message, Does.Contain("NoSuchAsset.prefab"));
            Assert.That(error.Message, Does.Contain("project-relative"));
        }

        [Test]
        public void Selection_round_trips_a_real_asset()
        {
            var guid = AssetDatabase.FindAssets("t:Object", new[] { "Assets" }).FirstOrDefault();
            if (string.IsNullOrEmpty(guid))
            {
                Assert.Ignore("The project has no assets under Assets/ to select.");
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            Execute("editor_selection_set", new CommandParameters { assetPaths = path });

            var response = Execute("editor_selection_get", new CommandParameters());
            Assert.That(Output(response, "count"), Is.EqualTo("1"));
            Assert.That(Output(response, "activeAssetPath"), Is.EqualTo(path));

            Execute("editor_selection_set", new CommandParameters());
        }

        // ------------------------------------------------------------------ compile gate

        [Test]
        public void Compile_status_reports_the_editor_state_without_starting_a_compile()
        {
            var response = Execute("editor_compile_status", new CommandParameters());

            Assert.That(Output(response, "status"), Is.Not.Empty);
            Assert.That(Output(response, "isCompiling"), Is.EqualTo("false"),
                "A test run cannot be in progress while a compile is.");
            Assert.That(int.Parse(Output(response, "errorCount"), CultureInfo.InvariantCulture),
                Is.GreaterThanOrEqualTo(0));
            Assert.That(Output(response, "messages"), Does.StartWith("["));
            Assert.That(Output(response, "statePath"), Does.Contain("compile"));
        }

        // ------------------------------------------------------------------ helpers

        private MoveProbeWindow OpenProbe()
        {
            var probe = MoveProbeWindow.OpenFloating();
            _opened.Add(probe);
            EditorToolBridge.ForgetPointerPosition(probe);
            probe.ResetCounters();
            EditorToolBridge.RepaintImmediate(probe);
            return probe;
        }

        private static Vector2 HoverCentre(MoveProbeWindow probe)
        {
            var bounds = probe.HoverAreaWorldBound;
            Assert.That(bounds.width, Is.GreaterThan(4f), "The probe's hover area has no layout yet.");
            return new Vector2(Mathf.Round(bounds.center.x), Mathf.Round(bounds.center.y));
        }

        private static CommandResponse Execute(string command, CommandParameters parameters)
        {
            var response = new CommandResponse { command = command };
            Assert.That(EditorToolBridge.TryExecute(command, parameters, response)
                || CompileBridge.TryExecute(command, parameters, response), Is.True,
                command + " is not a supported bridge command.");
            return response;
        }

        private static string Output(CommandResponse response, string key)
        {
            var entry = response.outputs.FirstOrDefault(x => x.key == key);
            Assert.That(entry, Is.Not.Null, "Response has no output '" + key + "'. Outputs: "
                + string.Join(", ", response.outputs.Select(x => x.key).ToArray()));
            return entry.value;
        }

        private static float Number(CommandResponse response, string key)
        {
            return float.Parse(Output(response, key), CultureInfo.InvariantCulture);
        }

        /// <summary>Reads one number out of the first object of a JSON array the bridge produced.</summary>
        private static float JsonNumber(string json, string key)
        {
            var marker = "\"" + key + "\":";
            var start = json.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "No '" + key + "' in " + json);
            start += marker.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.')) end++;
            return float.Parse(json.Substring(start, end - start), CultureInfo.InvariantCulture);
        }
    }
}
