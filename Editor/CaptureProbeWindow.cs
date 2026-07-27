using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// A deterministic target for exercising editor_window_capture, editor_drag and editor_drag_capture.
    ///
    /// Verifying those commands needs a window that is guaranteed to exist in any project, that renders
    /// through every path they claim to support, and whose visible state changes while a drag is in
    /// flight - otherwise "the frames differ" cannot be told apart from "the frames were captured at the
    /// wrong moment". This window covers all three: a UI Toolkit GraphView with draggable nodes and an
    /// edge between them, and an IMGUI strip that both draws and counts the raw events it receives.
    ///
    /// Every observable is also a plain instance field, so editor_get_field can confirm the same facts
    /// the PNGs are supposed to show, independently of any pixel comparison.
    /// </summary>
    public sealed class CaptureProbeWindow : EditorWindow
    {
        private const float ImguiStripHeight = 84f;

        [SerializeField] private Vector2 _nodeAPosition;
        [SerializeField] private Vector2 _nodeBPosition;
        [SerializeField] private int _imguiMouseDownCount;
        [SerializeField] private int _imguiMouseDragCount;
        [SerializeField] private int _imguiMouseUpCount;
        [SerializeField] private Vector2 _imguiLastPoint;
        [SerializeField] private Vector2 _imguiLastDelta;
        [SerializeField] private int _buttonClicks;
        [SerializeField] private int _repaintCount;

        private ProbeGraphView _graph;
        private Node _nodeA;
        private Node _nodeB;

        [MenuItem("Window/NX3 MCP/Capture Probe")]
        public static CaptureProbeWindow Open()
        {
            var window = GetWindow<CaptureProbeWindow>();
            window.titleContent = new GUIContent("MCP Capture Probe");
            window.minSize = new Vector2(420f, 320f);
            window.Show();
            return window;
        }

        /// <summary>Opens the same probe in a floating container, for testing the undocked capture path.</summary>
        [MenuItem("Window/NX3 MCP/Capture Probe (Floating)")]
        public static CaptureProbeWindow OpenFloating()
        {
            var window = CreateInstance<CaptureProbeWindow>();
            window.titleContent = new GUIContent("MCP Capture Probe");
            window.minSize = new Vector2(420f, 320f);
            window.position = new Rect(180f, 180f, 720f, 460f);
            window.ShowUtility();
            return window;
        }

        /// <summary>
        /// Opens the probe as a tab next to the Console, which is the only way to get a docked GraphView
        /// on demand - and a docked one is what exercises the tab-strip offset that drag coordinates and
        /// capture cropping both depend on.
        /// </summary>
        [MenuItem("Window/NX3 MCP/Capture Probe (Docked)")]
        public static CaptureProbeWindow OpenDocked()
        {
            var window = GetWindow<CaptureProbeWindow>("MCP Capture Probe", typeof(UnityEditor.EditorWindow).Assembly
                .GetType("UnityEditor.ConsoleWindow"));
            window.titleContent = new GUIContent("MCP Capture Probe");
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;

            _graph = new ProbeGraphView();
            _graph.style.flexGrow = 1f;
            root.Add(_graph);

            _nodeA = CreateNode("Node A", new Vector2(60f, 60f), UnityEditor.Experimental.GraphView.Direction.Output);
            _nodeB = CreateNode("Node B", new Vector2(300f, 200f), UnityEditor.Experimental.GraphView.Direction.Input);
            _graph.AddElement(_nodeA);
            _graph.AddElement(_nodeB);
            ConnectFirstPorts(_nodeA, _nodeB);

            var strip = new IMGUIContainer(DrawImguiStrip);
            strip.style.height = ImguiStripHeight;
            strip.style.flexShrink = 0f;
            root.Add(strip);

            SyncNodePositions();
        }

        // ------------------------------------------------------------------ IMGUI half

        /// <summary>
        /// Draws the numbers the capture is supposed to show and, on the same pass, records the raw
        /// mouse events IMGUI hands it - so one strip proves both that IMGUI input arrives and that the
        /// window re-rendered between two captured frames.
        /// </summary>
        private void DrawImguiStrip()
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    _imguiMouseDownCount++;
                    _imguiLastPoint = e.mousePosition;
                    break;
                case EventType.MouseDrag:
                    _imguiMouseDragCount++;
                    _imguiLastPoint = e.mousePosition;
                    _imguiLastDelta = e.delta;
                    break;
                case EventType.MouseUp:
                    _imguiMouseUpCount++;
                    _imguiLastPoint = e.mousePosition;
                    break;
                case EventType.Repaint:
                    _repaintCount++;
                    break;
            }

            SyncNodePositions();

            using (new GUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(
                    string.Format(CultureInfo.InvariantCulture,
                        "A {0:F0},{1:F0}   B {2:F0},{3:F0}\ndown {4}  drag {5}  up {6}  repaint {7}",
                        _nodeAPosition.x, _nodeAPosition.y, _nodeBPosition.x, _nodeBPosition.y,
                        _imguiMouseDownCount, _imguiMouseDragCount, _imguiMouseUpCount, _repaintCount),
                    GUILayout.Width(280f), GUILayout.ExpandHeight(true));

                if (GUILayout.Button("Probe Button\n(" + _buttonClicks + ")", GUILayout.Width(120f), GUILayout.ExpandHeight(true)))
                {
                    _buttonClicks++;
                }

                // A bar whose length follows Node A, so two frames of a drag differ in pixels even when
                // the node itself lands outside the captured region.
                var bar = GUILayoutUtility.GetRect(10f, ImguiStripHeight - 12f, GUILayout.ExpandWidth(true));
                if (e.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(bar, new Color(0.15f, 0.15f, 0.18f));
                    var filled = new Rect(bar.x, bar.y, Mathf.Clamp(_nodeAPosition.x, 0f, bar.width), bar.height);
                    EditorGUI.DrawRect(filled, new Color(0.20f, 0.65f, 0.95f));
                }
            }
        }

        private void SyncNodePositions()
        {
            if (_nodeA != null) _nodeAPosition = _nodeA.GetPosition().position;
            if (_nodeB != null) _nodeBPosition = _nodeB.GetPosition().position;
        }

        // ------------------------------------------------------------------ GraphView half

        private static Node CreateNode(string title, Vector2 position,
            UnityEditor.Experimental.GraphView.Direction direction)
        {
            var node = new Node { title = title };
            node.SetPosition(new Rect(position.x, position.y, 180f, 120f));

            var port = node.InstantiatePort(
                UnityEditor.Experimental.GraphView.Orientation.Horizontal,
                direction,
                Port.Capacity.Multi,
                typeof(float));
            port.portName = direction == UnityEditor.Experimental.GraphView.Direction.Output ? "out" : "in";
            if (direction == UnityEditor.Experimental.GraphView.Direction.Output)
            {
                node.outputContainer.Add(port);
            }
            else
            {
                node.inputContainer.Add(port);
            }

            node.RefreshExpandedState();
            node.RefreshPorts();
            return node;
        }

        private void ConnectFirstPorts(Node from, Node to)
        {
            var output = from.outputContainer.Query<Port>().ToList().FirstOrDefault();
            var input = to.inputContainer.Query<Port>().ToList().FirstOrDefault();
            if (output == null || input == null)
            {
                return;
            }

            var edge = output.ConnectTo(input);
            _graph.AddElement(edge);
        }

        private sealed class ProbeGraphView : GraphView
        {
            public ProbeGraphView()
            {
                Insert(0, new GridBackground());
                this.AddManipulator(new ContentZoomer());
                this.AddManipulator(new ContentDragger());
                this.AddManipulator(new SelectionDragger());
                this.AddManipulator(new RectangleSelector());
            }

            public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
            {
                return ports.ToList()
                    .Where(port => port.direction != startPort.direction && port.node != startPort.node)
                    .ToList();
            }
        }
    }
}
