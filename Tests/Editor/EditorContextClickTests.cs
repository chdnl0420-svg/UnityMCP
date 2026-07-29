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
    /// Coverage for editor_context_click, the right-click that actually opens a menu.
    ///
    /// The claim under test is narrow and easy to fake: that a third event, EventType.ContextClick,
    /// reaches the window after the press pair. So the assertions come from what MoveProbeWindow
    /// recorded - a ContextClickEvent on the UI Toolkit side, an EventType.ContextClick on the IMGUI
    /// side, and the editor going on to build a contextual menu - rather than from the command's own
    /// account of what it sent. The mirror test, that editor_click with button 1 still produces none of
    /// that, is what pins down why this command had to exist at all.
    ///
    /// Nothing here aims at the probe's GraphView. Its BuildContextualMenu override appends a real item,
    /// so a right-click there displays a real popup menu; that path is for manual QA, where a menu left
    /// on screen is the evidence rather than a hazard to the tests that follow.
    ///
    /// Each test yields a frame after sending, because UI Toolkit is free to dispatch on the next one.
    /// </summary>
    public sealed class EditorContextClickTests
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

        // ------------------------------------------------------------------ the event that opens a menu

        [UnityTest]
        public IEnumerator Context_click_delivers_a_ContextClick_after_the_press_pair()
        {
            var probe = OpenProbe();
            yield return null;

            var response = Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "hover-area",
            });
            yield return null;

            Assert.That(Output(response, "eventOrder"), Is.EqualTo("MouseDown,MouseUp,ContextClick"));
            Assert.That(Output(response, "eventsSent"), Is.EqualTo("3"));
            Assert.That(Output(response, "button"), Is.EqualTo("1"));

            // Presence, not value: Output fails when a key is missing, and the raw SendEvent returns are
            // explicitly not a "the menu opened" signal, so asserting what they contain would assert a
            // guarantee the command does not make.
            Output(response, "mouseDownReturned");
            Output(response, "mouseUpReturned");
            Output(response, "contextClickReturned");

            Assert.That(probe.ContextClickCount, Is.GreaterThanOrEqualTo(1),
                "No ContextClickEvent reached the window. Event log: " + probe.EventTypeLog);
            Assert.That(probe.DownCount, Is.EqualTo(1), "The press pair goes first, and exactly once.");
            Assert.That(probe.UpCount, Is.EqualTo(1));
            Assert.That(probe.LastButton, Is.EqualTo(1), "A context click is a right-button gesture.");

            // Deliberately no assertion that a menu was built here. hover-area is a bare VisualElement
            // with no contextual menu of its own, so nothing builds one - measured, and it is the same
            // thing a real right-click on blank UI does. Menu building is asserted on menu-area below.
        }

        [UnityTest]
        public IEnumerator Context_click_makes_the_editor_build_a_contextual_menu()
        {
            var probe = OpenProbe();
            yield return null;

            Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "menu-area",
            });
            yield return null;

            // The payoff, and the step editor_click could never reach: the editor's menu manager ran and
            // called the builder. menu-area's builder appends nothing on purpose, so Unity skips the
            // display - which is what keeps this assertable from a test at all. A menu that does display
            // holds the main thread until dismissed; that path is verified by hand against the GraphView.
            Assert.That(probe.MenuAreaMenuCount, Is.GreaterThanOrEqualTo(1),
                "The ContextClick did not reach the contextual menu builder. Event log: " + probe.EventTypeLog);
            Assert.That(probe.ContextMenuCount, Is.GreaterThanOrEqualTo(1),
                "No ContextualMenuPopulateEvent bubbled to the root.");
            Assert.That(probe.ContextClickCount, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator A_plain_right_click_already_reaches_a_UI_Toolkit_menu()
        {
            var probe = OpenProbe();
            yield return null;

            Execute("editor_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "menu-area",
                button = 1,
            });
            yield return null;

            // Measured, and worth pinning down because it contradicts the obvious story: on Windows a
            // ContextualMenuManipulator listens on MouseUpEvent, so the pair editor_click already sent
            // is enough for the UI Toolkit side. The gap editor_context_click closes is IMGUI, asserted
            // below - not this path.
            Assert.That(probe.MenuAreaPressCount, Is.GreaterThanOrEqualTo(1),
                "The click itself still has to arrive, or this proves nothing.");
            Assert.That(probe.MenuAreaMenuCount, Is.GreaterThanOrEqualTo(1),
                "A right-button press pair is expected to reach a ContextualMenuManipulator on this platform.");
            Assert.That(probe.ImguiContextClickCount, Is.EqualTo(0),
                "...but it must still deliver no EventType.ContextClick, which is the actual gap.");
        }

        [UnityTest]
        public IEnumerator Only_context_click_reaches_IMGUI_menu_code()
        {
            var probe = OpenProbe();
            yield return null;

            Execute("editor_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "imgui-strip",
                button = 1,
            });
            yield return null;

            Assert.That(probe.ImguiMouseDownCount, Is.GreaterThanOrEqualTo(1), "The click has to land on the strip.");
            Assert.That(probe.ImguiContextClickCount, Is.EqualTo(0),
                "editor_click delivers the right button but never EventType.ContextClick.");

            Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "imgui-strip",
            });
            yield return null;

            // The whole justification for the command, as a before/after on one target: an IMGUI tool
            // builds its GenericMenu on EventType.ContextClick, and only this command produces one.
            Assert.That(probe.ImguiContextClickCount, Is.GreaterThanOrEqualTo(1),
                "editor_context_click must deliver the ContextClick that editor_click cannot.");
        }

        [UnityTest]
        public IEnumerator A_right_button_editor_click_still_sends_no_ContextClick()
        {
            var probe = OpenProbe();
            yield return null;

            Execute("editor_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "hover-area",
                button = 1,
            });
            yield return null;

            // The original defect, kept as a test: the button number was always delivered, and it was
            // never what opened a menu. editor_click is deliberately left this way.
            Assert.That(probe.DownCount, Is.EqualTo(1), "editor_click still sends its pair.");
            Assert.That(probe.UpCount, Is.EqualTo(1));
            Assert.That(probe.LastButton, Is.EqualTo(1), "The right button did arrive.");
            Assert.That(probe.ContextClickCount, Is.EqualTo(0),
                "editor_click must not start synthesising a ContextClick; that is what editor_context_click is for.");
            Assert.That(probe.ImguiContextClickCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Context_click_reaches_IMGUI_as_EventType_ContextClick()
        {
            var probe = OpenProbe();
            yield return null;

            Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "imgui-strip",
            });
            yield return null;

            // The immediate-mode half of the contract: an IMGUI tool builds its GenericMenu by testing
            // Event.current.type against ContextClick, so the event has to arrive carrying that type.
            Assert.That(probe.ImguiContextClickCount, Is.GreaterThanOrEqualTo(1),
                "IMGUI never saw EventType.ContextClick.");
            Assert.That(probe.ImguiMouseDownCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(probe.ImguiMouseUpCount, Is.GreaterThanOrEqualTo(1));
        }

        // ------------------------------------------------------------------ shared targeting

        [UnityTest]
        public IEnumerator Context_click_and_click_aim_at_the_same_pixel()
        {
            var probe = OpenProbe();
            yield return null;

            var clicked = Execute("editor_click", ElementTarget(probe));
            var contextClicked = Execute("editor_context_click", ElementTarget(probe));
            yield return null;

            // Both commands resolve through one function, so an element - or an x/y, or an entry index -
            // means the same pixel in each. Drift here is what makes a right-click miss what a left click
            // just hit, with nothing in either response to show why.
            Assert.That(Number(contextClicked, "x"), Is.EqualTo(Number(clicked, "x")).Within(0.01f));
            Assert.That(Number(contextClicked, "y"), Is.EqualTo(Number(clicked, "y")).Within(0.01f));
            Assert.That(Output(contextClicked, "resolvedFrom"), Is.EqualTo(Output(clicked, "resolvedFrom")));
            Assert.That(Output(contextClicked, "elementName"), Is.EqualTo("hover-area"));
        }

        [UnityTest]
        public IEnumerator Context_click_reads_x_and_y_as_host_coordinates_by_default()
        {
            var probe = OpenProbe();
            yield return null;

            var response = Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                x = 40f,
                y = 50f,
            });
            yield return null;

            // editor_click has always read x/y as host-view coordinates, and editor_element_query hands
            // back the same space. The drag, move and scroll commands default to content space instead,
            // so this default is the one thing about the command that is not shared with all of them.
            Assert.That(Output(response, "coordinateSpace"), Is.EqualTo("host"));
            Assert.That(Number(response, "x"), Is.EqualTo(40f).Within(0.01f));
            Assert.That(Number(response, "y"), Is.EqualTo(50f).Within(0.01f));
            Assert.That(probe.ContextClickCount, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator Context_click_adds_the_border_when_asked_for_content_space()
        {
            var probe = OpenProbe();
            yield return null;

            var response = Execute("editor_context_click", new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                x = 40f,
                y = 50f,
                coordinateSpace = "content",
            });
            yield return null;

            // The probe reads its own root offset from the layout, so the shift the command applied is
            // checked against an independent measurement of the same border rather than against itself.
            var offset = probe.RootOffset;
            Assert.That(Output(response, "coordinateSpace"), Is.EqualTo("content"));
            Assert.That(Number(response, "x"), Is.EqualTo(40f + offset.x).Within(1f));
            Assert.That(Number(response, "y"), Is.EqualTo(50f + offset.y).Within(1f));
        }

        [UnityTest]
        public IEnumerator Context_click_rejects_a_coordinate_space_it_does_not_know()
        {
            var probe = OpenProbe();
            yield return null;

            var error = Assert.Throws<System.ArgumentException>(() => Execute("editor_context_click",
                new CommandParameters
                {
                    instanceId = probe.GetInstanceID(),
                    x = 10f,
                    y = 10f,
                    coordinateSpace = "screen",
                }));

            Assert.That(error.Message, Does.Contain("coordinateSpace"));
            Assert.That(probe.ContextClickCount, Is.EqualTo(0),
                "A rejected coordinate space must send nothing at all.");
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

        private static CommandParameters ElementTarget(MoveProbeWindow probe)
        {
            return new CommandParameters
            {
                instanceId = probe.GetInstanceID(),
                targetMode = "element",
                elementName = "hover-area",
            };
        }

        private static CommandResponse Execute(string command, CommandParameters parameters)
        {
            var response = new CommandResponse { command = command };
            Assert.That(EditorToolBridge.TryExecute(command, parameters, response), Is.True,
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
    }
}
