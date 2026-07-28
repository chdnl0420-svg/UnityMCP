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
    /// A deterministic target for exercising editor_move, the button-less pointer move.
    ///
    /// Proving a hover arrived needs more than "the command returned true": the receiving side has to
    /// show a MouseMoveEvent with pressedButtons at 0, at the requested coordinates, and no MouseDown,
    /// MouseUp or MouseDrag alongside it. So this window records all of that from three angles at once -
    /// a UI Toolkit element with real callbacks, an IMGUI strip reading raw Event.current types, and a
    /// GraphView whose node position, selection and pan expose any gesture a move must never start.
    ///
    /// Every observable is a plain instance field kept fresh on each repaint, so editor_get_field reads
    /// the same numbers the package tests assert on, and the hover tint gives editor_window_capture
    /// something visibly different to photograph.
    /// </summary>
    public sealed class MoveProbeWindow : EditorWindow
    {
        private const float ImguiStripHeight = 96f;
        private const float GraphHeight = 150f;

        // --- UI Toolkit mouse events, the primary evidence -------------------------------------
        [SerializeField] private int _moveCount;
        [SerializeField] private int _downCount;
        [SerializeField] private int _upCount;
        [SerializeField] private int _moveWithPressedButtonsCount;
        [SerializeField] private Vector2 _lastMousePosition;
        [SerializeField] private Vector2 _lastLocalMousePosition;
        [SerializeField] private Vector2 _lastMouseDelta;
        [SerializeField] private int _lastPressedButtons = -1;
        [SerializeField] private int _lastButton = -1;
        [SerializeField] private string _lastEventType = string.Empty;
        [SerializeField] private string _eventTypeLog = string.Empty;
        [SerializeField] private int _enterCount;
        [SerializeField] private int _leaveCount;
        [SerializeField] private bool _isHovered;
        [SerializeField] private int _wheelCount;
        [SerializeField] private Vector2 _lastWheelDelta;

        // --- raw IMGUI event types, so the same move is checkable on the immediate-mode side ----
        [SerializeField] private int _imguiMouseMoveCount;
        [SerializeField] private int _imguiMouseDownCount;
        [SerializeField] private int _imguiMouseDragCount;
        [SerializeField] private int _imguiMouseUpCount;
        [SerializeField] private int _imguiContextClickCount;
        [SerializeField] private int _imguiScrollWheelCount;
        [SerializeField] private Vector2 _imguiLastMovePoint;
        [SerializeField] private Vector2 _imguiLastMoveDelta;
        [SerializeField] private int _repaintCount;

        // --- gestures a button-less move must never start ---------------------------------------
        [SerializeField] private Vector2 _nodePosition;
        [SerializeField] private int _selectionCount;
        [SerializeField] private Vector3 _panPosition;
        [SerializeField] private int _contextClickCount;
        [SerializeField] private int _contextMenuCount;

        // --- geometry, so a caller can aim without guessing -------------------------------------
        [SerializeField] private Vector2 _hoverCenterHost;
        [SerializeField] private Vector2 _hoverCenterContent;
        [SerializeField] private Vector2 _rootOffset;
        [SerializeField] private Rect _hoverAreaWorldBound;

        private VisualElement _hoverArea;
        private Label _readout;
        private ProbeGraphView _graph;
        private Node _node;

        [MenuItem("Window/NX3 MCP/Move Probe")]
        public static MoveProbeWindow Open()
        {
            var window = GetWindow<MoveProbeWindow>();
            window.titleContent = new GUIContent("MCP Move Probe");
            window.minSize = new Vector2(420f, 360f);
            window.Show();
            return window;
        }

        /// <summary>Opens the probe floating, for checking hover in an undocked window.</summary>
        [MenuItem("Window/NX3 MCP/Move Probe (Floating)")]
        public static MoveProbeWindow OpenFloating()
        {
            var window = CreateInstance<MoveProbeWindow>();
            window.titleContent = new GUIContent("MCP Move Probe");
            window.minSize = new Vector2(420f, 360f);
            window.position = new Rect(220f, 220f, 640f, 460f);
            window.ShowUtility();
            return window;
        }

        /// <summary>
        /// Opens the probe docked next to the Console, which is what exercises the tab-strip offset
        /// between content coordinates and the host-view space the event is actually delivered into.
        /// </summary>
        [MenuItem("Window/NX3 MCP/Move Probe (Docked)")]
        public static MoveProbeWindow OpenDocked()
        {
            var window = GetWindow<MoveProbeWindow>("MCP Move Probe", typeof(EditorWindow).Assembly
                .GetType("UnityEditor.ConsoleWindow"));
            window.titleContent = new GUIContent("MCP Move Probe");
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;

            _hoverArea = new VisualElement { name = "hover-area" };
            _hoverArea.style.flexGrow = 1f;
            _hoverArea.style.minHeight = 90f;
            _hoverArea.style.backgroundColor = IdleColor;
            _hoverArea.style.justifyContent = Justify.Center;
            _hoverArea.style.alignItems = Align.Center;

            _readout = new Label(string.Empty);
            _readout.style.unityTextAlign = TextAnchor.MiddleCenter;
            _readout.style.whiteSpace = WhiteSpace.Normal;
            _hoverArea.Add(_readout);

            // Mouse callbacks rather than pointer ones on purpose: MouseMoveEvent with pressedButtons is
            // precisely the signal a hover-driven tool listens for, so recording it is recording what
            // that tool would see.
            _hoverArea.RegisterCallback<MouseMoveEvent>(OnMouseMove);
            _hoverArea.RegisterCallback<MouseDownEvent>(OnMouseDown);
            _hoverArea.RegisterCallback<MouseUpEvent>(OnMouseUp);
            _hoverArea.RegisterCallback<MouseEnterEvent>(OnMouseEnter);
            _hoverArea.RegisterCallback<MouseLeaveEvent>(OnMouseLeave);
            _hoverArea.RegisterCallback<WheelEvent>(OnWheel);
            root.Add(_hoverArea);

            _graph = new ProbeGraphView();
            _graph.style.height = GraphHeight;
            _graph.style.flexShrink = 0f;
            _node = CreateNode("Node", new Vector2(40f, 24f));
            _graph.AddElement(_node);
            root.Add(_graph);

            // A context menu is the one gesture that leaves no trace in the counters above, so it is
            // watched separately - on the root, which every child event bubbles through.
            root.RegisterCallback<ContextClickEvent>(_ => _contextClickCount++);
            root.RegisterCallback<ContextualMenuPopulateEvent>(_ => _contextMenuCount++);

            var strip = new IMGUIContainer(DrawImguiStrip);
            strip.style.height = ImguiStripHeight;
            strip.style.flexShrink = 0f;
            root.Add(strip);

            UpdateReadout();
        }

        private static Color IdleColor { get { return new Color(0.16f, 0.17f, 0.20f); } }
        private static Color HoverColor { get { return new Color(0.16f, 0.45f, 0.70f); } }

        // ------------------------------------------------------------------ UI Toolkit recording

        private void OnMouseMove(MouseMoveEvent evt)
        {
            _moveCount++;
            _lastMousePosition = evt.mousePosition;
            _lastLocalMousePosition = evt.localMousePosition;
            _lastMouseDelta = evt.mouseDelta;
            _lastPressedButtons = evt.pressedButtons;
            _lastButton = evt.button;
            Record(evt);

            // UI Toolkit has no MouseDragEvent: a drag arrives here as a move with a button held, which
            // is exactly the case editor_move must never produce.
            if (evt.pressedButtons != 0)
            {
                _moveWithPressedButtonsCount++;
            }

            UpdateReadout();
        }

        private void OnMouseDown(MouseDownEvent evt)
        {
            _downCount++;
            _lastPressedButtons = evt.pressedButtons;
            _lastButton = evt.button;
            Record(evt);
            UpdateReadout();
        }

        private void OnMouseUp(MouseUpEvent evt)
        {
            _upCount++;
            _lastPressedButtons = evt.pressedButtons;
            _lastButton = evt.button;
            Record(evt);
            UpdateReadout();
        }

        private void OnMouseEnter(MouseEnterEvent evt)
        {
            _enterCount++;
            _isHovered = true;
            _hoverArea.style.backgroundColor = HoverColor;
            Record(evt);
            UpdateReadout();
        }

        private void OnMouseLeave(MouseLeaveEvent evt)
        {
            _leaveCount++;
            _isHovered = false;
            _hoverArea.style.backgroundColor = IdleColor;
            Record(evt);
            UpdateReadout();
        }

        private void OnWheel(WheelEvent evt)
        {
            _wheelCount++;
            _lastWheelDelta = evt.delta;
            Record(evt);
            UpdateReadout();
        }

        private void Record(EventBase evt)
        {
            _lastEventType = evt.GetType().Name;

            // A short ordered log, so "no MouseDown arrived" can be read as a sequence rather than
            // inferred from counters that were reset at the wrong moment.
            var entries = string.IsNullOrEmpty(_eventTypeLog)
                ? new List<string>()
                : _eventTypeLog.Split(',').ToList();
            entries.Add(_lastEventType);
            if (entries.Count > 16)
            {
                entries.RemoveRange(0, entries.Count - 16);
            }

            _eventTypeLog = string.Join(",", entries.ToArray());
        }

        private void UpdateReadout()
        {
            if (_readout == null)
            {
                return;
            }

            _readout.text = string.Format(CultureInfo.InvariantCulture,
                "move {0}   down {1}   up {2}   moveWithButtons {3}\npos {4:F0},{5:F0}   delta {6:F0},{7:F0}   pressedButtons {8}\n{9}",
                _moveCount, _downCount, _upCount, _moveWithPressedButtonsCount,
                _lastMousePosition.x, _lastMousePosition.y, _lastMouseDelta.x, _lastMouseDelta.y,
                _lastPressedButtons, _isHovered ? "HOVERED" : "idle");
        }

        // ------------------------------------------------------------------ IMGUI recording

        /// <summary>
        /// Counts the raw event types IMGUI receives, which is the other half of the contract:
        /// a button-less move has to reach immediate-mode code as EventType.MouseMove, not as MouseDrag.
        /// </summary>
        private void DrawImguiStrip()
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseMove:
                    _imguiMouseMoveCount++;
                    _imguiLastMovePoint = e.mousePosition;
                    _imguiLastMoveDelta = e.delta;
                    break;
                case EventType.MouseDown: _imguiMouseDownCount++; break;
                case EventType.MouseDrag: _imguiMouseDragCount++; break;
                case EventType.MouseUp: _imguiMouseUpCount++; break;
                case EventType.ContextClick: _imguiContextClickCount++; break;
                case EventType.ScrollWheel: _imguiScrollWheelCount++; break;
                case EventType.Repaint: _repaintCount++; break;
            }

            SyncProbeState();

            using (new GUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "IMGUI  move {0}  down {1}  drag {2}  up {3}  contextClick {4}  scroll {5}  repaint {6}",
                    _imguiMouseMoveCount, _imguiMouseDownCount, _imguiMouseDragCount,
                    _imguiMouseUpCount, _imguiContextClickCount, _imguiScrollWheelCount, _repaintCount));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "IMGUI  lastMove {0:F0},{1:F0}  delta {2:F0},{3:F0}",
                    _imguiLastMovePoint.x, _imguiLastMovePoint.y,
                    _imguiLastMoveDelta.x, _imguiLastMoveDelta.y));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "Graph  node {0:F0},{1:F0}  selection {2}  pan {3:F0},{4:F0}  contextMenu {5}",
                    _nodePosition.x, _nodePosition.y, _selectionCount,
                    _panPosition.x, _panPosition.y, _contextMenuCount));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "Hover area host centre {0:F0},{1:F0}   content centre {2:F0},{3:F0}",
                    _hoverCenterHost.x, _hoverCenterHost.y,
                    _hoverCenterContent.x, _hoverCenterContent.y));
            }
        }

        /// <summary>
        /// Refreshes the fields that mirror live UI state, so editor_get_field never reads a stale
        /// number. Called from the IMGUI pass, which runs on every repaint the commands force.
        /// </summary>
        private void SyncProbeState()
        {
            if (_node != null) _nodePosition = _node.GetPosition().position;
            if (_graph != null)
            {
                _selectionCount = _graph.selection != null ? _graph.selection.Count : 0;
                _panPosition = _graph.contentViewContainer != null
                    ? _graph.contentViewContainer.transform.position
                    : Vector3.zero;
            }

            if (_hoverArea != null)
            {
                _hoverAreaWorldBound = _hoverArea.worldBound;
                _hoverCenterHost = _hoverAreaWorldBound.center;
            }

            if (rootVisualElement != null)
            {
                // The root sits below the host view's border, so this offset is the same tab-strip shift
                // the bridge adds to content coordinates - a second, independent reading of it.
                _rootOffset = rootVisualElement.worldBound.position;
            }

            _hoverCenterContent = _hoverCenterHost - _rootOffset;
        }

        // ------------------------------------------------------------------ helpers for tests

        /// <summary>The hover area in host-view coordinates - what editor_move sends with space "host".</summary>
        public Rect HoverAreaWorldBound
        {
            get
            {
                SyncProbeState();
                return _hoverAreaWorldBound;
            }
        }

        /// <summary>Zeroes every counter, so one test's events cannot be mistaken for another's.</summary>
        public void ResetCounters()
        {
            _moveCount = 0;
            _downCount = 0;
            _upCount = 0;
            _moveWithPressedButtonsCount = 0;
            _lastMousePosition = Vector2.zero;
            _lastLocalMousePosition = Vector2.zero;
            _lastMouseDelta = Vector2.zero;
            _lastPressedButtons = -1;
            _lastButton = -1;
            _lastEventType = string.Empty;
            _eventTypeLog = string.Empty;
            _enterCount = 0;
            _leaveCount = 0;
            _wheelCount = 0;
            _lastWheelDelta = Vector2.zero;
            _imguiScrollWheelCount = 0;
            _imguiMouseMoveCount = 0;
            _imguiMouseDownCount = 0;
            _imguiMouseDragCount = 0;
            _imguiMouseUpCount = 0;
            _imguiContextClickCount = 0;
            _imguiLastMovePoint = Vector2.zero;
            _imguiLastMoveDelta = Vector2.zero;
            _repaintCount = 0;
            _contextClickCount = 0;
            _contextMenuCount = 0;
            UpdateReadout();
            Repaint();
        }

        public int MoveCount { get { return _moveCount; } }
        public int DownCount { get { return _downCount; } }
        public int UpCount { get { return _upCount; } }
        public int MoveWithPressedButtonsCount { get { return _moveWithPressedButtonsCount; } }
        public int LastPressedButtons { get { return _lastPressedButtons; } }
        public string LastEventType { get { return _lastEventType; } }
        public string EventTypeLog { get { return _eventTypeLog; } }
        public Vector2 LastMousePosition { get { return _lastMousePosition; } }
        public Vector2 LastLocalMousePosition { get { return _lastLocalMousePosition; } }
        public Vector2 LastMouseDelta { get { return _lastMouseDelta; } }
        public int LastButton { get { return _lastButton; } }
        public int EnterCount { get { return _enterCount; } }
        public int LeaveCount { get { return _leaveCount; } }
        public bool IsHovered { get { return _isHovered; } }
        public int ImguiMouseMoveCount { get { return _imguiMouseMoveCount; } }
        public int ImguiMouseDownCount { get { return _imguiMouseDownCount; } }
        public int ImguiMouseDragCount { get { return _imguiMouseDragCount; } }
        public int ImguiMouseUpCount { get { return _imguiMouseUpCount; } }
        public int WheelCount { get { return _wheelCount; } }
        public Vector2 LastWheelDelta { get { return _lastWheelDelta; } }
        public int ImguiScrollWheelCount { get { return _imguiScrollWheelCount; } }
        public int ImguiContextClickCount { get { return _imguiContextClickCount; } }
        public Vector2 ImguiLastMovePoint { get { return _imguiLastMovePoint; } }
        public Vector2 ImguiLastMoveDelta { get { return _imguiLastMoveDelta; } }
        public int ContextClickCount { get { return _contextClickCount; } }
        public int ContextMenuCount { get { return _contextMenuCount; } }
        /// <summary>
        /// Where the window's content starts inside the host view - the dock tab strip on a docked
        /// window, zero on a floating one. Read from the layout rather than from the bridge, so a test
        /// can check the offset the bridge applied against an independent measurement of the same thing.
        /// </summary>
        public Vector2 RootOffset
        {
            get
            {
                SyncProbeState();
                return _rootOffset;
            }
        }

        public int SelectionCount { get { SyncProbeState(); return _selectionCount; } }
        public Vector2 NodePosition { get { SyncProbeState(); return _nodePosition; } }
        public Vector3 PanPosition { get { SyncProbeState(); return _panPosition; } }

        // ------------------------------------------------------------------ GraphView half

        private static Node CreateNode(string title, Vector2 position)
        {
            var node = new Node { title = title };
            node.SetPosition(new Rect(position.x, position.y, 140f, 90f));

            var port = node.InstantiatePort(
                UnityEditor.Experimental.GraphView.Orientation.Horizontal,
                UnityEditor.Experimental.GraphView.Direction.Output,
                Port.Capacity.Multi,
                typeof(float));
            port.portName = "out";
            node.outputContainer.Add(port);
            node.RefreshExpandedState();
            node.RefreshPorts();
            return node;
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
