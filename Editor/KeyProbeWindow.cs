using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// A deterministic target for the three gaps that only show up on the UI Toolkit side of a window:
    /// keys that never arrive, fields a caller can only reach by reflection, and drags too short to arm.
    ///
    /// The window deliberately receives each of those the way a real tool does, and from two angles at
    /// once. Keys land on a focusable element through <see cref="KeyDownEvent"/> callbacks *and* on an
    /// <see cref="IMGUIContainer"/> reading raw Event.current - which is the whole point, because a key
    /// that shows up in the IMGUI counter while the UI Toolkit counter stays at zero is the difference
    /// between "the command did nothing" and "the command delivered to the wrong half of the window".
    ///
    /// The rail reproduces a narrow vertical list whose rows are one drag-step apart, so a move to the
    /// neighbouring row is a genuinely short gesture rather than a long one that happens to end nearby.
    /// It arms its own drag on its own threshold, exactly like a tool would, and records the path it was
    /// dragged along - so a gesture that fails can be read as either "never armed" or "armed and missed".
    ///
    /// Every observable is a plain instance field refreshed as events arrive, so editor_get_field reads
    /// the same numbers the package tests assert on.
    /// </summary>
    public sealed class KeyProbeWindow : EditorWindow
    {
        internal const float RowHeight = 26f;
        internal const int RowCount = 6;
        private const float DefaultRailWidth = 126f;
        private const float ArmThreshold = 6f;

        // Narrow the rail to test a source the arming moves can walk off the edge of.
        [SerializeField] private float _railWidth = DefaultRailWidth;

        // --- keys seen by UI Toolkit callbacks, the primary evidence ----------------------------
        [SerializeField] private int _keyDownCount;
        [SerializeField] private int _keyUpCount;
        [SerializeField] private int _rootKeyDownCount;
        [SerializeField] private string _lastKeyCode = string.Empty;
        [SerializeField] private string _lastModifiers = string.Empty;
        [SerializeField] private string _lastCharacter = string.Empty;
        [SerializeField] private string _keyLog = string.Empty;

        // --- the same keys as raw IMGUI events, so "arrived but on the other path" is visible ---
        [SerializeField] private int _imguiKeyDownCount;
        [SerializeField] private int _imguiKeyUpCount;
        [SerializeField] private string _imguiLastKeyCode = string.Empty;
        [SerializeField] private string _imguiLastModifiers = string.Empty;

        // --- focus, because a key is routed to the focused element and nowhere else -------------
        [SerializeField] private string _focusedElement = string.Empty;
        [SerializeField] private int _focusCount;

        // --- an action a key is expected to drive, so delivery can be judged by its effect ------
        [SerializeField] private int _undoCount;
        [SerializeField] private string _order = string.Empty;
        [SerializeField] private string _orderBeforeLastDrop = string.Empty;

        // --- the dropdown, for the reflection-set route ------------------------------------------
        [SerializeField] private int _dropdownChangeCount;
        [SerializeField] private string _dropdownValue = string.Empty;
        [SerializeField] private string _dropdownPreviousValue = string.Empty;

        // --- the rail drag ------------------------------------------------------------------------
        [SerializeField] private int _mouseDownCount;
        [SerializeField] private int _armAttemptCount;
        [SerializeField] private bool _dragArmed;
        [SerializeField] private float _armedAtDistance = -1f;
        [SerializeField] private int _dragSourceIndex = -1;
        [SerializeField] private int _dragUpdatedCount;
        [SerializeField] private int _dragPerformCount;
        [SerializeField] private int _dragExitedCount;
        [SerializeField] private int _dropCount;
        [SerializeField] private int _lastDropIndex = -1;
        [SerializeField] private string _dragPath = string.Empty;

        // --- modal dialog, for the dialog commands ------------------------------------------------
        [SerializeField] private int _dialogCount;
        [SerializeField] private string _dialogChoice = string.Empty;

        // --- geometry, so a caller can aim without guessing --------------------------------------
        [SerializeField] private Rect _railWorldBound;
        [SerializeField] private Rect _row0WorldBound;
        [SerializeField] private Vector2 _rootOffset;

        private readonly List<string> _items = new List<string>();
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private readonly List<string> _keyEvents = new List<string>();
        private readonly List<string> _pathPoints = new List<string>();

        private VisualElement _rail;
        private VisualElement _keyArea;
        private DropdownField _dropdown;
        private Label _readout;

        private Vector2 _pressPoint;
        private bool _pressed;
        private int _pressedIndex = -1;
        private List<string> _orderAtDragStart;
        private KeyProbePopup _popup;

        [MenuItem("Window/NX3 MCP/Key Probe")]
        public static KeyProbeWindow Open()
        {
            var window = GetWindow<KeyProbeWindow>();
            window.titleContent = new GUIContent("MCP Key Probe");
            window.minSize = new Vector2(420f, 380f);
            window.Show();
            return window;
        }

        /// <summary>Opens the probe floating, which is where a modal dialog is easiest to photograph over.</summary>
        [MenuItem("Window/NX3 MCP/Key Probe (Floating)")]
        public static KeyProbeWindow OpenFloating()
        {
            var window = CreateInstance<KeyProbeWindow>();
            window.titleContent = new GUIContent("MCP Key Probe");
            window.minSize = new Vector2(420f, 380f);
            window.position = new Rect(260f, 200f, 640f, 470f);
            window.ShowUtility();
            return window;
        }

        /// <summary>
        /// Opens the probe docked next to the Console, so the tab-strip offset between content
        /// coordinates and the host view is part of what gets exercised.
        /// </summary>
        [MenuItem("Window/NX3 MCP/Key Probe (Docked)")]
        public static KeyProbeWindow OpenDocked()
        {
            var window = GetWindow<KeyProbeWindow>("MCP Key Probe", typeof(EditorWindow).Assembly
                .GetType("UnityEditor.ConsoleWindow"));
            window.titleContent = new GUIContent("MCP Key Probe");
            window.Show();
            return window;
        }

        private void CreateGUI()
        {
            ResetCounters();

            var root = rootVisualElement;
            root.style.paddingLeft = 6f;
            root.style.paddingTop = 6f;

            // Focusable, because keys are delivered to the focused element and a plain container is
            // not one. This stands in for the tool window that owns the shortcut.
            _keyArea = new VisualElement
            {
                name = "keyArea",
                focusable = true,
                tabIndex = 0,
            };
            _keyArea.style.height = 54f;
            _keyArea.style.marginBottom = 6f;
            _keyArea.style.backgroundColor = new StyleColor(new Color(0.20f, 0.24f, 0.30f));
            _keyArea.Add(new Label("key area (focusable)"));

            _keyArea.RegisterCallback<KeyDownEvent>(OnKeyDown);
            _keyArea.RegisterCallback<KeyUpEvent>(OnKeyUp);
            _keyArea.RegisterCallback<FocusEvent>(_ =>
            {
                _focusCount++;
                RefreshReadout();
            });

            // The root gets its own counter: a converted event aimed at the panel root rather than the
            // focused element would light this one and leave the key area's at zero, and those two
            // outcomes need telling apart.
            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                _rootKeyDownCount++;
                RefreshReadout();
                // Not stopped: the key area still has to get its turn.
                _ = evt;
            });

            root.Add(_keyArea);

            var imgui = new IMGUIContainer(OnImguiKeys)
            {
                name = "imguiKeys",
            };
            imgui.style.height = 22f;
            root.Add(imgui);

            _dropdown = new DropdownField(
                "Combination",
                new List<string> { "None", "Ctrl", "Ctrl+Shift", "Alt", "Alt+Shift" },
                0)
            {
                name = "combination",
            };
            _dropdown.RegisterValueChangedCallback(evt =>
            {
                _dropdownChangeCount++;
                _dropdownPreviousValue = evt.previousValue ?? string.Empty;
                _dropdownValue = evt.newValue ?? string.Empty;
                RefreshReadout();
            });
            _dropdown.style.marginBottom = 6f;
            root.Add(_dropdown);

            _rail = new VisualElement { name = "rail" };
            _rail.style.width = _railWidth;
            _rail.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.16f));
            root.Add(_rail);

            BuildRows();

            _readout = new Label { name = "readout" };
            _readout.style.marginTop = 6f;
            _readout.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_readout);

            root.RegisterCallback<GeometryChangedEvent>(_ => CaptureGeometry());

            RefreshReadout();
            EditorApplication.delayCall += () =>
            {
                if (_keyArea != null && _keyArea.panel != null)
                {
                    _keyArea.Focus();
                }
                CaptureGeometry();
            };
        }

        private void BuildRows()
        {
            _rail.Clear();
            _rows.Clear();

            for (var i = 0; i < _items.Count; i++)
            {
                var row = new VisualElement { name = "row" + i.ToString(CultureInfo.InvariantCulture) };
                row.style.height = RowHeight;
                row.style.borderBottomWidth = 1f;
                row.style.borderBottomColor = new StyleColor(new Color(0.08f, 0.08f, 0.08f));
                row.style.backgroundColor = new StyleColor(i % 2 == 0
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.26f, 0.26f, 0.26f));

                var label = new Label(_items[i]);
                label.style.marginLeft = 6f;
                label.style.unityTextAlign = TextAnchor.MiddleLeft;
                label.pickingMode = PickingMode.Ignore;
                row.Add(label);

                var index = i;
                row.RegisterCallback<MouseDownEvent>(evt => OnRowMouseDown(evt, index));
                row.RegisterCallback<MouseMoveEvent>(OnRowMouseMove);
                row.RegisterCallback<MouseUpEvent>(OnRowMouseUp);
                row.RegisterCallback<DragUpdatedEvent>(evt => OnRowDragUpdated(evt, index));
                row.RegisterCallback<DragPerformEvent>(evt => OnRowDragPerform(evt, index));
                row.RegisterCallback<DragExitedEvent>(OnRowDragExited);

                _rail.Add(row);
                _rows.Add(row);
            }

            // Rows are measured after they are laid out, not now: reading worldBound in the same frame
            // they were added hands back NaN, and a caller aiming at NaN gets a gesture that lands
            // nowhere and looks exactly like a threshold problem.
            if (_rows.Count > 0)
            {
                _rows[0].RegisterCallback<GeometryChangedEvent>(_ => CaptureGeometry());
            }
        }

        // ------------------------------------------------------------------ keys

        private void OnKeyDown(KeyDownEvent evt)
        {
            _keyDownCount++;
            _lastKeyCode = evt.keyCode.ToString();
            _lastModifiers = DescribeModifiers(evt);
            _lastCharacter = evt.character == '\0' || char.IsControl(evt.character)
                ? string.Empty
                : evt.character.ToString();

            Record(_keyEvents, "down:" + _lastKeyCode + (_lastModifiers.Length > 0 ? "+" + _lastModifiers : string.Empty));
            _keyLog = string.Join(",", _keyEvents.ToArray());

            // The effect half: a delivered Ctrl+Z has to visibly undo something, otherwise "the callback
            // ran" is still not proof the window is usable by keyboard.
            if (evt.keyCode == KeyCode.Z && (evt.ctrlKey || evt.commandKey))
            {
                Undo();
                evt.StopPropagation();
            }

            RefreshReadout();
        }

        private void OnKeyUp(KeyUpEvent evt)
        {
            _keyUpCount++;
            Record(_keyEvents, "up:" + evt.keyCode);
            _keyLog = string.Join(",", _keyEvents.ToArray());
            RefreshReadout();
        }

        private void OnImguiKeys()
        {
            var current = Event.current;
            if (current == null) return;

            if (current.type == EventType.KeyDown)
            {
                _imguiKeyDownCount++;
                _imguiLastKeyCode = current.keyCode.ToString();
                _imguiLastModifiers = current.modifiers.ToString();
            }
            else if (current.type == EventType.KeyUp)
            {
                _imguiKeyUpCount++;
            }

            GUILayout.Label("imgui keyDown " + _imguiKeyDownCount.ToString(CultureInfo.InvariantCulture)
                            + "  |  uitk keyDown " + _keyDownCount.ToString(CultureInfo.InvariantCulture));
        }

        private static string DescribeModifiers(KeyDownEvent evt)
        {
            var parts = new List<string>();
            if (evt.ctrlKey) parts.Add("ctrl");
            if (evt.shiftKey) parts.Add("shift");
            if (evt.altKey) parts.Add("alt");
            if (evt.commandKey) parts.Add("command");
            return string.Join("+", parts.ToArray());
        }

        private void Undo()
        {
            if (_orderBeforeLastDrop.Length == 0) return;

            _items.Clear();
            _items.AddRange(_orderBeforeLastDrop.Split(','));
            _orderBeforeLastDrop = string.Empty;
            _undoCount++;
            _order = string.Join(",", _items.ToArray());
            BuildRows();
            CaptureGeometry();
        }

        // ------------------------------------------------------------------ rail drag

        private void OnRowMouseDown(MouseDownEvent evt, int index)
        {
            if (evt.button != 0) return;

            _mouseDownCount++;
            _pressed = true;
            _pressedIndex = index;
            _pressPoint = evt.mousePosition;
            _dragArmed = false;
            _armedAtDistance = -1f;
            _dragSourceIndex = -1;

            _pathPoints.Clear();
            Record(_pathPoints, "down@" + Format(evt.mousePosition));
            _dragPath = string.Join(" ", _pathPoints.ToArray());

            RefreshReadout();
        }

        /// <summary>
        /// Arms on the first move that clears the threshold, which is what makes a short gesture a real
        /// test: the distance the caller's arm phase travels decides whether this ever fires.
        /// </summary>
        private void OnRowMouseMove(MouseMoveEvent evt)
        {
            if (!_pressed || _dragArmed) return;

            _armAttemptCount++;
            var travelled = Vector2.Distance(evt.mousePosition, _pressPoint);
            Record(_pathPoints, "move@" + Format(evt.mousePosition) + "(d" + F(travelled) + ")");
            _dragPath = string.Join(" ", _pathPoints.ToArray());

            if (travelled < ArmThreshold)
            {
                RefreshReadout();
                return;
            }

            _dragArmed = true;
            _armedAtDistance = travelled;
            _dragSourceIndex = _pressedIndex;
            _orderAtDragStart = new List<string>(_items);

            // Deliberately no DragAndDrop.StartDrag here. StartDrag enters Windows' own modal drag
            // loop, which waits for a physical button release that a synthesized gesture never makes -
            // measured: the editor tick stopped dead and 12 bridge requests queued up unprocessed until
            // Unity was restarted. Arming the payload is what a receiver actually reads
            // (DragAndDrop.GetGenericData), and it is what the bridge's own genericDataKey contract is
            // built on, so this is the part worth reproducing.
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new UnityEngine.Object[0];
            DragAndDrop.SetGenericData("MCPKeyProbeRow", _pressedIndex);

            RefreshReadout();
        }

        private void OnRowMouseUp(MouseUpEvent evt)
        {
            _pressed = false;
            Record(_pathPoints, "up@" + Format(evt.mousePosition));
            _dragPath = string.Join(" ", _pathPoints.ToArray());
            RefreshReadout();
        }

        private void OnRowDragUpdated(DragUpdatedEvent evt, int index)
        {
            if (DragAndDrop.GetGenericData("MCPKeyProbeRow") == null) return;

            _dragUpdatedCount++;
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            Record(_pathPoints, "update@row" + index.ToString(CultureInfo.InvariantCulture));
            _dragPath = string.Join(" ", _pathPoints.ToArray());
            evt.StopPropagation();
            RefreshReadout();
        }

        private void OnRowDragPerform(DragPerformEvent evt, int index)
        {
            var data = DragAndDrop.GetGenericData("MCPKeyProbeRow");
            if (data == null) return;

            _dragPerformCount++;
            DragAndDrop.AcceptDrag();

            var from = data is int ? (int)data : -1;
            if (from >= 0 && from < _items.Count && index >= 0 && index < _items.Count && from != index)
            {
                _orderBeforeLastDrop = _orderAtDragStart != null
                    ? string.Join(",", _orderAtDragStart.ToArray())
                    : _order;

                var moved = _items[from];
                _items.RemoveAt(from);
                _items.Insert(index, moved);

                _dropCount++;
                _lastDropIndex = index;
                _order = string.Join(",", _items.ToArray());

                BuildRows();
                CaptureGeometry();
            }

            Record(_pathPoints, "perform@row" + index.ToString(CultureInfo.InvariantCulture));
            _dragPath = string.Join(" ", _pathPoints.ToArray());
            evt.StopPropagation();
            RefreshReadout();
        }

        private void OnRowDragExited(DragExitedEvent evt)
        {
            _dragExitedCount++;
            Record(_pathPoints, "exited");
            _dragPath = string.Join(" ", _pathPoints.ToArray());
            RefreshReadout();
        }

        // ------------------------------------------------------------------ bookkeeping

        /// <summary>Puts every counter back, so one measurement cannot be read as the next one's result.</summary>
        public void ResetCounters()
        {
            _keyDownCount = 0;
            _keyUpCount = 0;
            _rootKeyDownCount = 0;
            _lastKeyCode = string.Empty;
            _lastModifiers = string.Empty;
            _lastCharacter = string.Empty;
            _keyLog = string.Empty;
            _imguiKeyDownCount = 0;
            _imguiKeyUpCount = 0;
            _imguiLastKeyCode = string.Empty;
            _imguiLastModifiers = string.Empty;
            _focusCount = 0;
            _undoCount = 0;
            _orderBeforeLastDrop = string.Empty;
            _dropdownChangeCount = 0;
            _dropdownPreviousValue = string.Empty;
            _mouseDownCount = 0;
            _armAttemptCount = 0;
            _dragArmed = false;
            _armedAtDistance = -1f;
            _dragSourceIndex = -1;
            _dragUpdatedCount = 0;
            _dragPerformCount = 0;
            _dragExitedCount = 0;
            _dropCount = 0;
            _lastDropIndex = -1;
            _dragPath = string.Empty;
            _pressed = false;
            _pressedIndex = -1;
            _keyEvents.Clear();
            _pathPoints.Clear();

            _items.Clear();
            for (var i = 0; i < RowCount; i++)
            {
                _items.Add("item" + i.ToString(CultureInfo.InvariantCulture));
            }
            _order = string.Join(",", _items.ToArray());

            if (_dropdown != null)
            {
                _dropdownValue = _dropdown.value ?? string.Empty;
            }

            if (_rail != null)
            {
                BuildRows();
                CaptureGeometry();
            }

            RefreshReadout();
        }

        /// <summary>
        /// Opens a borderless popup over this window, which is what editor_window_capture's
        /// includePopups has to find. Unlike a modal it does not block the editor, so the capture
        /// command can still be sent while it is on screen - which is the only way to photograph one.
        /// </summary>
        public void ShowOverlayPopup()
        {
            if (_popup != null)
            {
                _popup.Close();
                _popup = null;
            }

            _popup = CreateInstance<KeyProbePopup>();
            _popup.position = new Rect(position.x + 90f, position.y + 150f, 260f, 96f);
            _popup.ShowPopup();
            _popup.Repaint();
            Repaint();
        }

        /// <summary>Closes the overlay popup, so one measurement does not leak into the next.</summary>
        public void CloseOverlayPopup()
        {
            if (_popup != null)
            {
                _popup.Close();
                _popup = null;
            }
            Repaint();
        }

        /// <summary>
        /// Raises a real modal dialog, so the dialog commands have something to be tested against.
        ///
        /// This call does not return until the box is dismissed, and the editor stops ticking for the
        /// whole time - which is the point. A caller that has not armed editor_dialog_click first will
        /// see its own request time out, because the bridge cannot read it while this sits here.
        /// </summary>
        public void RaiseDialog()
        {
            _dialogChoice = EditorUtility.DisplayDialog(
                "MCP Probe Dialog",
                "This dialog exists to be dismissed by editor_dialog_click.",
                "Accept",
                "Cancel")
                ? "Accept"
                : "Cancel";
            _dialogCount++;
            RefreshReadout();
        }

        /// <summary>
        /// Resizes the rail. A narrow one is the case that matters: arming moves that travel sideways
        /// regardless of where the drop is can leave a source only a few tens of pixels wide, and a
        /// gesture that leaves its source before it arms never arms.
        /// </summary>
        public void SetRailWidth(float width)
        {
            _railWidth = Mathf.Clamp(width, 8f, 400f);
            if (_rail != null)
            {
                _rail.style.width = _railWidth;
                BuildRows();
                CaptureGeometry();
            }
            RefreshReadout();
        }

        /// <summary>Moves focus onto the key area, so a caller can retry delivery without clicking.</summary>
        public void FocusKeyArea()
        {
            if (_keyArea != null)
            {
                _keyArea.Focus();
            }
            RefreshReadout();
        }

        private void CaptureGeometry()
        {
            if (_rail == null) return;

            _railWorldBound = _rail.worldBound;
            _row0WorldBound = _rows.Count > 0 ? _rows[0].worldBound : new Rect();
            _rootOffset = rootVisualElement != null
                ? new Vector2(rootVisualElement.worldBound.x, rootVisualElement.worldBound.y)
                : Vector2.zero;
            RefreshReadout();
        }

        private void RefreshReadout()
        {
            var focused = rootVisualElement != null && rootVisualElement.panel != null
                ? rootVisualElement.panel.focusController.focusedElement
                : null;
            _focusedElement = focused == null
                ? string.Empty
                : (focused as VisualElement) != null && ((VisualElement)focused).name.Length > 0
                    ? ((VisualElement)focused).name
                    : focused.GetType().Name;

            if (_dropdown != null)
            {
                _dropdownValue = _dropdown.value ?? string.Empty;
            }

            if (_readout == null) return;

            var sb = new StringBuilder();
            sb.Append("uitk keyDown ").Append(_keyDownCount);
            sb.Append(" | root ").Append(_rootKeyDownCount);
            sb.Append(" | imgui ").Append(_imguiKeyDownCount);
            sb.Append(" | focus '").Append(_focusedElement).Append('\'');
            sb.Append('\n');
            sb.Append("last ").Append(_lastKeyCode);
            if (_lastModifiers.Length > 0) sb.Append('+').Append(_lastModifiers);
            sb.Append(" | undo ").Append(_undoCount);
            sb.Append('\n');
            sb.Append("armed ").Append(_dragArmed ? "yes" : "no");
            sb.Append(" @d").Append(F(_armedAtDistance));
            sb.Append(" | update ").Append(_dragUpdatedCount);
            sb.Append(" | perform ").Append(_dragPerformCount);
            sb.Append(" | drops ").Append(_dropCount);
            sb.Append('\n');
            sb.Append("order ").Append(_order);
            sb.Append('\n');
            sb.Append("dropdown '").Append(_dropdownValue).Append("' changes ").Append(_dropdownChangeCount);

            _readout.text = sb.ToString();
            Repaint();
        }

        private static void Record(List<string> log, string entry)
        {
            log.Add(entry);
            // Bounded so a long session cannot turn one field read into a wall of text.
            if (log.Count > 24) log.RemoveAt(0);
        }

        private static string Format(Vector2 point)
        {
            return F(point.x) + "," + F(point.y);
        }

        private static string F(float value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The overlay for the capture test. It is deliberately loud and flat-coloured: proving it landed
    /// in the frame means finding its pixels, and a subtle window would make that a judgement call.
    /// </summary>
    public sealed class KeyProbePopup : EditorWindow
    {
        internal static readonly Color Fill = new Color(1f, 0.25f, 0.1f);

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0f, 0f, position.width, position.height), Fill);

            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(10f, 34f, position.width - 20f, 24f), "MCP overlay popup", style);
        }
    }
}
