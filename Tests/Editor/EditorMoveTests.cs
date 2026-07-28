using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectMQaMcp.Editor.Tests
{
    /// <summary>
    /// Coverage for editor_move, the button-less pointer move.
    ///
    /// The commands are driven straight through EditorToolBridge rather than through the file bridge:
    /// going the long way round would test the request watcher, while what needs proving here is what
    /// the receiving window actually got. MoveProbeWindow records that from the UI Toolkit side, the
    /// IMGUI side and the GraphView side at once.
    ///
    /// Each test yields a frame after sending, because UI Toolkit is free to dispatch on the next one.
    /// </summary>
    public sealed class EditorMoveTests
    {
        private readonly List<EditorWindow> _opened = new List<EditorWindow>();
        private readonly List<string> _tempFiles = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var window in _opened.Where(x => x != null))
            {
                EditorToolBridge.ForgetPointerPosition(window);
                window.Close();
            }

            _opened.Clear();

            foreach (var path in _tempFiles.Where(File.Exists))
            {
                try { File.Delete(path); } catch (IOException) { /* a locked temp file is not a failure */ }
            }

            _tempFiles.Clear();
        }

        // ------------------------------------------------------------------ the contract

        [UnityTest]
        public IEnumerator Move_delivers_a_MouseMoveEvent_with_no_pressed_button_to_a_floating_window()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            var target = HostPointInHoverArea(probe);
            var response = Move(probe, target.x, target.y, "host");
            yield return null;

            Assert.That(probe.MoveCount, Is.GreaterThanOrEqualTo(1),
                "The window received no MouseMoveEvent. Event log: " + probe.EventTypeLog);
            // The move brings the pointer into the area, so UI Toolkit follows it with MouseEnterEvent -
            // the move must be in the log, but it is not necessarily the last thing in it.
            Assert.That(probe.EventTypeLog, Does.Contain("MouseMoveEvent"));
            Assert.That(probe.LastPressedButtons, Is.EqualTo(0),
                "A hover must arrive with no button held, otherwise it is indistinguishable from a drag.");
            Assert.That(probe.MoveWithPressedButtonsCount, Is.EqualTo(0));

            Assert.That(probe.DownCount, Is.EqualTo(0), "MouseDownEvent must not be sent by a move.");
            Assert.That(probe.UpCount, Is.EqualTo(0), "MouseUpEvent must not be sent by a move.");
            Assert.That(probe.ImguiMouseDownCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseUpCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseDragCount, Is.EqualTo(0), "MouseDrag must not be sent by a move.");
            Assert.That(probe.EventTypeLog, Does.Not.Contain("MouseDownEvent"));
            Assert.That(probe.EventTypeLog, Does.Not.Contain("MouseUpEvent"));

            Assert.That(probe.LastMousePosition.x, Is.EqualTo(target.x).Within(0.5f),
                "The received x must be the requested x.");
            Assert.That(probe.LastMousePosition.y, Is.EqualTo(target.y).Within(0.5f),
                "The received y must be the requested y.");
            // Unity spells "no button" as either 0 or -1 on a move depending on the path taken; what
            // must not happen is a real button index alongside pressedButtons 0.
            Assert.That(probe.LastButton, Is.EqualTo(0).Or.EqualTo(-1));
            Assert.That(probe.IsHovered, Is.True, "The pointer should now be over the hover area.");

            Assert.That(Output(response, "pressedButtons"), Is.EqualTo("0"));
            Assert.That(Output(response, "eventType"), Is.EqualTo("MouseMove"));
            Assert.That(Number(response, "x"), Is.EqualTo(target.x).Within(0.01f));
            Assert.That(Number(response, "y"), Is.EqualTo(target.y).Within(0.01f));
            Assert.That(Output(response, "targetInstanceId"),
                Is.EqualTo(probe.GetInstanceID().ToString(CultureInfo.InvariantCulture)));
            Assert.That(Output(response, "targetWindowType"), Is.EqualTo(typeof(MoveProbeWindow).FullName));
            Assert.That(Output(response, "targetWindowTitle"), Is.EqualTo(probe.titleContent.text));
            Assert.That(Output(response, "sendEventReturned"), Is.EqualTo("true").Or.EqualTo("false"));
        }

        [UnityTest]
        public IEnumerator Move_reaches_a_docked_window_and_content_coordinates_clear_the_tab_strip()
        {
            var probe = OpenDockedProbe();
            yield return null;

            var rootOffset = probe.RootOffset;
            Assert.That(rootOffset.y, Is.GreaterThan(0f),
                "The probe did not dock, so this run would not exercise the tab-strip offset at all. " +
                "rootOffset=" + rootOffset);

            // Content space is the default: 0,0 is the content corner, and the bridge is expected to add
            // the tab strip itself.
            var contentPoint = HostPointInHoverArea(probe) - rootOffset;
            var response = Move(probe, contentPoint.x, contentPoint.y, "content");
            yield return null;

            var expectedHost = contentPoint + rootOffset;

            Assert.That(probe.MoveCount, Is.GreaterThanOrEqualTo(1),
                "The docked window received no MouseMoveEvent. Event log: " + probe.EventTypeLog);
            Assert.That(probe.LastPressedButtons, Is.EqualTo(0));
            Assert.That(probe.DownCount, Is.EqualTo(0));
            Assert.That(probe.UpCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseDragCount, Is.EqualTo(0));

            Assert.That(probe.LastMousePosition.x, Is.EqualTo(expectedHost.x).Within(0.5f));
            Assert.That(probe.LastMousePosition.y, Is.EqualTo(expectedHost.y).Within(0.5f),
                "A docked window's content coordinates must be shifted by the tab strip before sending.");

            Assert.That(Number(response, "contentX"), Is.EqualTo(contentPoint.x).Within(0.01f));
            Assert.That(Number(response, "contentY"), Is.EqualTo(contentPoint.y).Within(0.01f));
            Assert.That(Number(response, "y"), Is.EqualTo(expectedHost.y).Within(0.01f));
            // The offset the bridge applied has to match the layout's own idea of where content starts.
            var contentOffset = Output(response, "contentOffset").Split(',');
            Assert.That(float.Parse(contentOffset[1], CultureInfo.InvariantCulture),
                Is.EqualTo(rootOffset.y).Within(0.5f));
        }

        [UnityTest]
        public IEnumerator The_second_move_in_a_window_carries_the_delta_between_the_two_points()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            var first = HostPointInHoverArea(probe);
            var second = first + new Vector2(37f, 21f);

            var firstResponse = Move(probe, first.x, first.y, "host");
            yield return null;

            Assert.That(Output(firstResponse, "hadPreviousPoint"), Is.EqualTo("false"));
            Assert.That(Number(firstResponse, "deltaX"), Is.EqualTo(0f).Within(0.01f),
                "The first move into a window has no previous point, so it carries no delta.");
            Assert.That(Number(firstResponse, "deltaY"), Is.EqualTo(0f).Within(0.01f));

            var secondResponse = Move(probe, second.x, second.y, "host");
            yield return null;

            Assert.That(Output(secondResponse, "hadPreviousPoint"), Is.EqualTo("true"));
            Assert.That(Number(secondResponse, "deltaX"), Is.EqualTo(37f).Within(0.01f));
            Assert.That(Number(secondResponse, "deltaY"), Is.EqualTo(21f).Within(0.01f));
            Assert.That(Number(secondResponse, "previousX"), Is.EqualTo(first.x).Within(0.01f));

            Assert.That(probe.MoveCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(probe.LastMouseDelta.x, Is.EqualTo(37f).Within(0.5f),
                "The received mouseDelta must be the distance from the previous point, not zero.");
            Assert.That(probe.LastMouseDelta.y, Is.EqualTo(21f).Within(0.5f));
            Assert.That(probe.LastPressedButtons, Is.EqualTo(0),
                "The second move must still be button-less; two moves are not a drag.");
        }

        [UnityTest]
        public IEnumerator Pointer_positions_are_remembered_per_window()
        {
            // Docked first: it resolves through GetWindow, which would otherwise hand back the floating
            // instance and quietly turn this into one window compared against itself.
            var docked = OpenDockedProbe();
            var floating = OpenFloatingProbe();
            yield return null;

            Assert.That(floating.GetInstanceID(), Is.Not.EqualTo(docked.GetInstanceID()),
                "The two probes resolved to the same window, so this test would prove nothing.");

            var floatingPoint = HostPointInHoverArea(floating);
            var dockedPoint = HostPointInHoverArea(docked);

            Move(floating, floatingPoint.x, floatingPoint.y, "host");
            yield return null;

            // A first move into a different window must not inherit the other window's position.
            var dockedFirst = Move(docked, dockedPoint.x, dockedPoint.y, "host");
            yield return null;

            Assert.That(Output(dockedFirst, "hadPreviousPoint"), Is.EqualTo("false"),
                "The second window must start its own pointer path.");
            Assert.That(Number(dockedFirst, "deltaX"), Is.EqualTo(0f).Within(0.01f));
            Assert.That(Number(dockedFirst, "deltaY"), Is.EqualTo(0f).Within(0.01f));

            // And the first window's next move must still measure from its own last point.
            var floatingSecond = Move(floating, floatingPoint.x + 12f, floatingPoint.y + 5f, "host");
            yield return null;

            Assert.That(Output(floatingSecond, "hadPreviousPoint"), Is.EqualTo("true"));
            Assert.That(Number(floatingSecond, "deltaX"), Is.EqualTo(12f).Within(0.01f));
            Assert.That(Number(floatingSecond, "deltaY"), Is.EqualTo(5f).Within(0.01f));
            Assert.That(Number(floatingSecond, "previousX"), Is.EqualTo(floatingPoint.x).Within(0.01f));
        }

        [UnityTest]
        public IEnumerator Move_over_a_graph_starts_no_drag_selection_pan_or_context_menu()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            var nodeBefore = probe.NodePosition;
            var panBefore = probe.PanPosition;

            // Straight across the graph, over the node, which is where a stray press would show up.
            var graphPoints = GraphPathPoints(probe, 6);
            foreach (var point in graphPoints)
            {
                Move(probe, point.x, point.y, "host");
                yield return null;
            }

            Assert.That(probe.NodePosition, Is.EqualTo(nodeBefore),
                "A button-less move must not drag the node.");
            Assert.That(probe.SelectionCount, Is.EqualTo(0),
                "A button-less move must not select anything.");
            Assert.That(probe.PanPosition, Is.EqualTo(panBefore),
                "A button-less move must not pan the view.");
            Assert.That(probe.ContextClickCount, Is.EqualTo(0));
            Assert.That(probe.ContextMenuCount, Is.EqualTo(0),
                "A button-less move must not open a context menu.");
            Assert.That(probe.DownCount, Is.EqualTo(0));
            Assert.That(probe.UpCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseDownCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseDragCount, Is.EqualTo(0));
            Assert.That(probe.ImguiMouseUpCount, Is.EqualTo(0));
            Assert.That(probe.ImguiContextClickCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Window_capture_works_immediately_after_a_move()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            var target = HostPointInHoverArea(probe);
            Move(probe, target.x, target.y, "host");
            yield return null;

            var path = TempPngPath();
            var response = Execute("editor_window_capture", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                outputPath = path,
                captureBackend = "auto",
                captureSettleMs = 24,
            });

            Assert.That(File.Exists(path), Is.True,
                "editor_window_capture wrote no PNG after the move. " + Describe(response));
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(0));
            Assert.That(Output(response, "pngExists"), Is.EqualTo("true"));
        }

        // ------------------------------------------------------------------ failure modes

        [Test]
        public void Move_reports_an_unresolved_window_the_same_way_the_other_commands_do()
        {
            var parameters = new CommandParameters { windowType = "NoSuchEditorWindowType", x = 10f, y = 10f };

            var error = Assert.Throws<InvalidOperationException>(
                () => EditorToolBridge.TryExecute("editor_move", parameters, new CommandResponse()));
            Assert.That(error.Message, Does.Contain("No open EditorWindow matched"));
            Assert.That(error.Message, Does.Contain("NoSuchEditorWindowType"));
        }

        [UnityTest]
        public IEnumerator Move_rejects_a_point_outside_the_window()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            probe.ResetCounters();
            var outside = probe.position.size + new Vector2(200f, 200f);

            var error = Assert.Throws<ArgumentException>(() => Move(probe, outside.x, outside.y, "content"));
            Assert.That(error.Message, Does.Contain("outside"));
            Assert.That(error.Message, Does.Contain("coordinateSpace"));
            Assert.That(probe.MoveCount, Is.EqualTo(0), "A rejected move must send nothing.");
        }

        [UnityTest]
        public IEnumerator Move_rejects_a_non_finite_point_and_an_unknown_coordinate_space()
        {
            var probe = OpenFloatingProbe();
            yield return null;

            Assert.Throws<ArgumentException>(() => Move(probe, float.NaN, 10f, "content"));
            Assert.Throws<ArgumentException>(() => Move(probe, 10f, float.PositiveInfinity, "content"));
            Assert.Throws<ArgumentException>(() => Move(probe, 10f, 10f, "screen"));
            Assert.That(probe.MoveCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ helpers

        private MoveProbeWindow OpenFloatingProbe()
        {
            var probe = MoveProbeWindow.OpenFloating();
            return Prepare(probe);
        }

        private MoveProbeWindow OpenDockedProbe()
        {
            // GetWindow hands back any live instance of the type, including a floating one somebody left
            // open - and then nothing docks and the tab-strip assertions below test nothing. So the
            // field is cleared first, which makes the test independent of what was on screen.
            foreach (var stale in Resources.FindObjectsOfTypeAll<MoveProbeWindow>())
            {
                EditorToolBridge.ForgetPointerPosition(stale);
                _opened.Remove(stale);
                stale.Close();
            }

            // The console is the dock host the probe asks for, so it has to exist first - and it is
            // closed again in teardown if this test is what opened it, rather than leaving the user's
            // layout changed.
            var consoleType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ConsoleWindow");
            var alreadyOpen = Resources.FindObjectsOfTypeAll(consoleType).Length > 0;
            var console = EditorWindow.GetWindow(consoleType);
            console.Show();
            if (!alreadyOpen)
            {
                _opened.Add(console);
            }

            var probe = MoveProbeWindow.OpenDocked();
            return Prepare(probe);
        }

        private MoveProbeWindow Prepare(MoveProbeWindow probe)
        {
            _opened.Add(probe);
            // A docked probe is reused across tests, so both the counters and the remembered pointer
            // position have to start clean or the delta assertions would depend on test order.
            EditorToolBridge.ForgetPointerPosition(probe);
            probe.ResetCounters();
            EditorToolBridge.RepaintImmediate(probe);
            return probe;
        }

        private static CommandResponse Move(EditorWindow window, float x, float y, string coordinateSpace)
        {
            return Execute("editor_move", new CommandParameters
            {
                instanceId = window.GetInstanceID(),
                x = x,
                y = y,
                coordinateSpace = coordinateSpace,
            });
        }

        private static CommandResponse Execute(string command, CommandParameters parameters)
        {
            var response = new CommandResponse { command = command };
            Assert.That(EditorToolBridge.TryExecute(command, parameters, response), Is.True,
                command + " is not a supported bridge command.");
            return response;
        }

        /// <summary>A point guaranteed to be inside the hover area, in host-view coordinates.</summary>
        private static Vector2 HostPointInHoverArea(MoveProbeWindow probe)
        {
            var bounds = probe.HoverAreaWorldBound;
            Assert.That(bounds.width, Is.GreaterThan(4f), "The probe's hover area has no layout yet.");
            // Rounded, so the assertions compare exact numbers rather than fighting float formatting.
            return new Vector2(Mathf.Round(bounds.center.x), Mathf.Round(bounds.center.y));
        }

        /// <summary>A straight path across the graph, passing over the node.</summary>
        private static List<Vector2> GraphPathPoints(MoveProbeWindow probe, int count)
        {
            var hover = probe.HoverAreaWorldBound;
            var y = Mathf.Round(hover.yMax + 60f);
            var points = new List<Vector2>();
            for (var i = 0; i < count; i++)
            {
                points.Add(new Vector2(Mathf.Round(hover.xMin + 30f + i * 24f), y));
            }

            return points;
        }

        private string TempPngPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "nx3-move-probe-" + Guid.NewGuid().ToString("N") + ".png");
            _tempFiles.Add(path);
            return path;
        }

        private static string Output(CommandResponse response, string key)
        {
            var entry = response.outputs.FirstOrDefault(x => x.key == key);
            Assert.That(entry, Is.Not.Null, "Response has no output '" + key + "'. " + Describe(response));
            return entry.value;
        }

        private static float Number(CommandResponse response, string key)
        {
            return float.Parse(Output(response, key), CultureInfo.InvariantCulture);
        }

        private static string Describe(CommandResponse response)
        {
            return "Outputs: " + string.Join(", ", response.outputs.Select(x => x.key + "=" + x.value).ToArray());
        }
    }
}
