using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Editor-tool (EditorWindow) automation commands for the file bridge.
    ///
    /// The pre-existing input commands drive the *runtime* NGUI stack (NguiRaycast + UICamera.Notify),
    /// which editor windows never go through: EditorWindow draws with IMGUI, an immediate-mode system
    /// with no retained widget tree to walk. So editor tools were untestable over MCP.
    ///
    /// This class covers that gap with three complementary mechanisms, in order of reliability:
    ///   1. Reflection over the window instance - in-house editor tools keep their state in instance
    ///      fields that OnGUI reads and writes, so dumping and setting those fields drives most tools
    ///      and gives a directly readable before/after as evidence.
    ///   2. Real IMGUI event injection (EditorWindow.SendEvent) - a MouseDown/MouseUp pair at a point
    ///      makes GUILayout.Button actually fire, so the tool's own callback runs rather than being
    ///      bypassed.
    ///   3. IMGUI layout-rect recovery - the layout cache the IMGUIContainer keeps after a layout pass
    ///      still holds every entry's rect and style, which turns (2) from "guess a coordinate" into
    ///      "click entry #7, the third button-styled rect".
    /// </summary>
    internal static class EditorToolBridge
    {
        internal const string BridgeVersion = "0.4.5";

        private const int DefaultMaxDepth = 1;
        private const int ValuePreviewLimit = 400;

        internal static readonly string[] SupportedCommands =
        {
            "editor_window_list",
            "editor_window_open",
            "editor_window_close",
            "editor_window_focus",
            "editor_window_dump",
            "editor_window_screenshot",
            "editor_window_capture",
            "editor_drag",
            "editor_drag_capture",
            "editor_move",
            "editor_scroll",
            "editor_element_query",
            "editor_selection_get",
            "editor_selection_set",
            "editor_set_field",
            "editor_get_field",
            "editor_invoke_method",
            "editor_click",
            "editor_context_click",
            "editor_key",
            "editor_menu_execute",
            "editor_menu_list",
            "editor_console_read",
            "editor_console_clear",
            "editor_prefs_get",
            "editor_prefs_set",
            "editor_play_mode",
            "exit_play_mode",
        };

        internal static bool TryExecute(string command, CommandParameters p, CommandResponse response)
        {
            switch (command)
            {
                case "editor_window_list": ListWindows(p, response); return true;
                case "editor_window_open": OpenWindow(p, response); return true;
                case "editor_window_close": CloseWindow(p, response); return true;
                case "editor_window_focus": FocusWindow(p, response); return true;
                case "editor_window_dump": DumpWindow(p, response); return true;
                case "editor_window_screenshot": ScreenshotWindow(p, response); return true;
                case "editor_window_capture": CaptureWindow(p, response); return true;
                case "editor_drag": Drag(p, response); return true;
                case "editor_drag_capture": DragCapture(p, response); return true;
                case "editor_drag_drop": DragDrop(p, response); return true;
                case "editor_move": Move(p, response); return true;
                case "editor_scroll": Scroll(p, response); return true;
                case "editor_element_query": ElementQuery(p, response); return true;
                case "editor_selection_get": SelectionGet(response); return true;
                case "editor_selection_set": SelectionSet(p, response); return true;
                case "editor_set_field": SetField(p, response); return true;
                case "editor_get_field": GetField(p, response); return true;
                case "editor_invoke_method": InvokeMethod(p, response); return true;
                case "editor_click": Click(p, response); return true;
                case "editor_context_click": ContextClick(p, response); return true;
                case "editor_key": Key(p, response); return true;
                case "editor_menu_execute": MenuExecute(p, response); return true;
                case "editor_menu_list": MenuList(p, response); return true;
                case "editor_console_read": ConsoleRead(p, response); return true;
                case "editor_console_clear": ConsoleClear(p, response); return true;
                case "editor_prefs_get": PrefsGet(p, response); return true;
                case "editor_prefs_set": PrefsSet(p, response); return true;
                case "editor_play_mode": PlayMode(p, response); return true;
                case "exit_play_mode": ExitPlayMode(response); return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------------ windows

        private static void ListWindows(CommandParameters p, CommandResponse response)
        {
            var windows = AllWindows();
            var sb = new StringBuilder("[");
            for (var i = 0; i < windows.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(DescribeWindow(windows[i]));
            }
            sb.Append(']');

            response.AddOutput("count", windows.Length.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("windows", sb.ToString());
            response.AddOutput("focused", EditorWindow.focusedWindow != null
                ? EditorWindow.focusedWindow.GetType().FullName
                : string.Empty);
        }

        private static void OpenWindow(CommandParameters p, CommandResponse response)
        {
            EditorWindow window;

            if (!string.IsNullOrEmpty(p.menuPath))
            {
                if (!EditorApplication.ExecuteMenuItem(p.menuPath))
                {
                    throw new InvalidOperationException($"Menu item failed or not found: {p.menuPath}");
                }

                response.AddOutput("openedVia", "menu");
                response.AddOutput("menuPath", p.menuPath);

                // The menu handler decides which window it opens, so re-resolve rather than assume.
                window = !string.IsNullOrEmpty(p.windowType)
                    ? ResolveWindow(p)
                    : EditorWindow.focusedWindow;
            }
            else
            {
                var typeName = Require(p.windowType, "windowType (or menuPath)");
                var type = FindWindowType(typeName);
                if (type == null)
                {
                    throw new InvalidOperationException(
                        $"EditorWindow type not found: {typeName}. Use editor_menu_list or pass the full type name.");
                }

                var title = string.IsNullOrEmpty(p.windowTitle) ? type.Name : p.windowTitle;
                window = EditorWindow.GetWindow(type, p.utility, title, !p.noFocus);
                response.AddOutput("openedVia", "type");
            }

            if (window == null)
            {
                throw new InvalidOperationException("Window did not open, or could not be resolved after opening.");
            }

            // A window that has never laid out has no layout cache to inspect, so force one pass now.
            RepaintImmediate(window);

            response.AddOutput("window", DescribeWindow(window));
            response.AddOutput("instanceId", window.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            response.AddOutput("windowType", window.GetType().FullName);
            response.AddOutput("opened", "true");
        }

        private static void CloseWindow(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var described = DescribeWindow(window);
            window.Close();
            response.AddOutput("closed", "true");
            response.AddOutput("window", described);
        }

        private static void FocusWindow(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            window.Focus();
            window.Repaint();
            RepaintImmediate(window);
            response.AddOutput("focused", "true");
            response.AddOutput("window", DescribeWindow(window));
        }

        private static void DumpWindow(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            RepaintImmediate(window);

            var maxDepth = p.maxDepth > 0 ? p.maxDepth : DefaultMaxDepth;

            response.AddOutput("window", DescribeWindow(window));
            response.AddOutput("windowType", window.GetType().FullName);
            response.AddOutput("instanceId", window.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            response.AddOutput("fields", DumpFields(window, maxDepth, !p.publicOnly));
            response.AddOutput("methods", DumpMethods(window));

            string layoutDiagnostics;
            Vector2 containerOffset;
            response.AddOutput("layout", DumpLayout(window, out layoutDiagnostics, out containerOffset));
            response.AddOutput("layoutDiagnostics", layoutDiagnostics);
            response.AddOutput("containerOffset", F(containerOffset.x) + "," + F(containerOffset.y));
            response.AddOutput("visualTree", DumpVisualTree(window));
        }

        private static string DescribeWindow(EditorWindow window)
        {
            var rect = window.position;
            var sb = new StringBuilder("{");
            sb.Append("\"type\":\"").Append(Esc(window.GetType().FullName)).Append("\",");
            sb.Append("\"assembly\":\"").Append(Esc(window.GetType().Assembly.GetName().Name)).Append("\",");
            sb.Append("\"title\":\"").Append(Esc(window.titleContent != null ? window.titleContent.text : string.Empty)).Append("\",");
            sb.Append("\"instanceId\":").Append(window.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"focused\":").Append(EditorWindow.focusedWindow == window ? "true" : "false").Append(',');
            sb.Append("\"hasFocus\":").Append(window.hasFocus ? "true" : "false").Append(',');
            sb.Append("\"x\":").Append(F(rect.x)).Append(',');
            sb.Append("\"y\":").Append(F(rect.y)).Append(',');
            sb.Append("\"width\":").Append(F(rect.width)).Append(',');
            sb.Append("\"height\":").Append(F(rect.height));
            sb.Append('}');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ fields

        private static string DumpFields(object instance, int maxDepth, bool includePrivate)
        {
            var sb = new StringBuilder("[");
            var first = true;

            foreach (var field in EnumerateFields(instance.GetType(), includePrivate))
            {
                if (!first) sb.Append(',');
                first = false;

                object value = null;
                string error = null;
                try
                {
                    value = field.GetValue(instance);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }

                sb.Append('{');
                sb.Append("\"name\":\"").Append(Esc(field.Name)).Append("\",");
                sb.Append("\"type\":\"").Append(Esc(FriendlyTypeName(field.FieldType))).Append("\",");
                sb.Append("\"declaredIn\":\"").Append(Esc(field.DeclaringType != null ? field.DeclaringType.Name : string.Empty)).Append("\",");
                sb.Append("\"access\":\"").Append(field.IsPublic ? "public" : "private").Append("\",");
                sb.Append("\"settable\":").Append(IsCoercible(field.FieldType) ? "true" : "false").Append(',');
                if (error != null)
                {
                    sb.Append("\"error\":\"").Append(Esc(error)).Append("\",");
                }
                sb.Append("\"value\":\"").Append(Esc(DescribeValue(value, maxDepth))).Append('"');
                sb.Append('}');
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static IEnumerable<FieldInfo> EnumerateFields(Type type, bool includePrivate)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate)
            {
                flags |= BindingFlags.NonPublic;
            }

            // Stop at EditorWindow: everything above it is Unity's own plumbing, not the tool's state.
            for (var current = type; current != null && current != typeof(EditorWindow); current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags))
                {
                    if (field.Name.IndexOf("k__BackingField", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    yield return field;
                }
            }
        }

        private static string DumpMethods(object instance)
        {
            var sb = new StringBuilder("[");
            var first = true;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (var current = instance.GetType(); current != null && current != typeof(EditorWindow); current = current.BaseType)
            {
                foreach (var method in current.GetMethods(flags))
                {
                    if (method.IsSpecialGetterOrSetter())
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Any(x => !IsCoercible(x.ParameterType)))
                    {
                        continue;
                    }

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('{');
                    sb.Append("\"name\":\"").Append(Esc(method.Name)).Append("\",");
                    sb.Append("\"returns\":\"").Append(Esc(FriendlyTypeName(method.ReturnType))).Append("\",");
                    sb.Append("\"parameters\":\"")
                        .Append(Esc(string.Join(", ", parameters.Select(x => FriendlyTypeName(x.ParameterType) + " " + x.Name))))
                        .Append('"');
                    sb.Append('}');
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static void GetField(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var path = Require(p.fieldPath, "fieldPath");

            object owner;
            MemberInfo member;
            ResolveMemberPath(window, path, out owner, out member);

            var value = ReadMember(owner, member);
            response.AddOutput("fieldPath", path);
            response.AddOutput("type", FriendlyTypeName(GetMemberType(member)));
            response.AddOutput("value", DescribeValue(value, p.maxDepth > 0 ? p.maxDepth : 2));
        }

        private static void SetField(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var path = Require(p.fieldPath, "fieldPath");

            object owner;
            MemberInfo member;
            ResolveMemberPath(window, path, out owner, out member);

            var targetType = GetMemberType(member);
            var before = ReadMember(owner, member);
            var coerced = Coerce(p.fieldValue, targetType);

            WriteMember(owner, member, coerced);

            // Re-read instead of echoing what we wrote: a property setter may clamp or ignore the value,
            // and that difference is exactly what makes this an evidence trail rather than a claim.
            var after = ReadMember(owner, member);

            window.Repaint();
            RepaintImmediate(window);

            response.AddOutput("fieldPath", path);
            response.AddOutput("type", FriendlyTypeName(targetType));
            response.AddOutput("before", DescribeValue(before, 1));
            response.AddOutput("after", DescribeValue(after, 1));
            response.AddOutput("changed", !Equals(SafeToString(before), SafeToString(after)) ? "true" : "false");
        }

        private static void InvokeMethod(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var name = Require(p.methodName, "methodName");
            // methodArgsText, not methodArgs: this bridge takes the arguments as one
            // Unit-Separator-joined string, while invoke_static_method takes a real list.
            var args = SplitArgs(p.methodArgsText);

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo method = null;
            for (var current = window.GetType(); current != null && method == null && current != typeof(EditorWindow); current = current.BaseType)
            {
                method = current
                    .GetMethods(flags | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            }

            if (method == null)
            {
                throw new MissingMethodException($"Method not found on {window.GetType().Name}: {name} with {args.Length} parameter(s).");
            }

            var parameters = method.GetParameters();
            var typedArgs = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                typedArgs[i] = Coerce(args[i], parameters[i].ParameterType);
            }

            var result = method.Invoke(window, typedArgs);

            window.Repaint();
            RepaintImmediate(window);

            response.AddOutput("methodName", name);
            response.AddOutput("returns", FriendlyTypeName(method.ReturnType));
            response.AddOutput("result", DescribeValue(result, 2));
        }

        /// <summary>
        /// Walks a dotted/indexed path such as <c>_resolutions[0].name</c> and hands back the object that
        /// directly owns the final member, so the caller can read or write it.
        /// </summary>
        private static void ResolveMemberPath(object root, string path, out object owner, out MemberInfo member)
        {
            var segments = path.Split('.');
            owner = root;
            member = null;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var indices = new List<int>();

                var bracket = segment.IndexOf('[');
                if (bracket >= 0)
                {
                    var indexPart = segment.Substring(bracket);
                    segment = segment.Substring(0, bracket);
                    foreach (var raw in indexPart.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int parsed;
                        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            throw new ArgumentException($"Bad index '{raw}' in path segment '{segments[i]}'.");
                        }
                        indices.Add(parsed);
                    }
                }

                if (owner == null)
                {
                    throw new NullReferenceException($"Path '{path}' hit a null before segment '{segment}'.");
                }

                var found = FindMember(owner.GetType(), segment);
                if (found == null)
                {
                    throw new MissingMemberException($"No field or property '{segment}' on {owner.GetType().Name} (path '{path}').");
                }

                var isLast = i == segments.Length - 1 && indices.Count == 0;
                if (isLast)
                {
                    member = found;
                    return;
                }

                var value = ReadMember(owner, found);
                for (var k = 0; k < indices.Count; k++)
                {
                    var isLastIndex = i == segments.Length - 1 && k == indices.Count - 1;
                    if (isLastIndex)
                    {
                        member = new IndexerMember(value, indices[k]);
                        owner = value;
                        return;
                    }

                    value = ReadIndex(value, indices[k]);
                }

                owner = value;
            }

            throw new ArgumentException($"Could not resolve path '{path}'.");
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null) return field;

                var property = current.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null) return property;
            }

            return null;
        }

        private static object ReadIndex(object collection, int index)
        {
            if (collection == null)
            {
                throw new NullReferenceException("Cannot index into null.");
            }

            var list = collection as IList;
            if (list == null)
            {
                throw new InvalidOperationException($"{collection.GetType().Name} is not indexable by position.");
            }

            if (index < 0 || index >= list.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of range (count {list.Count}).");
            }

            return list[index];
        }

        private static Type GetMemberType(MemberInfo member)
        {
            var field = member as FieldInfo;
            if (field != null) return field.FieldType;

            var property = member as PropertyInfo;
            if (property != null) return property.PropertyType;

            var indexer = member as IndexerMember;
            if (indexer != null) return indexer.ElementType;

            throw new NotSupportedException($"Unsupported member kind: {member.GetType().Name}");
        }

        private static object ReadMember(object owner, MemberInfo member)
        {
            var field = member as FieldInfo;
            if (field != null) return field.GetValue(owner);

            var property = member as PropertyInfo;
            if (property != null)
            {
                if (!property.CanRead) throw new InvalidOperationException($"Property '{property.Name}' is write-only.");
                return property.GetValue(owner, null);
            }

            var indexer = member as IndexerMember;
            if (indexer != null) return indexer.Read();

            throw new NotSupportedException($"Unsupported member kind: {member.GetType().Name}");
        }

        private static void WriteMember(object owner, MemberInfo member, object value)
        {
            var field = member as FieldInfo;
            if (field != null)
            {
                field.SetValue(owner, value);
                return;
            }

            var property = member as PropertyInfo;
            if (property != null)
            {
                if (!property.CanWrite) throw new InvalidOperationException($"Property '{property.Name}' is read-only.");
                property.SetValue(owner, value, null);
                return;
            }

            var indexer = member as IndexerMember;
            if (indexer != null)
            {
                indexer.Write(value);
                return;
            }

            throw new NotSupportedException($"Unsupported member kind: {member.GetType().Name}");
        }

        /// <summary>MemberInfo stand-in for "element N of this list", so indexed paths write back in place.</summary>
        private sealed class IndexerMember : MemberInfo
        {
            private readonly IList list;
            private readonly int index;

            public IndexerMember(object collection, int index)
            {
                list = collection as IList;
                if (list == null)
                {
                    throw new InvalidOperationException("Indexed path segment requires a list-like value.");
                }

                if (index < 0 || index >= list.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of range (count {list.Count}).");
                }

                this.index = index;
            }

            public Type ElementType
            {
                get
                {
                    var value = list[index];
                    if (value != null) return value.GetType();

                    var listType = list.GetType();
                    if (listType.IsArray) return listType.GetElementType();
                    if (listType.IsGenericType) return listType.GetGenericArguments()[0];
                    return typeof(object);
                }
            }

            public object Read() { return list[index]; }
            public void Write(object value) { list[index] = value; }

            public override Type DeclaringType { get { return list.GetType(); } }
            public override MemberTypes MemberType { get { return MemberTypes.Custom; } }
            public override string Name { get { return "[" + index + "]"; } }
            public override Type ReflectedType { get { return list.GetType(); } }
            public override object[] GetCustomAttributes(bool inherit) { return Array.Empty<object>(); }
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) { return Array.Empty<object>(); }
            public override bool IsDefined(Type attributeType, bool inherit) { return false; }
        }

        // ------------------------------------------------------------------ input injection

        /// <summary>The right button, which is what "context click" means everywhere in the editor.</summary>
        private const int RightButton = 1;

        /// <summary>
        /// The pixel a click lands on, in the host-view space SendEvent delivers into.
        ///
        /// editor_click and editor_context_click share this so the two cannot drift apart: the same
        /// entry index, the same element filters and the same x/y aim at the same pixel in both, and
        /// only the events sent afterwards differ.
        ///
        /// Explicit coordinates default to host-view space here, not to content space - that is what
        /// editor_click's x/y has always meant, and what editor_element_query hands back. The drag,
        /// move and scroll commands default the other way; pass coordinateSpace "content" to measure
        /// from the window's content corner and have the dock tab strip added, as they do.
        /// </summary>
        private static Vector2 ResolveClickPoint(EditorWindow window, CommandParameters p,
            CommandResponse response, List<string> notes)
        {
            if (string.Equals(p.targetMode, "entry", StringComparison.OrdinalIgnoreCase))
            {
                Vector2 containerOffset;
                var rect = ResolveLayoutRect(window, p.entryIndex, out containerOffset);

                response.AddOutput("resolvedFrom", "layoutEntry");
                response.AddOutput("entryIndex", p.entryIndex.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("entryRect", RectJson(rect));
                response.AddOutput("containerOffset", F(containerOffset.x) + "," + F(containerOffset.y));

                // Layout rects are local to the IMGUIContainer; SendEvent lands in the host view's
                // space, which also contains the tab strip. Without the offset the click misses high.
                return new Vector2(
                    containerOffset.x + rect.x + rect.width * 0.5f,
                    containerOffset.y + rect.y + rect.height * 0.5f);
            }

            if (string.Equals(p.targetMode, "element", StringComparison.OrdinalIgnoreCase))
            {
                // A UI Toolkit element's worldBound is already in the space SendEvent delivers into, so
                // its centre needs no offset - and it survives a resize, unlike a hard-coded pixel.
                return ResolveElementPoint(window, p, response);
            }

            var space = string.IsNullOrEmpty(p.coordinateSpace)
                ? "host"
                : p.coordinateSpace.Trim().ToLowerInvariant();
            if (space != "content" && space != "host")
            {
                throw new ArgumentException(
                    "coordinateSpace must be 'host' (the default for clicks) or 'content'.");
            }

            var offset = Vector2.zero;
            if (space == "content")
            {
                var border = BorderSize(window, notes);
                if (border != null)
                {
                    offset = new Vector2(border.left, border.top);
                }
            }

            response.AddOutput("resolvedFrom", "point");
            response.AddOutput("coordinateSpace", space);
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            return new Vector2(p.x, p.y) + offset;
        }

        private static void Click(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            // Layout rects only exist once a pass has run, so refresh before resolving an entry index.
            RepaintImmediate(window);

            var point = ResolveClickPoint(window, p, response, notes);

            var modifiers = ParseModifiers(p.modifiers);
            var button = p.button;
            var clickCount = p.clickCount > 0 ? p.clickCount : 1;

            window.Focus();

            var down = new Event
            {
                type = EventType.MouseDown,
                mousePosition = point,
                button = button,
                clickCount = clickCount,
                modifiers = modifiers,
            };
            var downHandled = SendEvent(window, down);

            var up = new Event
            {
                type = EventType.MouseUp,
                mousePosition = point,
                button = button,
                clickCount = clickCount,
                modifiers = modifiers,
            };
            var upHandled = SendEvent(window, up);

            window.Repaint();
            RepaintImmediate(window);

            response.AddOutput("x", F(point.x));
            response.AddOutput("y", F(point.y));
            response.AddOutput("button", button.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("clickCount", clickCount.ToString(CultureInfo.InvariantCulture));
            // SendEvent's return value is not a reliable "the control reacted" signal - a click that
            // genuinely fires a button can still come back false - so these are reported as the raw
            // return values, not as proof. Confirm the effect by reading the tool's own state.
            response.AddOutput("mouseDownReturned", downHandled ? "true" : "false");
            response.AddOutput("mouseUpReturned", upHandled ? "true" : "false");
            response.AddOutput("clicked", "true");

            if (notes.Count > 0)
            {
                response.AddOutput("clickNotes", string.Join(" | ", notes.ToArray()));
            }
        }

        /// <summary>
        /// Opens a context menu at a point, the way a real right-click does.
        ///
        /// What editor_click with button 1 misses is one event, not the button. Measured on 2022.3.62,
        /// Windows, against MoveProbeWindow:
        ///
        ///   - UI Toolkit already worked. A ContextualMenuManipulator listens on MouseUpEvent on this
        ///     platform, so the pair editor_click sends is enough - a right editor_click on the probe's
        ///     GraphView runs BuildContextualMenu and displays the menu.
        ///   - IMGUI never fired. OnGUI code builds a GenericMenu by testing Event.current.type against
        ///     EventType.ContextClick, and editor_click delivers no such event, so that branch is dead.
        ///
        /// Unity's native input layer synthesises ContextClick for a real right-click;
        /// EditorWindow.SendEvent does not. So this command sends it, which both reaches IMGUI menu code
        /// and makes the gesture what a real right-click is rather than what UI Toolkit alone accepts.
        ///
        /// The press pair still goes first, and in that order, because a context menu is a gesture
        /// rather than a lone event: handlers that track which button is down, dismiss an already-open
        /// popup, or take the menu's anchor from the press would otherwise see a ContextClick arrive
        /// out of nowhere - and on the UI Toolkit path that pair is what opens the menu at all.
        /// </summary>
        private static void ContextClick(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            // Layout rects only exist once a pass has run, so refresh before resolving an entry index.
            RepaintImmediate(window);

            // Deliberately the same resolver editor_click uses: a right-click has to be able to aim at
            // exactly the pixel a left-click aimed at, or the two commands describe different targets.
            var point = ResolveClickPoint(window, p, response, notes);
            var modifiers = ParseModifiers(p.modifiers);

            window.Focus();

            var down = new Event
            {
                type = EventType.MouseDown,
                mousePosition = point,
                button = RightButton,
                clickCount = 1,
                modifiers = modifiers,
            };
            var downHandled = SendEvent(window, down);

            var up = new Event
            {
                type = EventType.MouseUp,
                mousePosition = point,
                button = RightButton,
                clickCount = 1,
                modifiers = modifiers,
            };
            var upHandled = SendEvent(window, up);

            // The event that actually opens the menu, and the one whose position the menu is placed at -
            // so it carries the resolved point rather than wherever the pointer happens to be.
            var contextClick = new Event
            {
                type = EventType.ContextClick,
                mousePosition = point,
                button = RightButton,
                clickCount = 1,
                modifiers = modifiers,
            };

            // Timed, because a menu that really displays does not return until it is dismissed: Unity
            // shows it from inside this call, and the editor's main thread - the bridge included - is
            // held for that whole time. Measured here at 4s, 24s and 123s for the same click, the
            // difference being only how long the menu happened to stay up. Reporting the number is what
            // turns "the command was slow" into "a menu was open for 24 seconds".
            var startedTicks = DateTime.UtcNow.Ticks;
            var contextClickHandled = SendEvent(window, contextClick);
            var blockedMs = (DateTime.UtcNow.Ticks - startedTicks) / TimeSpan.TicksPerMillisecond;

            window.Repaint();
            RepaintImmediate(window);

            DescribeTarget(window, response);
            response.AddOutput("x", F(point.x));
            response.AddOutput("y", F(point.y));
            response.AddOutput("button", RightButton.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("clickCount", "1");
            response.AddOutput("modifiers", modifiers.ToString());
            response.AddOutput("eventsSent", "3");
            response.AddOutput("eventOrder", "MouseDown,MouseUp,ContextClick");
            // Reported the same way editor_click reports its pair: these are the raw SendEvent returns,
            // and a right-click that genuinely opened a menu can still come back false.
            response.AddOutput("mouseDownReturned", downHandled ? "true" : "false");
            response.AddOutput("mouseUpReturned", upHandled ? "true" : "false");
            response.AddOutput("contextClickReturned", contextClickHandled ? "true" : "false");
            response.AddOutput("contextClickBlockedMs", blockedMs.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("contextClicked", "true");
            response.AddOutput("note",
                "The *Returned values are raw returns of EditorWindow.SendEvent, not proof a menu opened. " +
                "Confirm by reading the tool's own state with editor_get_field. A menu with no items builds " +
                "and displays nothing, which looks identical from here. A large contextClickBlockedMs means a " +
                "menu really did display: Unity shows it from inside this call and holds the editor's main " +
                "thread - and therefore this bridge - until it is dismissed, so no other command can run, and " +
                "the popup cannot be inspected while it is up.");

            if (notes.Count > 0)
            {
                response.AddOutput("contextClickNotes", string.Join(" | ", notes.ToArray()));
            }
        }

        private static void Key(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            window.Focus();

            var modifiers = ParseModifiers(p.modifiers);
            var keyCode = KeyCode.None;
            if (!string.IsNullOrEmpty(p.keyCode))
            {
                try
                {
                    keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), p.keyCode, true);
                }
                catch (Exception)
                {
                    throw new ArgumentException($"Unknown keyCode '{p.keyCode}'. Use a UnityEngine.KeyCode name such as Return, Escape, A.");
                }
            }

            var text = p.text ?? string.Empty;
            var sent = 0;

            if (text.Length > 0)
            {
                // Typing goes character by character: IMGUI text fields consume the character, not the keyCode.
                foreach (var character in text)
                {
                    SendEvent(window, new Event { type = EventType.KeyDown, character = character, keyCode = KeyCode.None, modifiers = modifiers });
                    SendEvent(window, new Event { type = EventType.KeyUp, character = character, keyCode = KeyCode.None, modifiers = modifiers });
                    sent += 2;
                }
            }

            if (keyCode != KeyCode.None)
            {
                SendEvent(window, new Event { type = EventType.KeyDown, keyCode = keyCode, modifiers = modifiers });
                SendEvent(window, new Event { type = EventType.KeyUp, keyCode = keyCode, modifiers = modifiers });
                sent += 2;
            }

            if (sent == 0)
            {
                throw new ArgumentException("Provide keyCode and/or text for editor_key.");
            }

            window.Repaint();
            RepaintImmediate(window);

            response.AddOutput("keyCode", keyCode.ToString());
            response.AddOutput("text", text);
            response.AddOutput("eventsSent", sent.ToString(CultureInfo.InvariantCulture));
        }

        private static EventModifiers ParseModifiers(string modifiers)
        {
            var result = EventModifiers.None;
            if (string.IsNullOrEmpty(modifiers))
            {
                return result;
            }

            foreach (var token in modifiers.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (token.Trim().ToLowerInvariant())
                {
                    case "shift": result |= EventModifiers.Shift; break;
                    case "ctrl":
                    case "control": result |= EventModifiers.Control; break;
                    case "alt": result |= EventModifiers.Alt; break;
                    case "cmd":
                    case "command": result |= EventModifiers.Command; break;
                    default: throw new ArgumentException($"Unknown modifier '{token}'. Use shift, control, alt, command.");
                }
            }

            return result;
        }

        private static bool SendEvent(EditorWindow window, Event evt)
        {
            var method = typeof(EditorWindow).GetMethod(
                "SendEvent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Event) },
                null);

            if (method == null)
            {
                throw new MissingMethodException("EditorWindow.SendEvent(Event) not found on this Unity version.");
            }

            var result = method.Invoke(window, new object[] { evt });
            return result is bool && (bool)result;
        }

        internal static void RepaintImmediate(EditorWindow window)
        {
            try
            {
                var method = typeof(EditorWindow).GetMethod(
                    "RepaintImmediately",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    method.Invoke(window, null);
                    return;
                }
            }
            catch (Exception)
            {
                // Falls through to the plain repaint below; a stale layout is better than a failed command.
            }

            window.Repaint();
        }

        // ------------------------------------------------------------------ IMGUI layout recovery

        /// <summary>
        /// Recovers every IMGUI layout rect the window produced in its last pass.
        ///
        /// IMGUI keeps no widget tree, but the IMGUIContainer that runs OnGUI holds a layout cache whose
        /// entries survive the pass, each with the rect it occupied and the GUIStyle it used. That is
        /// enough to address a control as "the third button-styled rect" and click its centre.
        /// Everything here is reflection over internals, so each step reports why it failed instead of
        /// throwing - a missing layout must not take the whole dump down.
        /// </summary>
        private static string DumpLayout(EditorWindow window, out string diagnostics, out Vector2 offset)
        {
            var notes = new List<string>();
            object topLevel;
            if (!TryGetBestLayout(window, notes, out topLevel, out offset))
            {
                diagnostics = string.Join(" | ", notes);
                return "[]";
            }

            var sb = new StringBuilder("[");
            var index = 0;
            WriteLayoutEntry(sb, topLevel, 0, ref index);
            sb.Append(']');

            notes.Add("entries=" + index.ToString(CultureInfo.InvariantCulture));
            diagnostics = string.Join(" | ", notes);
            return sb.ToString();
        }

        /// <summary>
        /// Picks the IMGUIContainer that actually ran the window's OnGUI, and reports where it sits.
        ///
        /// A docked window's host view owns several IMGUIContainers - the tab strip and the rest of the
        /// dock chrome each draw through their own - so the first one found depth-first is usually not
        /// the window's, and its layout cache looks almost empty. Rather than guess, every candidate is
        /// measured and the one with the most layout entries wins.
        ///
        /// The container's worldBound comes back with it because the two coordinate spaces differ:
        /// layout rects are local to the container, while SendEvent delivers into the host view's space.
        /// Clicking without that offset lands above the intended control by the height of the tab strip.
        /// </summary>
        private static bool TryGetBestLayout(EditorWindow window, List<string> notes, out object topLevel, out Vector2 offset)
        {
            topLevel = null;
            offset = Vector2.zero;

            // Make the window the active tab first. A docked area only runs OnGUI for the tab on top,
            // so dumping or clicking a background tab reads a layout that was never built.
            window.Focus();
            RepaintImmediate(window);

            var best = -1;
            Vector2 bestOffset = Vector2.zero;

            foreach (var candidate in CollectLayoutGroups(window, notes))
            {
                var count = 0;
                CountEntries(candidate.Group, 0, ref count);

                var rect = ReadMemberByName(candidate.Group, "rect");
                var groupRect = rect is Rect ? (Rect)rect : new Rect();
                notes.Add($"cand[{candidate.Source}]={count}@{F(groupRect.x)},{F(groupRect.y)},{F(groupRect.width)}x{F(groupRect.height)}");

                if (count > best)
                {
                    best = count;
                    topLevel = candidate.Group;
                    bestOffset = candidate.Offset;
                }
            }

            offset = bestOffset;

            if (topLevel != null)
            {
                notes.Add("chosen=" + best.ToString(CultureInfo.InvariantCulture) +
                          " offset=" + F(offset.x) + "," + F(offset.y));
            }

            return topLevel != null && best > 1;
        }

        private struct LayoutCandidate
        {
            public object Group;
            public Vector2 Offset;
            public string Source;
        }

        /// <summary>
        /// Gathers every IMGUI layout group that could belong to this window.
        ///
        /// Where a window's layout tree actually lives is not something to assume: it may hang off the
        /// host view's IMGUIContainer cache, or off one of GUILayoutUtility's own per-instance caches.
        /// So every reachable cache is collected and reported with its entry count and rect, and the
        /// richest one wins. The diagnostics list every candidate so a wrong pick is visible rather
        /// than silent.
        /// </summary>
        private static List<LayoutCandidate> CollectLayoutGroups(EditorWindow window, List<string> notes)
        {
            var found = new List<LayoutCandidate>();
            var borderTop = BorderTop(window, notes);
            var contentOffset = new Vector2(0f, borderTop);

            foreach (var container in FindImguiContainers(window, notes))
            {
                var cache = FindLayoutCache(container);
                if (cache == null)
                {
                    continue;
                }

                // The host view's own layout root spans the whole view, while the window's content is
                // shorter by exactly the chrome above it. That difference is the tab strip, measured
                // from live layout data rather than assumed from a version-specific constant.
                var measured = 0f;
                var hostGroup = ReadMemberByName(cache, "topLevel");
                var hostRect = hostGroup != null ? ReadMemberByName(hostGroup, "rect") : null;
                if (hostRect is Rect)
                {
                    var difference = ((Rect)hostRect).height - window.position.height;
                    if (difference > 0f && difference < 60f)
                    {
                        measured = difference;
                    }
                }

                var tabOffset = measured > 0f ? measured : borderTop;
                var name = string.IsNullOrEmpty(container.name) ? "unnamed" : container.name;
                notes.Add($"tabOffset[{name}]={F(tabOffset)} (measured={F(measured)} borderTop={F(borderTop)})");

                AddGroupsFromCache(found, cache, container.worldBound.position, new Vector2(0f, tabOffset), "container:" + name);
            }

            // GUILayoutUtility keys its caches by instance id, so a window's tree can live there rather
            // than on any container we can reach through the visual tree.
            try
            {
                foreach (var field in typeof(GUILayoutUtility).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    object value;
                    try { value = field.GetValue(null); }
                    catch (Exception) { continue; }

                    if (value == null)
                    {
                        continue;
                    }

                    var dictionary = value as IDictionary;
                    if (dictionary != null)
                    {
                        foreach (DictionaryEntry pair in dictionary)
                        {
                            AddGroupsFromCache(found, pair.Value, Vector2.zero, contentOffset, $"static:{field.Name}[{pair.Key}]");
                        }

                        continue;
                    }

                    if (value.GetType().Name.IndexOf("LayoutCache", StringComparison.Ordinal) >= 0)
                    {
                        AddGroupsFromCache(found, value, Vector2.zero, contentOffset, "static:" + field.Name);
                    }
                }
            }
            catch (Exception e)
            {
                notes.Add("GUILayoutUtility scan failed: " + e.Message);
            }

            notes.Add("candidates=" + found.Count.ToString(CultureInfo.InvariantCulture));
            return found;
        }

        private static void AddGroupsFromCache(
            List<LayoutCandidate> into, object cache, Vector2 viewOffset, Vector2 contentOffset, string source)
        {
            if (cache == null)
            {
                return;
            }

            foreach (var member in new[] { "topLevel", "windows" })
            {
                var group = ReadMemberByName(cache, member);
                if (group == null)
                {
                    continue;
                }

                // A docked EditorWindow's OnGUI lands under "windows", laid out in the window's own
                // content space; "topLevel" is the host view's space, which also covers the tab strip.
                // Only the former needs the tab strip added back before an injected click can land.
                var offset = member == "windows" ? viewOffset + contentOffset : viewOffset;
                into.Add(new LayoutCandidate { Group = group, Offset = offset, Source = source + "." + member });
            }
        }

        /// <summary>
        /// Secondary reading of the chrome above a window's content, from the host view's own border.
        /// A dock area reports its tab strip as the top border; floating windows report zero. Used as a
        /// fallback when the layout roots are not available to measure the difference directly.
        /// </summary>
        /// <summary>
        /// Desktop rect of the OS window that hosts this EditorWindow. A docked window's own position
        /// is relative to this, so both are needed to place a screen-space capture.
        /// </summary>
        internal static Rect ContainerRect(EditorWindow window)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                var container = parent != null ? ReadMemberByName(parent, "window") : null;
                var position = container != null ? ReadMemberByName(container, "position") : null;
                if (position is Rect)
                {
                    return (Rect)position;
                }
            }
            catch (Exception)
            {
                // Falls back to the origin, which is correct for a maximized editor.
            }

            return new Rect(0f, 0f, 0f, 0f);
        }

        /// <summary>
        /// The host view's rect in desktop points, which is what a screen capture needs. Preferred over
        /// the window's own position because it is the view that actually owns the pixels on screen.
        /// </summary>
        internal static Rect HostScreenRect(EditorWindow window)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                var screen = parent != null ? ReadMemberByName(parent, "screenPosition") : null;
                if (screen is Rect)
                {
                    return (Rect)screen;
                }
            }
            catch (Exception)
            {
                // Caller falls back to EditorWindow.position.
            }

            return new Rect(0f, 0f, 0f, 0f);
        }

        internal static object ContainerOf(EditorWindow window)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                return parent != null ? ReadMemberByName(parent, "window") : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The container window holding the main editor, which is the one screen reads see.</summary>
        internal static object MainContainer()
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                if (type == null)
                {
                    return null;
                }

                foreach (var candidate in Resources.FindObjectsOfTypeAll(type))
                {
                    var showMode = ReadMemberByName(candidate, "showMode");
                    if (showMode == null)
                    {
                        continue;
                    }

                    if (string.Equals(Enum.GetName(showMode.GetType(), showMode), "MainWindow", StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
                // Caller treats a missing main window as "not capturable".
            }

            return null;
        }

        private static float BorderTop(EditorWindow window, List<string> notes)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                if (parent == null)
                {
                    notes.Add("borderTop: no m_Parent");
                    return 0f;
                }

                var border = ReadMemberByName(parent, "borderSize") as RectOffset;
                if (border == null)
                {
                    notes.Add("borderTop: host view has no borderSize");
                    return 0f;
                }

                return border.top;
            }
            catch (Exception e)
            {
                notes.Add("borderTop failed: " + e.Message);
                return 0f;
            }
        }

        /// <summary>
        /// The window the target's host view is currently showing.
        ///
        /// A dock area holds several windows and draws exactly one of them. A background tab keeps a
        /// stale position and owns no pixels, so anything that reads the screen there gets whichever tab
        /// is in front - a Scene request quietly returning the Game view, for instance. Comparing this
        /// against the requested window is what turns that into an error instead of a wrong image.
        /// </summary>
        internal static EditorWindow VisibleViewOf(EditorWindow window)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                return parent != null ? ReadMemberByName(parent, "actualView") as EditorWindow : null;
            }
            catch (Exception)
            {
                // Unknown means "cannot prove it is hidden", and the caller treats that as visible.
                return null;
            }
        }

        /// <summary>
        /// The chrome the host view draws around the window's content: a dock area reports its tab strip
        /// as the top border, a floating window reports zero. Capture uses it to cut the tab strip off,
        /// and drag uses it to turn content-local coordinates into the host-view space SendEvent expects.
        /// </summary>
        internal static RectOffset BorderSize(EditorWindow window, List<string> notes)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                if (parent == null)
                {
                    if (notes != null) notes.Add("borderSize: no m_Parent");
                    return null;
                }

                var border = ReadMemberByName(parent, "borderSize") as RectOffset;
                if (border == null && notes != null)
                {
                    notes.Add("borderSize: host view has no borderSize");
                }

                return border;
            }
            catch (Exception e)
            {
                if (notes != null) notes.Add("borderSize failed: " + e.Message);
                return null;
            }
        }

        private static object FindLayoutCache(VisualElement container)
        {
            foreach (var field in container.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.Name == "m_Cache" || field.FieldType.Name.IndexOf("LayoutCache", StringComparison.Ordinal) >= 0)
                {
                    return field.GetValue(container);
                }
            }

            return null;
        }

        private static void CountEntries(object entry, int depth, ref int count)
        {
            if (entry == null || depth > 32)
            {
                return;
            }

            count++;
            var children = ReadMemberByName(entry, "entries") as IList;
            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                CountEntries(child, depth + 1, ref count);
            }
        }

        private static void WriteLayoutEntry(StringBuilder sb, object entry, int depth, ref int index)
        {
            if (entry == null || depth > 32)
            {
                return;
            }

            var rectValue = ReadMemberByName(entry, "rect");
            var rect = rectValue is Rect ? (Rect)rectValue : new Rect();
            var style = ReadMemberByName(entry, "style") as GUIStyle;
            var children = ReadMemberByName(entry, "entries") as IList;

            if (index > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"i\":").Append(index.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"depth\":").Append(depth.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"style\":\"").Append(Esc(style != null ? style.name : string.Empty)).Append("\",");
            sb.Append("\"kind\":\"").Append(Esc(entry.GetType().Name)).Append("\",");
            sb.Append("\"isGroup\":").Append(children != null ? "true" : "false").Append(',');
            sb.Append("\"x\":").Append(F(rect.x)).Append(',');
            sb.Append("\"y\":").Append(F(rect.y)).Append(',');
            sb.Append("\"w\":").Append(F(rect.width)).Append(',');
            sb.Append("\"h\":").Append(F(rect.height));
            sb.Append('}');
            index++;

            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                WriteLayoutEntry(sb, child, depth + 1, ref index);
            }
        }

        private static Rect ResolveLayoutRect(EditorWindow window, int wantedIndex, out Vector2 offset)
        {
            var notes = new List<string>();
            object topLevel;
            if (!TryGetBestLayout(window, notes, out topLevel, out offset))
            {
                throw new InvalidOperationException(
                    "No IMGUI layout available for this window; click by x/y instead. " + string.Join(" | ", notes));
            }

            var index = 0;
            Rect found;
            if (!TryFindLayoutRect(topLevel, wantedIndex, 0, ref index, out found))
            {
                throw new ArgumentOutOfRangeException(nameof(wantedIndex),
                    $"Layout entry {wantedIndex} not found (window produced {index} entries). Run editor_window_dump first.");
            }

            return found;
        }

        private static bool TryFindLayoutRect(object entry, int wanted, int depth, ref int index, out Rect result)
        {
            result = new Rect();
            if (entry == null || depth > 32)
            {
                return false;
            }

            if (index == wanted)
            {
                var rectValue = ReadMemberByName(entry, "rect");
                result = rectValue is Rect ? (Rect)rectValue : new Rect();
                index++;
                return true;
            }

            index++;

            var children = ReadMemberByName(entry, "entries") as IList;
            if (children == null)
            {
                return false;
            }

            foreach (var child in children)
            {
                if (TryFindLayoutRect(child, wanted, depth + 1, ref index, out result))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<VisualElement> FindImguiContainers(EditorWindow window, List<string> notes)
        {
            // Legacy OnGUI windows draw inside the HostView's container, not the window's own root,
            // so both roots have to be searched before concluding there is no IMGUI surface.
            var roots = new List<VisualElement>();

            try
            {
                if (window.rootVisualElement != null)
                {
                    roots.Add(window.rootVisualElement);
                    notes.Add("root=rootVisualElement");
                }
            }
            catch (Exception e)
            {
                notes.Add("rootVisualElement failed: " + e.Message);
            }

            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField != null ? parentField.GetValue(window) : null;
                if (parent != null)
                {
                    var visualTree = ReadMemberByName(parent, "visualTree") as VisualElement;
                    if (visualTree != null)
                    {
                        roots.Add(visualTree);
                        notes.Add("root=m_Parent.visualTree");
                    }
                    else
                    {
                        notes.Add("m_Parent has no visualTree");
                    }
                }
                else
                {
                    notes.Add("m_Parent null (window not docked yet?)");
                }
            }
            catch (Exception e)
            {
                notes.Add("m_Parent failed: " + e.Message);
            }

            // Collect every candidate rather than stopping at the first: the caller decides which one
            // really ran OnGUI by measuring their layout caches.
            var found = new List<VisualElement>();
            foreach (var root in roots)
            {
                CollectImguiContainers(root, 0, found);
            }

            notes.Add("imguiContainers=" + found.Count.ToString(CultureInfo.InvariantCulture) +
                      " in " + roots.Count.ToString(CultureInfo.InvariantCulture) + " root(s)");
            return found;
        }

        private static void CollectImguiContainers(VisualElement element, int depth, List<VisualElement> into)
        {
            if (element == null || depth > 32)
            {
                return;
            }

            if (element is IMGUIContainer && !into.Contains(element))
            {
                into.Add(element);
            }

            foreach (var child in element.Children())
            {
                CollectImguiContainers(child, depth + 1, into);
            }
        }

        private static string DumpVisualTree(EditorWindow window)
        {
            VisualElement root = null;
            try
            {
                root = window.rootVisualElement;
            }
            catch (Exception)
            {
                return "[]";
            }

            if (root == null)
            {
                return "[]";
            }

            var sb = new StringBuilder("[");
            var index = 0;
            WriteVisualElement(sb, root, 0, ref index);
            sb.Append(']');
            return sb.ToString();
        }

        private static void WriteVisualElement(StringBuilder sb, VisualElement element, int depth, ref int index)
        {
            if (element == null || depth > 16 || index > 500)
            {
                return;
            }

            var bound = element.worldBound;
            var text = element as TextElement;

            if (index > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"i\":").Append(index.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"depth\":").Append(depth.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"type\":\"").Append(Esc(element.GetType().Name)).Append("\",");
            sb.Append("\"name\":\"").Append(Esc(element.name)).Append("\",");
            sb.Append("\"text\":\"").Append(Esc(text != null ? text.text : string.Empty)).Append("\",");
            sb.Append("\"x\":").Append(F(bound.x)).Append(',');
            sb.Append("\"y\":").Append(F(bound.y)).Append(',');
            sb.Append("\"w\":").Append(F(bound.width)).Append(',');
            sb.Append("\"h\":").Append(F(bound.height));
            sb.Append('}');
            index++;

            foreach (var child in element.Children())
            {
                WriteVisualElement(sb, child, depth + 1, ref index);
            }
        }

        // ------------------------------------------------------------------ screenshot

        private static void ScreenshotWindow(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var outputPath = Require(p.outputPath, "outputPath");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            window.Focus();
            RepaintImmediate(window);

            var rect = window.position;
            var width = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            var height = Mathf.Max(1, Mathf.RoundToInt(rect.height));

            var internalUtility = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.InternalEditorUtility");
            var readScreenPixel = internalUtility != null
                ? internalUtility.GetMethod("ReadScreenPixel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                : null;

            if (readScreenPixel == null)
            {
                throw new MissingMethodException("InternalEditorUtility.ReadScreenPixel not available on this Unity version.");
            }

            // Three coordinate spaces meet here and none of them can be assumed:
            //   - EditorWindow.position is relative to its container window, not the desktop, so a
            //     docked window's y can exceed the screen height on its own.
            //   - ReadScreenPixel works bottom-left, while every Unity editor rect is top-left.
            //   - The editor draws in points; ReadScreenPixel reads physical pixels.
            // So the desktop rect is rebuilt from the container, flipped, and scaled by pixelsPerPoint,
            // and every input is reported below so a wrong capture can be diagnosed from the response
            // instead of by staring at the image.
            // Measured on 2022.3: the host view's screenPosition and the window's own position both
            // come back in desktop points already, so they are used as-is. The container rect is
            // reported alongside for diagnosis but must not be added in - doing so doubles the origin.
            var containerRect = ContainerRect(window);
            var hostRect = HostScreenRect(window);
            var source = hostRect.width > 0f ? hostRect : rect;

            var targetContainer = ContainerOf(window);
            var mainContainer = MainContainer();
            var mainRectValue = mainContainer != null ? ReadMemberByName(mainContainer, "position") : null;
            var mainRect = mainRectValue is Rect ? (Rect)mainRectValue : new Rect();
            var inMainWindow = targetContainer != null && mainContainer != null && ReferenceEquals(targetContainer, mainContainer);

            var scale = EditorGUIUtility.pixelsPerPoint;
            if (scale <= 0f) scale = 1f;

            response.AddOutput("screenResolution", Screen.currentResolution.width + "x" + Screen.currentResolution.height);
            response.AddOutput("pixelsPerPoint", F(scale));
            response.AddOutput("containerRect", RectJson(containerRect));
            response.AddOutput("mainWindowRect", RectJson(mainRect));
            response.AddOutput("windowRect", RectJson(rect));
            response.AddOutput("hostRect", RectJson(source));
            response.AddOutput("inMainWindow", inMainWindow ? "true" : "false");

            // ReadScreenPixel reads the *main editor window's* framebuffer, not the desktop. A window
            // living in its own floating container simply is not in those pixels, and capturing anyway
            // writes a plausible-looking but blank PNG. Refuse instead, and say what to do about it.
            if (!inMainWindow && p.originY == 0f)
            {
                throw new InvalidOperationException(
                    "This window is in a floating container, which ReadScreenPixel cannot see - it only reads the " +
                    "main editor window's framebuffer. Dock the window into the main editor window and retry, or use " +
                    "editor_window_dump to read its state instead.");
            }

            // Coordinates are relative to the main window, bottom-left, in physical pixels: the editor
            // reports rects top-left in points, so flip against the main window and scale.
            var localX = source.x - mainRect.x;
            var localY = source.y - mainRect.y;
            var flippedY = mainRect.height - (localY + source.height);

            var originPoint = new Vector2(
                p.originX != 0f ? p.originX : localX,
                p.originY != 0f ? p.originY : (p.noFlipY ? localY : flippedY));

            var origin = originPoint * scale;
            width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            var pixels = readScreenPixel.Invoke(null, new object[] { origin, width, height }) as Color[];
            if (pixels == null || pixels.Length == 0)
            {
                throw new InvalidOperationException("ReadScreenPixel returned no pixels (window off-screen or minimized?).");
            }

            // A flat single-colour result means the region held nothing, which is worth saying out loud:
            // the file would otherwise look like a successful capture.
            var uniform = true;
            for (var i = 1; i < pixels.Length && uniform; i++)
            {
                if (pixels[i] != pixels[0])
                {
                    uniform = false;
                }
            }

            response.AddOutput("uniformColor", uniform ? "true" : "false");
            if (uniform)
            {
                response.logs.Add("[ProjectMQaMcp] capture is a single flat colour - the window may be occluded, " +
                                  "minimized, or outside the main editor window.");
            }

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply();
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            var info = new FileInfo(outputPath);
            response.AddOutput("outputPath", outputPath);
            response.AddOutput("pngBytes", info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0");
            response.AddOutput("width", width.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("height", height.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("screenOrigin", F(origin.x) + "," + F(origin.y));
        }

        // ------------------------------------------------------------------ per-window pixel capture

        /// <summary>
        /// Captures only the target EditorWindow's pixels, docked or floating, without touching its
        /// position, size, docking or focus. See EditorWindowCapture for how the pixels are obtained.
        /// </summary>
        private static void CaptureWindow(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var outputPath = Require(p.outputPath, "outputPath");

            DescribeTarget(window, response);

            var result = EditorWindowCapture.Capture(window, outputPath, CaptureOptionsFrom(p));
            EditorWindowCapture.WriteOutputs(result, response, string.Empty);

            if (!result.success)
            {
                // Never let an empty or missing image pass as a success: the caller would treat a blank
                // PNG as evidence the window rendered.
                throw new InvalidOperationException(DescribeCaptureFailure(window, result));
            }
        }

        private static EditorWindowCapture.Options CaptureOptionsFrom(CommandParameters p)
        {
            return new EditorWindowCapture.Options
            {
                backend = string.IsNullOrEmpty(p.captureBackend) ? "auto" : p.captureBackend,
                includeChrome = p.includeChrome,
                settleMs = p.captureSettleMs > 0 ? p.captureSettleMs : 24,
                allowUniform = p.allowUniform,
            };
        }

        private static void DescribeTarget(EditorWindow window, CommandResponse response)
        {
            response.AddOutput("targetWindowType", window.GetType().FullName);
            response.AddOutput("targetWindowTitle", window.titleContent != null ? window.titleContent.text : string.Empty);
            response.AddOutput("targetInstanceId", window.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        }

        private static string DescribeCaptureFailure(EditorWindow window, EditorWindowCapture.Result result)
        {
            var sb = new StringBuilder();
            sb.Append("Capture failed for ").Append(window.GetType().FullName);
            if (window.titleContent != null && !string.IsNullOrEmpty(window.titleContent.text))
            {
                sb.Append(" ('").Append(window.titleContent.text).Append("')");
            }

            sb.Append(" instanceId=").Append(window.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            sb.Append(", hostRect=").Append(RectJson(result.hostRect));
            sb.Append(", inMainWindow=").Append(result.inMainWindow ? "true" : "false");
            sb.Append(". Reason: ").Append(result.failureReason ?? "unknown");

            if (result.attempts.Count > 0)
            {
                sb.Append(" Backends tried: ").Append(string.Join(" | ", result.attempts.ToArray()));
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ drag

        private static void Drag(CommandParameters p, CommandResponse response)
        {
            RunDrag(p, response, false);
        }

        private static void DragCapture(CommandParameters p, CommandResponse response)
        {
            RunDrag(p, response, true);
        }

        /// <summary>
        /// Presses, moves and releases the mouse inside one EditorWindow, optionally saving the window's
        /// pixels at every stage of the gesture.
        ///
        /// Two details decide whether a drag is actually recognised rather than looking like a click.
        /// First, the moves are MouseDrag events, not MouseMove: Unity only emits MouseMove when no
        /// button is held, so IMGUI's drag handling and UI Toolkit's MouseMoveEvent-with-pressed-button
        /// path both key off MouseDrag. Second, every move carries the delta since the previous point -
        /// IMGUI code reads Event.current.delta rather than recomputing from mousePosition, and
        /// GraphView's SelectionDragger moves nodes by exactly that delta, so a zero delta drags nothing.
        ///
        /// Coordinates are content-local by default. The host view's border - the dock tab strip on a
        /// docked window, nothing on a floating one - is added automatically, so the same coordinates
        /// work before and after the user docks the window.
        /// </summary>
        private static void RunDrag(CommandParameters p, CommandResponse response, bool capture)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            var steps = p.moveStepCount > 0 ? p.moveStepCount : 12;
            steps = Mathf.Clamp(steps, 1, 240);
            var durationMs = p.durationMs > 0 ? p.durationMs : 240;
            var stepDelayMs = Mathf.Clamp(durationMs / steps, 0, 2000);

            var space = string.IsNullOrEmpty(p.coordinateSpace)
                ? "content"
                : p.coordinateSpace.Trim().ToLowerInvariant();

            var offset = Vector2.zero;
            if (space == "content")
            {
                var border = BorderSize(window, notes);
                if (border != null)
                {
                    offset = new Vector2(border.left, border.top);
                }
            }
            else if (space != "host")
            {
                throw new ArgumentException("coordinateSpace must be 'content' (default) or 'host'.");
            }

            var from = new Vector2(p.fromX, p.fromY) + offset;
            var to = new Vector2(p.toX, p.toY) + offset;
            var modifiers = ParseModifiers(p.modifiers);
            var button = p.button;

            string framesDir = null;
            var captureEveryMove = true;
            EditorWindowCapture.Options captureOptions = null;
            if (capture)
            {
                framesDir = Require(p.framesDir, "framesDir");
                Directory.CreateDirectory(framesDir);
                // Tri-state on purpose: JsonUtility cannot express "absent" for a bool, and the useful
                // default here is true, so the parameter travels as text.
                captureEveryMove = string.IsNullOrEmpty(p.captureEveryMove) || ParseBool(p.captureEveryMove);
                captureOptions = CaptureOptionsFrom(p);
            }

            if (!p.noFocus)
            {
                // A drag needs the window to own the mouse; focusing is part of the gesture, unlike the
                // capture command which must leave window order untouched.
                window.Focus();
            }

            RepaintImmediate(window);

            var events = new StringBuilder("[");
            var frames = new StringBuilder("[");
            var eventCount = 0;
            var moveCount = 0;
            var frameIndex = 0;
            var frameOk = 0;
            var frameFailed = 0;

            var down = new Event
            {
                type = EventType.MouseDown,
                mousePosition = from,
                button = button,
                clickCount = 1,
                modifiers = modifiers,
                delta = Vector2.zero,
            };
            var downSent = SendEvent(window, down);
            eventCount++;
            AppendDragEvent(events, eventCount - 1, "down", from, offset, Vector2.zero, downSent);
            RepaintImmediate(window);
            if (capture)
            {
                AppendDragFrame(frames, window, framesDir, ref frameIndex, "down", from, captureOptions,
                    ref frameOk, ref frameFailed);
            }

            var previous = from;
            for (var step = 1; step <= steps; step++)
            {
                var t = (float)step / steps;
                var point = Vector2.Lerp(from, to, t);
                var delta = point - previous;

                if (stepDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(stepDelayMs);
                }

                var move = new Event
                {
                    type = EventType.MouseDrag,
                    mousePosition = point,
                    button = button,
                    clickCount = 0,
                    modifiers = modifiers,
                    delta = delta,
                };
                var moveSent = SendEvent(window, move);
                eventCount++;
                moveCount++;
                AppendDragEvent(events, eventCount - 1, "move", point, offset, delta, moveSent);

                RepaintImmediate(window);
                if (capture && captureEveryMove)
                {
                    AppendDragFrame(frames, window, framesDir, ref frameIndex, "move", point, captureOptions,
                        ref frameOk, ref frameFailed);
                }

                previous = point;
            }

            var up = new Event
            {
                type = EventType.MouseUp,
                mousePosition = to,
                button = button,
                clickCount = 1,
                modifiers = modifiers,
                delta = Vector2.zero,
            };
            var upSent = SendEvent(window, up);
            eventCount++;
            AppendDragEvent(events, eventCount - 1, "up", to, offset, Vector2.zero, upSent);

            window.Repaint();
            RepaintImmediate(window);
            if (capture)
            {
                AppendDragFrame(frames, window, framesDir, ref frameIndex, "up", to, captureOptions,
                    ref frameOk, ref frameFailed);
            }

            events.Append(']');
            frames.Append(']');

            DescribeTarget(window, response);
            response.AddOutput("coordinateSpace", space);
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            response.AddOutput("from", F(from.x) + "," + F(from.y));
            response.AddOutput("to", F(to.x) + "," + F(to.y));
            response.AddOutput("moveStepCount", steps.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("durationMs", durationMs.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("stepDelayMs", stepDelayMs.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("eventCount", eventCount.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("moveCount", moveCount.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("events", events.ToString());
            // SendEvent's return value says the event was consumed, not that the tool reacted, so it is
            // reported per event rather than folded into a single pass/fail.
            response.AddOutput("mouseDownReturned", downSent ? "true" : "false");
            response.AddOutput("mouseUpReturned", upSent ? "true" : "false");

            if (notes.Count > 0)
            {
                response.AddOutput("dragNotes", string.Join(" | ", notes.ToArray()));
            }

            if (capture)
            {
                response.AddOutput("framesDir", framesDir);
                response.AddOutput("captureEveryMove", captureEveryMove ? "true" : "false");
                response.AddOutput("frameCount", frameIndex.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("framesCaptured", frameOk.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("framesFailed", frameFailed.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("frames", frames.ToString());

                if (frameOk == 0)
                {
                    throw new InvalidOperationException(
                        "The drag ran but no frame could be captured, so there is no rendering evidence. " +
                        "First failure: " + FirstFrameError(frames.ToString()));
                }
            }
        }

        /// <summary>
        /// Runs a real editor drag and drop, the kind that moves an item between two panes of a tool
        /// window, and saves a PNG at each stage.
        ///
        /// unity_editor_drag alone cannot do this. A tool that starts a drag calls DragAndDrop.StartDrag,
        /// which hands the gesture to the editor's own drag session; from that point the receiving side
        /// waits for DragUpdated and DragPerform, and no amount of further MouseDrag produces them.
        /// Measured on a tool whose drop target never lit up and whose item never moved, while the same
        /// coordinates driven by a real mouse worked. So the press and the first moves are sent as a
        /// normal drag to make the source call StartDrag, and then DragUpdated / DragPerform / DragExited
        /// are sent at the destination to finish the session.
        ///
        /// DragUpdated is sent twice with a pause between: the first one lets the receiver set
        /// DragAndDrop.visualMode and paint its highlight, and the pause is what makes that highlight
        /// capturable - a mid-gesture frame is usually the only picture worth having.
        /// </summary>
        private static void DragDrop(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            var space = string.IsNullOrEmpty(p.coordinateSpace)
                ? "content"
                : p.coordinateSpace.Trim().ToLowerInvariant();

            var offset = Vector2.zero;
            if (space == "content")
            {
                var border = BorderSize(window, notes);
                if (border != null)
                {
                    offset = new Vector2(border.left, border.top);
                }
            }
            else if (space != "host")
            {
                throw new ArgumentException("coordinateSpace must be 'content' (default) or 'host'.");
            }

            var from = new Vector2(p.fromX, p.fromY) + offset;
            var to = new Vector2(p.toX, p.toY) + offset;
            var modifiers = ParseModifiers(p.modifiers);
            var hoverMs = Mathf.Clamp(p.hoverMs > 0 ? p.hoverMs : 400, 0, 10000);
            var drop = string.IsNullOrEmpty(p.performDrop) || ParseBool(p.performDrop);
            var panelEvents = string.IsNullOrEmpty(p.panelEvents) || ParseBool(p.panelEvents);

            string framesDir = null;
            EditorWindowCapture.Options captureOptions = null;
            if (!string.IsNullOrEmpty(p.framesDir))
            {
                framesDir = p.framesDir;
                Directory.CreateDirectory(framesDir);
                captureOptions = CaptureOptionsFrom(p);
            }

            if (!p.noFocus)
            {
                window.Focus();
            }

            RepaintImmediate(window);

            var events = new StringBuilder("[");
            var frames = new StringBuilder("[");
            var eventCount = 0;
            var frameIndex = 0;
            var frameOk = 0;
            var frameFailed = 0;

            Action<string, Vector2, Vector2, bool> record = (phase, point, delta, sent) =>
            {
                AppendDragEvent(events, eventCount, phase, point, offset, delta, sent);
                eventCount++;
                RepaintImmediate(window);

                if (framesDir != null)
                {
                    AppendDragFrame(frames, window, framesDir, ref frameIndex, phase, point, captureOptions,
                        ref frameOk, ref frameFailed);
                }
            };

            var downSent = SendEvent(window, new Event
            {
                type = EventType.MouseDown,
                mousePosition = from,
                button = 0,
                clickCount = 1,
                modifiers = modifiers,
                delta = Vector2.zero,
            });
            record("down", from, Vector2.zero, downSent);

            // Short moves that stay on the source. A tool arms its drag on the first move that clears its
            // own threshold, so these have to be real moves with a real delta, not one jump to the target.
            var previous = from;
            foreach (var nudge in new[] { 4f, 10f, 18f, 28f })
            {
                var point = from + new Vector2(nudge, 0f);
                var sent = SendEvent(window, new Event
                {
                    type = EventType.MouseDrag,
                    mousePosition = point,
                    button = 0,
                    clickCount = 0,
                    modifiers = modifiers,
                    delta = point - previous,
                });
                record("arm", point, point - previous, sent);
                previous = point;
            }

            // Did the source actually start a drag? A receiver reads DragAndDrop.GetGenericData(key),
            // so an empty slot here means the press and moves never reached the source's own handler and
            // nothing downstream can work. Reported rather than thrown: the caller needs to see which
            // half broke, and the key is only known to the caller.
            var armedData = string.IsNullOrEmpty(p.genericDataKey)
                ? null
                : DragAndDrop.GetGenericData(p.genericDataKey);

            // Standing in for a source that did not arm, so the receiver still has something to resolve.
            // Only reaches for it when the caller named a key and the slot came back empty - a drag the
            // source did arm must keep its own payload, which is the live object the tool will mutate.
            var injected = false;
            if (armedData == null && !string.IsNullOrEmpty(p.genericDataKey) && !string.IsNullOrEmpty(p.genericDataJson))
            {
                injected = InjectGenericData(p.genericDataKey, p.genericDataType, p.genericDataJson);
                armedData = DragAndDrop.GetGenericData(p.genericDataKey);
            }

            // Carried across the calls below: DragUpdated finds who takes the drag, DragPerform drops on
            // that one alone. Null after a hover nobody took, which is what "the drop went nowhere" means.
            VisualElement handler = null;

            var updatedSent = SendDragEvent(window, EventType.DragUpdated, to, to - previous, modifiers,
                panelEvents, ref handler);
            record("dragUpdated", to, to - previous, updatedSent);

            if (hoverMs > 0)
            {
                System.Threading.Thread.Sleep(hoverMs);
            }

            // Second one after the pause: this is the frame where the highlight is on screen.
            var hoverSent = SendDragEvent(window, EventType.DragUpdated, to, Vector2.zero, modifiers,
                panelEvents, ref handler);
            record("hover", to, Vector2.zero, hoverSent);

            var hoverHandler = DescribeElement(handler);

            var performSent = false;
            if (drop)
            {
                performSent = SendDragEvent(window, EventType.DragPerform, to, Vector2.zero, modifiers,
                    panelEvents, ref handler);
                record("dragPerform", to, Vector2.zero, performSent);
            }

            // Always close the session, dropped or not - a tool that skipped DragExited would keep its
            // highlight lit and refuse the next drag. This one goes to every ancestor: clearing a
            // highlight has no side effect, and one lit further up would otherwise stay on.
            VisualElement exitHandler = null;
            var exitedSent = SendDragEvent(window, EventType.DragExited, to, Vector2.zero, modifiers,
                panelEvents, ref exitHandler);
            record("dragExited", to, Vector2.zero, exitedSent);

            var upSent = SendEvent(window, new Event
            {
                type = EventType.MouseUp,
                mousePosition = to,
                button = 0,
                clickCount = 1,
                modifiers = modifiers,
                delta = Vector2.zero,
            });
            record("up", to, Vector2.zero, upSent);

            window.Repaint();
            RepaintImmediate(window);

            events.Append(']');
            frames.Append(']');

            DescribeTarget(window, response);
            response.AddOutput("coordinateSpace", space);
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            response.AddOutput("from", F(from.x) + "," + F(from.y));
            response.AddOutput("to", F(to.x) + "," + F(to.y));
            response.AddOutput("hoverMs", hoverMs.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("performDrop", drop ? "true" : "false");
            response.AddOutput("eventCount", eventCount.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("events", events.ToString());
            response.AddOutput("panelEvents", panelEvents ? "true" : "false");
            response.AddOutput("dragUpdatedReturned", updatedSent ? "true" : "false");

            // Empty means the hover reached no drop target, so a drop here was never going to land -
            // the difference between "the receiver refused it" and "nothing was listening".
            response.AddOutput("hoverHandler", hoverHandler);

            if (!string.IsNullOrEmpty(p.genericDataKey))
            {
                // The single most useful line when a drop does nothing: it says whether the source armed
                // the drag at all, which decides whether to fix the press or the receiver.
                response.AddOutput("genericDataKey", p.genericDataKey);
                response.AddOutput("genericDataAfterArm",
                    armedData == null ? "null" : armedData.GetType().FullName);
                response.AddOutput("genericDataInjected", injected ? "true" : "false");
            }
            response.AddOutput("dragPerformReturned", performSent ? "true" : "false");

            if (notes.Count > 0)
            {
                response.AddOutput("dragNotes", string.Join(" | ", notes.ToArray()));
            }

            if (framesDir != null)
            {
                response.AddOutput("framesDir", framesDir);
                response.AddOutput("frameCount", frameIndex.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("framesCaptured", frameOk.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("framesFailed", frameFailed.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("frames", frames.ToString());

                if (frameOk == 0)
                {
                    throw new InvalidOperationException(
                        "The drag ran but no frame could be captured, so there is no rendering evidence. " +
                        "First failure: " + FirstFrameError(frames.ToString()));
                }
            }
        }

        /// <summary>How far up the ancestor chain a drag event is offered before giving up.</summary>
        private const int AncestorWalkLimit = 24;

        /// <summary>
        /// Sends one drag event, and when panelEvents is on also offers it to the UI Toolkit elements
        /// under the point.
        ///
        /// EditorWindow.SendEvent alone is not enough for a UI Toolkit receiver. The IMGUI event goes
        /// down the IMGUI path, and a DragUpdatedEvent callback registered on a VisualElement never sees
        /// it - measured on a drop target whose highlight stayed off while the same gesture from a real
        /// mouse lit it. Both are sent because a window can mix IMGUI and UI Toolkit, and a receiver that
        /// already handled the IMGUI one simply ignores the second.
        ///
        /// Picking one element is not enough either. Pick returns the deepest thing under the point - a
        /// label, inside a row, inside a scroll view - while the drop target is usually registered on the
        /// container above it, and the converted event does not travel up there on its own. Measured on a
        /// hold column: the highlight stayed off when the event went to the picked label and came on when
        /// the same event went to that label's container five levels up. So the event is offered to each
        /// ancestor in turn.
        ///
        /// handler carries the element that took the drag from one call to the next. DragUpdated finds it;
        /// DragPerform then goes to that element alone, because a receiver that accepts a drop also applies
        /// it, and walking past it would apply the same drop again on every ancestor that accepts.
        /// </summary>
        private static bool SendDragEvent(EditorWindow window, EventType type, Vector2 point, Vector2 delta,
            EventModifiers modifiers, bool panelEvents, ref VisualElement handler)
        {
            var imgui = new Event
            {
                type = type,
                mousePosition = point,
                modifiers = modifiers,
                delta = delta,
            };

            var sent = SendEvent(window, imgui);

            if (!panelEvents)
            {
                return sent;
            }

            var root = window.rootVisualElement;
            if (root == null || root.panel == null)
            {
                return sent;
            }

            // The panel measures from the window's content corner, so the tab strip offset that the
            // IMGUI coordinates carry has to come back off before picking.
            var border = BorderSize(window, null);
            var panelPoint = border == null
                ? point
                : point - new Vector2(border.left, border.top);

            // A drop goes only to whoever took the hover. Everything else starts at the point.
            var element = type == EventType.DragPerform && handler != null
                ? handler
                : root.panel.Pick(panelPoint) ?? root;

            var visited = 0;

            while (element != null && visited < AncestorWalkLimit)
            {
                // A receiver that takes the drag sets the visual mode, so clearing it first turns
                // "did anyone take it" into something readable - the event itself reports nothing.
                var previousMode = DragAndDrop.visualMode;
                if (type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.None;
                }

                if (!SendConverted(element, type, imgui))
                {
                    return sent;
                }

                if (type == EventType.DragUpdated)
                {
                    if (DragAndDrop.visualMode != DragAndDropVisualMode.None)
                    {
                        handler = element;
                        return sent;
                    }

                    DragAndDrop.visualMode = previousMode;
                    handler = null;
                }
                else if (type == EventType.DragPerform)
                {
                    // The drop was aimed at one element; do not let it land twice.
                    return sent;
                }

                element = element.parent;
                visited++;
            }

            return sent;
        }

        /// <summary>Converts one IMGUI drag event and hands it to a single element.</summary>
        /// <returns>False when the event type has no UI Toolkit counterpart to send.</returns>
        private static bool SendConverted(VisualElement element, EventType type, Event imgui)
        {
            EventBase converted;
            switch (type)
            {
                case EventType.DragUpdated:
                    converted = DragUpdatedEvent.GetPooled(imgui);
                    break;

                case EventType.DragPerform:
                    converted = DragPerformEvent.GetPooled(imgui);
                    break;

                case EventType.DragExited:
                    converted = DragExitedEvent.GetPooled(imgui);
                    break;

                default:
                    return false;
            }

            using (converted)
            {
                converted.target = element;
                element.SendEvent(converted);
            }

            return true;
        }

        /// <summary>Names an element for the response, so a failed drop says where it stopped.</summary>
        private static string DescribeElement(VisualElement element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            return string.IsNullOrEmpty(element.name)
                ? element.GetType().Name
                : $"{element.GetType().Name}#{element.name}";
        }

        /// <summary>
        /// Puts a payload into the drag session for a source that did not arm one.
        /// </summary>
        /// <remarks>
        /// Deserialising the JSON makes a <b>new</b> object. That is fine for lighting a highlight, which
        /// only reads the payload, but a tool that mutates the dragged item will mutate this copy and
        /// leave its own model untouched. So this is a diagnostic and screenshot aid, not a way to
        /// perform real edits - for those the source has to start the drag itself.
        /// </remarks>
        private static bool InjectGenericData(string key, string typeName, string json)
        {
            try
            {
                object value = json;

                if (!string.IsNullOrEmpty(typeName))
                {
                    var type = ResolveType(typeName);
                    if (type == null)
                    {
                        return false;
                    }

                    value = JsonUtility.FromJson(json, type);
                    if (value == null)
                    {
                        return false;
                    }
                }

                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new UnityEngine.Object[0];
                DragAndDrop.SetGenericData(key, value);
                return true;
            }
            catch (Exception)
            {
                // A bad type name or malformed JSON must not take the whole gesture down - the response
                // says the injection did not happen and the caller can see why from the empty slot.
                return false;
            }
        }

        /// <summary>Finds a type by full name across the loaded assemblies.</summary>
        private static Type ResolveType(string typeName)
        {
            var direct = Type.GetType(typeName, false);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = assembly.GetType(typeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void AppendDragEvent(StringBuilder sb, int index, string phase, Vector2 point,
            Vector2 offset, Vector2 delta, bool sent)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append("{\"i\":").Append(index.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"phase\":\"").Append(phase).Append('"');
            sb.Append(",\"x\":").Append(F(point.x));
            sb.Append(",\"y\":").Append(F(point.y));
            sb.Append(",\"contentX\":").Append(F(point.x - offset.x));
            sb.Append(",\"contentY\":").Append(F(point.y - offset.y));
            sb.Append(",\"deltaX\":").Append(F(delta.x));
            sb.Append(",\"deltaY\":").Append(F(delta.y));
            sb.Append(",\"sendEventReturned\":").Append(sent ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendDragFrame(StringBuilder sb, EditorWindow window, string framesDir,
            ref int frameIndex, string phase, Vector2 point, EditorWindowCapture.Options options,
            ref int ok, ref int failed)
        {
            var path = Path.Combine(framesDir,
                string.Format(CultureInfo.InvariantCulture, "drag_{0:D3}_{1}.png", frameIndex, phase));

            EditorWindowCapture.Result result;
            string error = null;
            try
            {
                result = EditorWindowCapture.Capture(window, path, options);
                if (!result.success)
                {
                    error = result.failureReason ?? "unknown";
                }
            }
            catch (Exception e)
            {
                result = new EditorWindowCapture.Result { outputPath = path };
                error = e.Message;
            }

            if (sb.Length > 1) sb.Append(',');
            sb.Append("{\"i\":").Append(frameIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"phase\":\"").Append(phase).Append('"');
            sb.Append(",\"x\":").Append(F(point.x));
            sb.Append(",\"y\":").Append(F(point.y));
            sb.Append(",\"path\":\"").Append(Esc(path)).Append('"');
            sb.Append(",\"success\":").Append(result.success ? "true" : "false");
            sb.Append(",\"exists\":").Append(result.pngExists ? "true" : "false");
            sb.Append(",\"bytes\":").Append(result.pngBytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"width\":").Append(result.imageWidth.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":").Append(result.imageHeight.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"backend\":\"").Append(Esc(result.backend)).Append('"');
            sb.Append(",\"uniformColor\":").Append(result.uniformColor ? "true" : "false");
            if (error != null)
            {
                sb.Append(",\"error\":\"").Append(Esc(error)).Append('"');
            }
            sb.Append('}');

            if (result.success) ok++; else failed++;
            frameIndex++;
        }

        private static string FirstFrameError(string framesJson)
        {
            var marker = "\"error\":\"";
            var start = framesJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "no error recorded";

            start += marker.Length;
            var end = framesJson.IndexOf('"', start);
            return end > start ? framesJson.Substring(start, end - start) : "no error recorded";
        }

        // ------------------------------------------------------------------ hover (button-less move)

        /// <summary>
        /// Where the pointer was left in each window, so the next move can carry a real delta.
        ///
        /// Keyed per window on purpose: two windows have two independent cursors as far as their hover
        /// handling is concerned, and a shared last-position would make one window's delta depend on
        /// whichever window happened to be moved over previously.
        /// </summary>
        private static readonly Dictionary<int, Vector2> PointerPositions = new Dictionary<int, Vector2>();

        /// <summary>
        /// Moves the pointer inside one EditorWindow with no button held.
        ///
        /// This is what makes hover testable from outside the editor. editor_drag sends MouseDrag, and
        /// UI Toolkit turns that into a MouseMoveEvent whose pressedButtons is non-zero, because the
        /// MouseDown that opened the gesture registered the press in PointerDeviceState. Anything that
        /// keys off "the cursor is over me and nothing is pressed" - GraphView highlighting the edge
        /// under the mouse, for instance - therefore never runs during a drag. A bare MouseMove leaves
        /// pressedButtons at 0 and takes that path instead.
        ///
        /// No MouseDown, MouseDrag or MouseUp is sent, so this cannot move a node, change the selection,
        /// start a marquee, pan the view or open a context menu: every one of those needs a press.
        ///
        /// Coordinates follow editor_drag exactly - content-local by default with the dock tab strip
        /// added automatically, or raw host-view space (what editor_click uses) via coordinateSpace
        /// "host". The command adds no convention of its own.
        /// </summary>
        private static void Move(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            var byElement = string.Equals(p.targetMode, "element", StringComparison.OrdinalIgnoreCase);

            if (!byElement && (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y)))
            {
                throw new ArgumentException(
                    $"x and y must be finite numbers for editor_move (got x={F(p.x)}, y={F(p.y)}).");
            }

            var space = byElement
                ? "host"
                : string.IsNullOrEmpty(p.coordinateSpace)
                    ? "content"
                    : p.coordinateSpace.Trim().ToLowerInvariant();
            if (space != "content" && space != "host")
            {
                throw new ArgumentException("coordinateSpace must be 'content' (default) or 'host'.");
            }

            var border = BorderSize(window, notes);
            var offset = border != null ? new Vector2(border.left, border.top) : Vector2.zero;

            // Focus and a layout pass first, for the same reason click does it: hover targets - and any
            // element rect this is about to aim at - are resolved from the last layout.
            window.Focus();
            RepaintImmediate(window);

            // Everything below works in host-view space, because that is what SendEvent delivers into.
            var point = byElement
                ? ResolveElementPoint(window, p, response)
                : space == "content" ? new Vector2(p.x, p.y) + offset : new Vector2(p.x, p.y);
            var contentPoint = point - offset;

            var size = window.position.size;
            if (!byElement
                && (contentPoint.x < 0f || contentPoint.y < 0f || contentPoint.x > size.x || contentPoint.y > size.y))
            {
                // A point outside the window would hover nothing at all, and reporting that as a
                // successful move would look exactly like a hover the tool ignored.
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "Point is outside {0}: content coordinates ({1}, {2}) are not within 0,0 - {3},{4}. " +
                    "Content space adds the host border ({5}, {6}); pass coordinateSpace 'host' to send raw host-view coordinates.",
                    window.GetType().FullName, F(contentPoint.x), F(contentPoint.y), F(size.x), F(size.y),
                    F(offset.x), F(offset.y)));
            }

            var modifiers = ParseModifiers(p.modifiers);

            var id = window.GetInstanceID();
            PrunePointerPositions();
            Vector2 previous;
            var hadPrevious = PointerPositions.TryGetValue(id, out previous);
            // First move into a window has nowhere to come from, so it carries no delta rather than a
            // fabricated one.
            var delta = hadPrevious ? point - previous : Vector2.zero;

            var move = new Event
            {
                type = EventType.MouseMove,
                mousePosition = point,
                // button/clickCount are meaningless without a press. What decides pressedButtons on the
                // receiving side is that no MouseDown was ever sent, not these fields.
                button = 0,
                clickCount = 0,
                modifiers = modifiers,
                delta = delta,
            };

            // IMGUI only ever sees MouseMove in a window that asked for it - that is Unity's rule for a
            // real mouse too, and an IMGUI tool that never set the flag would silently receive nothing
            // here. The flag is turned on for the send and put straight back, so the window keeps the
            // setting it had; pass ensureWantsMouseMove false to send exactly what production would see.
            var ensureWantsMouseMove = string.IsNullOrEmpty(p.ensureWantsMouseMove)
                || ParseBool(p.ensureWantsMouseMove);
            var hadWantsMouseMove = window.wantsMouseMove;
            var enabledForSend = ensureWantsMouseMove && !hadWantsMouseMove;

            bool sent;
            if (enabledForSend) window.wantsMouseMove = true;
            try
            {
                sent = SendEvent(window, move);
            }
            finally
            {
                if (enabledForSend) window.wantsMouseMove = hadWantsMouseMove;
            }

            PointerPositions[id] = point;

            window.Repaint();
            RepaintImmediate(window);

            var events = new StringBuilder("[");
            AppendMoveEvent(events, point, offset, delta, sent);
            events.Append(']');

            DescribeTarget(window, response);
            if (!byElement)
            {
                // Element mode reports resolvedFrom itself, along with what it matched.
                response.AddOutput("resolvedFrom", "point");
            }

            response.AddOutput("coordinateSpace", space);
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            response.AddOutput("x", F(point.x));
            response.AddOutput("y", F(point.y));
            response.AddOutput("contentX", F(contentPoint.x));
            response.AddOutput("contentY", F(contentPoint.y));
            response.AddOutput("deltaX", F(delta.x));
            response.AddOutput("deltaY", F(delta.y));
            response.AddOutput("hadPreviousPoint", hadPrevious ? "true" : "false");
            response.AddOutput("previousX", hadPrevious ? F(previous.x) : string.Empty);
            response.AddOutput("previousY", hadPrevious ? F(previous.y) : string.Empty);
            response.AddOutput("eventType", "MouseMove");
            response.AddOutput("eventsSent", "1");
            response.AddOutput("pressedButtons", "0");
            response.AddOutput("modifiers", modifiers.ToString());
            response.AddOutput("wantsMouseMove", hadWantsMouseMove ? "true" : "false");
            response.AddOutput("wantsMouseMoveEnabledForSend", enabledForSend ? "true" : "false");
            response.AddOutput("events", events.ToString());
            response.AddOutput("sendEventReturned", sent ? "true" : "false");
            // Same rule as click and drag: the return value says the event was consumed somewhere, not
            // that the window's hover handling ran.
            response.AddOutput("note",
                "sendEventReturned is the raw return of EditorWindow.SendEvent, not proof the UI reacted. " +
                "Confirm a hover with editor_window_capture right after this call, or by reading the tool's own state with editor_get_field.");

            if (notes.Count > 0)
            {
                response.AddOutput("moveNotes", string.Join(" | ", notes.ToArray()));
            }
        }

        private static void AppendMoveEvent(StringBuilder sb, Vector2 point, Vector2 offset, Vector2 delta, bool sent)
        {
            sb.Append("{\"i\":0");
            sb.Append(",\"phase\":\"move\"");
            sb.Append(",\"type\":\"MouseMove\"");
            sb.Append(",\"x\":").Append(F(point.x));
            sb.Append(",\"y\":").Append(F(point.y));
            sb.Append(",\"contentX\":").Append(F(point.x - offset.x));
            sb.Append(",\"contentY\":").Append(F(point.y - offset.y));
            sb.Append(",\"deltaX\":").Append(F(delta.x));
            sb.Append(",\"deltaY\":").Append(F(delta.y));
            sb.Append(",\"pressedButtons\":0");
            sb.Append(",\"sendEventReturned\":").Append(sent ? "true" : "false");
            sb.Append('}');
        }

        /// <summary>Drops remembered positions for windows that have since been closed.</summary>
        private static void PrunePointerPositions()
        {
            if (PointerPositions.Count == 0)
            {
                return;
            }

            var live = new HashSet<int>(AllWindows().Select(x => x.GetInstanceID()));
            var dead = PointerPositions.Keys.Where(x => !live.Contains(x)).ToList();
            foreach (var id in dead)
            {
                PointerPositions.Remove(id);
            }
        }

        /// <summary>Forgets the remembered pointer position, so the next move starts a fresh path.</summary>
        internal static void ForgetPointerPosition(EditorWindow window)
        {
            if (window != null)
            {
                PointerPositions.Remove(window.GetInstanceID());
            }
        }

        // ------------------------------------------------------------------ scroll

        /// <summary>
        /// Turns the mouse wheel at a point inside a window.
        ///
        /// Same delivery path and coordinate rules as move and click; what differs is the event type
        /// (ScrollWheel) and that the payload is a delta rather than a position. IMGUI reads it as
        /// Event.current.delta on an EventType.ScrollWheel, UI Toolkit as WheelEvent.delta, so a
        /// ScrollView, a long inspector and a GraphView's zoom all respond.
        ///
        /// One wheel notch is about 3 units, and positive y scrolls down - the same convention Unity's
        /// own events use, deliberately not renormalised here.
        /// </summary>
        private static void Scroll(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            var notes = new List<string>();

            if (Mathf.Approximately(p.scrollX, 0f) && Mathf.Approximately(p.scrollY, 0f))
            {
                throw new ArgumentException(
                    "Provide scrollX and/or scrollY for editor_scroll. One wheel notch is about 3; positive y scrolls down.");
            }

            window.Focus();
            RepaintImmediate(window);

            Vector2 point;
            var space = ResolvePoint(window, p, response, notes, out point);
            var delta = new Vector2(p.scrollX, p.scrollY);

            var scroll = new Event
            {
                type = EventType.ScrollWheel,
                mousePosition = point,
                delta = delta,
                modifiers = ParseModifiers(p.modifiers),
            };
            var sent = SendEvent(window, scroll);

            // The pointer really is at that point now, so a following move measures from here.
            PrunePointerPositions();
            PointerPositions[window.GetInstanceID()] = point;

            window.Repaint();
            RepaintImmediate(window);

            DescribeTarget(window, response);
            response.AddOutput("coordinateSpace", space);
            response.AddOutput("x", F(point.x));
            response.AddOutput("y", F(point.y));
            response.AddOutput("scrollX", F(delta.x));
            response.AddOutput("scrollY", F(delta.y));
            response.AddOutput("eventType", "ScrollWheel");
            response.AddOutput("eventsSent", "1");
            response.AddOutput("sendEventReturned", sent ? "true" : "false");
            response.AddOutput("note",
                "sendEventReturned is the raw return of EditorWindow.SendEvent, not proof the view scrolled. " +
                "Confirm with editor_window_capture or by reading the tool's own state with editor_get_field.");

            if (notes.Count > 0)
            {
                response.AddOutput("scrollNotes", string.Join(" | ", notes.ToArray()));
            }
        }

        // ------------------------------------------------------------------ UI Toolkit elements

        /// <summary>
        /// Finds UI Toolkit elements in a window by name, USS class, type or text, and reports where each
        /// one actually is.
        ///
        /// Aiming input at a hard-coded pixel breaks the moment a window is resized or a field is added
        /// above the target. An element's worldBound is already in the host-view space SendEvent expects,
        /// so a query result feeds straight into click, move or scroll - and because the query reports
        /// what it matched, a miss is visible as "no element matched" rather than as a click into empty
        /// space that silently did nothing.
        /// </summary>
        private static void ElementQuery(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);
            RepaintImmediate(window);

            var notes = new List<string>();
            var border = BorderSize(window, notes);
            var offset = border != null ? new Vector2(border.left, border.top) : Vector2.zero;

            var matches = QueryElements(window, p);

            var sb = new StringBuilder("[");
            for (var i = 0; i < matches.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendElementJson(sb, matches[i], i, offset);
            }

            sb.Append(']');

            DescribeTarget(window, response);
            response.AddOutput("count", matches.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            response.AddOutput("elements", sb.ToString());
            response.AddOutput("filters", DescribeElementFilters(p));
            response.AddOutput("note",
                "x/y/w/h and centerX/centerY are host-view coordinates: pass them to editor_click, editor_move or " +
                "editor_scroll with coordinateSpace 'host', or use targetMode 'element' with the same filters.");

            if (notes.Count > 0)
            {
                response.AddOutput("queryNotes", string.Join(" | ", notes.ToArray()));
            }
        }

        private sealed class ElementMatch
        {
            public VisualElement element;
            public int treeIndex;
            public int depth;
        }

        private static List<ElementMatch> QueryElements(EditorWindow window, CommandParameters p)
        {
            var root = window.rootVisualElement;
            if (root == null)
            {
                return new List<ElementMatch>();
            }

            var all = new List<ElementMatch>();
            var index = 0;
            CollectElements(root, 0, ref index, all);

            return all.Where(x => MatchesElementFilters(x.element, p)).ToList();
        }

        private static void CollectElements(VisualElement element, int depth, ref int index, List<ElementMatch> into)
        {
            if (element == null || depth > 32 || into.Count > 2000)
            {
                return;
            }

            into.Add(new ElementMatch { element = element, treeIndex = index, depth = depth });
            index++;

            foreach (var child in element.Children())
            {
                CollectElements(child, depth + 1, ref index, into);
            }
        }

        /// <summary>
        /// Every filter given has to match. Name and type are exact because they identify one element;
        /// text is a contains-match because a label's text is often decorated with counts or units.
        /// </summary>
        private static bool MatchesElementFilters(VisualElement element, CommandParameters p)
        {
            if (!string.IsNullOrEmpty(p.elementName)
                && !string.Equals(element.name, p.elementName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(p.elementClass) && !element.ClassListContains(p.elementClass))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(p.elementType))
            {
                var type = element.GetType();
                if (!string.Equals(type.Name, p.elementType, StringComparison.Ordinal)
                    && !string.Equals(type.FullName, p.elementType, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(p.elementText))
            {
                var text = element as TextElement;
                if (text == null || text.text == null
                    || text.text.IndexOf(p.elementText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static void AppendElementJson(StringBuilder sb, ElementMatch match, int i, Vector2 offset)
        {
            var element = match.element;
            var bound = element.worldBound;
            var text = element as TextElement;

            sb.Append("{\"i\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"treeIndex\":").Append(match.treeIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"depth\":").Append(match.depth.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"type\":\"").Append(Esc(element.GetType().Name)).Append('"');
            sb.Append(",\"name\":\"").Append(Esc(element.name)).Append('"');
            sb.Append(",\"classes\":\"").Append(Esc(string.Join(" ", element.GetClasses().ToArray()))).Append('"');
            sb.Append(",\"text\":\"").Append(Esc(text != null ? text.text : string.Empty)).Append('"');
            sb.Append(",\"x\":").Append(F(bound.x));
            sb.Append(",\"y\":").Append(F(bound.y));
            sb.Append(",\"w\":").Append(F(bound.width));
            sb.Append(",\"h\":").Append(F(bound.height));
            sb.Append(",\"centerX\":").Append(F(bound.center.x));
            sb.Append(",\"centerY\":").Append(F(bound.center.y));
            sb.Append(",\"contentX\":").Append(F(bound.x - offset.x));
            sb.Append(",\"contentY\":").Append(F(bound.y - offset.y));
            sb.Append(",\"enabled\":").Append(element.enabledInHierarchy ? "true" : "false");
            sb.Append(",\"visible\":").Append(element.visible ? "true" : "false");
            sb.Append(",\"pickable\":").Append(element.pickingMode == PickingMode.Position ? "true" : "false");
            sb.Append('}');
        }

        private static string DescribeElementFilters(CommandParameters p)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.elementName)) parts.Add("name=" + p.elementName);
            if (!string.IsNullOrEmpty(p.elementClass)) parts.Add("class=" + p.elementClass);
            if (!string.IsNullOrEmpty(p.elementType)) parts.Add("type=" + p.elementType);
            if (!string.IsNullOrEmpty(p.elementText)) parts.Add("text~=" + p.elementText);
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "(none: every element)";
        }

        /// <summary>
        /// The point an input command should use, in host-view space, plus the space it came from.
        ///
        /// Three ways in, and they are kept in one place so click, move and scroll cannot drift apart:
        /// an element's centre (already host space), or an explicit x/y in content space (tab strip added)
        /// or host space.
        /// </summary>
        private static string ResolvePoint(EditorWindow window, CommandParameters p, CommandResponse response,
            List<string> notes, out Vector2 point)
        {
            if (string.Equals(p.targetMode, "element", StringComparison.OrdinalIgnoreCase))
            {
                point = ResolveElementPoint(window, p, response);
                return "host";
            }

            var space = string.IsNullOrEmpty(p.coordinateSpace)
                ? "content"
                : p.coordinateSpace.Trim().ToLowerInvariant();
            if (space != "content" && space != "host")
            {
                throw new ArgumentException("coordinateSpace must be 'content' (default) or 'host'.");
            }

            var border = BorderSize(window, notes);
            var offset = border != null ? new Vector2(border.left, border.top) : Vector2.zero;
            point = space == "content" ? new Vector2(p.x, p.y) + offset : new Vector2(p.x, p.y);
            response.AddOutput("resolvedFrom", "point");
            response.AddOutput("contentOffset", F(offset.x) + "," + F(offset.y));
            return space;
        }

        private static Vector2 ResolveElementPoint(EditorWindow window, CommandParameters p, CommandResponse response)
        {
            var matches = QueryElements(window, p);
            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No UI Toolkit element matched ({DescribeElementFilters(p)}) in {window.GetType().FullName}. " +
                    "List what is there with editor_element_query.");
            }

            var index = Mathf.Clamp(p.elementIndex, 0, matches.Count - 1);
            if (p.elementIndex >= matches.Count)
            {
                throw new ArgumentException(
                    $"elementIndex {p.elementIndex} is out of range: {matches.Count} element(s) matched ({DescribeElementFilters(p)}).");
            }

            var match = matches[index];
            var bound = match.element.worldBound;
            if (bound.width <= 0f || bound.height <= 0f)
            {
                throw new InvalidOperationException(
                    $"The matched element ({DescribeElementFilters(p)}) has no size ({RectJson(bound)}), so there is nothing to aim at. " +
                    "It may be hidden or not laid out yet.");
            }

            response.AddOutput("resolvedFrom", "element");
            response.AddOutput("elementIndex", index.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("elementMatchCount", matches.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("elementType", match.element.GetType().Name);
            response.AddOutput("elementName", match.element.name ?? string.Empty);
            response.AddOutput("elementRect", RectJson(bound));
            return bound.center;
        }

        // ------------------------------------------------------------------ selection

        private static void SelectionGet(CommandResponse response)
        {
            var objects = Selection.objects ?? Array.Empty<Object>();
            var sb = new StringBuilder("[");
            for (var i = 0; i < objects.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendSelectedObject(sb, objects[i]);
            }

            sb.Append(']');

            response.AddOutput("count", objects.Length.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("objects", sb.ToString());
            response.AddOutput("activeObject", Selection.activeObject != null ? Selection.activeObject.name : string.Empty);
            response.AddOutput("activeInstanceId", Selection.activeInstanceID.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("activeAssetPath", Selection.activeObject != null
                ? AssetDatabase.GetAssetPath(Selection.activeObject)
                : string.Empty);
        }

        /// <summary>
        /// Selects assets or scene objects, which is how an inspector-driven tool is put in front of the
        /// thing it should operate on before its buttons are clicked.
        /// </summary>
        private static void SelectionSet(CommandParameters p, CommandResponse response)
        {
            var selected = new List<Object>();
            var missing = new List<string>();

            if (p.instanceId != 0)
            {
                var byId = EditorUtility.InstanceIDToObject(p.instanceId);
                if (byId != null) selected.Add(byId);
                else missing.Add("instanceId " + p.instanceId.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var path in SplitPaths(p.assetPaths))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset != null) selected.Add(asset);
                else missing.Add(path);
            }

            Selection.objects = selected.ToArray();

            response.AddOutput("requested", (selected.Count + missing.Count).ToString(CultureInfo.InvariantCulture));
            response.AddOutput("selected", selected.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("selectedNames", string.Join(", ", selected.Select(x => x.name).ToArray()));
            response.AddOutput("cleared", selected.Count == 0 ? "true" : "false");

            if (missing.Count > 0)
            {
                // Selecting only some of what was asked for would look like success from the outside.
                throw new InvalidOperationException(
                    "Could not resolve: " + string.Join(", ", missing.ToArray())
                    + ". Asset paths are project-relative, e.g. 'Assets/Prefabs/Hero.prefab'."
                    + $" {selected.Count} of {selected.Count + missing.Count} were selected.");
            }
        }

        internal static IEnumerable<string> SplitPaths(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<string>();
            }

            // Unit Separator first, matching methodArgs, so a path containing a comma still survives.
            var separators = value.IndexOf('\u001F') >= 0
                ? new[] { '\u001F' }
                : new[] { ',', '\n', '\r' };
            return value.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);
        }

        private static void AppendSelectedObject(StringBuilder sb, Object obj)
        {
            if (obj == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append("{\"name\":\"").Append(Esc(obj.name)).Append('"');
            sb.Append(",\"type\":\"").Append(Esc(obj.GetType().Name)).Append('"');
            sb.Append(",\"instanceId\":").Append(obj.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"assetPath\":\"").Append(Esc(AssetDatabase.GetAssetPath(obj))).Append("\"}");
        }

        // ------------------------------------------------------------------ menus

        private static void MenuExecute(CommandParameters p, CommandResponse response)
        {
            var menuPath = Require(p.menuPath, "menuPath");
            var executed = EditorApplication.ExecuteMenuItem(menuPath);

            response.AddOutput("menuPath", menuPath);
            response.AddOutput("executed", executed ? "true" : "false");

            if (!executed)
            {
                throw new InvalidOperationException(
                    $"ExecuteMenuItem returned false for '{menuPath}'. Check the exact path with editor_menu_list.");
            }

            var focused = EditorWindow.focusedWindow;
            if (focused != null)
            {
                response.AddOutput("focusedWindow", DescribeWindow(focused));
            }
        }

        private static void MenuList(CommandParameters p, CommandResponse response)
        {
            var filter = p.filter ?? string.Empty;
            var items = new List<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        MenuItem attribute;
                        try
                        {
                            attribute = (MenuItem)Attribute.GetCustomAttribute(method, typeof(MenuItem));
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (attribute == null || attribute.validate)
                        {
                            continue;
                        }

                        if (filter.Length > 0 && attribute.menuItem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        items.Add("{\"path\":\"" + Esc(attribute.menuItem) + "\",\"type\":\"" + Esc(type.FullName) +
                                  "\",\"method\":\"" + Esc(method.Name) + "\"}");
                    }
                }
            }

            items.Sort(StringComparer.Ordinal);
            response.AddOutput("count", items.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("filter", filter);
            response.AddOutput("menuItems", "[" + string.Join(",", items) + "]");
        }

        // ------------------------------------------------------------------ console

        private static void ConsoleRead(CommandParameters p, CommandResponse response)
        {
            var logEntries = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            var logEntry = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntry");
            if (logEntries == null || logEntry == null)
            {
                throw new MissingMemberException("UnityEditor.LogEntries not available on this Unity version.");
            }

            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var getCounts = logEntries.GetMethod("GetCountsByType", flags);
            if (getCounts != null)
            {
                var counts = new object[] { 0, 0, 0 };
                getCounts.Invoke(null, counts);
                response.AddOutput("errorCount", Convert.ToString(counts[0], CultureInfo.InvariantCulture));
                response.AddOutput("warningCount", Convert.ToString(counts[1], CultureInfo.InvariantCulture));
                response.AddOutput("logCount", Convert.ToString(counts[2], CultureInfo.InvariantCulture));
            }

            var start = logEntries.GetMethod("StartGettingEntries", flags);
            var end = logEntries.GetMethod("EndGettingEntries", flags);
            var getEntry = logEntries.GetMethod("GetEntryInternal", flags);
            if (start == null || end == null || getEntry == null)
            {
                response.AddOutput("entries", "[]");
                response.AddOutput("note", "LogEntries entry API unavailable; counts only.");
                return;
            }

            var maxEntries = p.maxEntries > 0 ? p.maxEntries : 100;
            var filter = p.filter ?? string.Empty;
            var collected = new List<string>();

            var total = Convert.ToInt32(start.Invoke(null, null), CultureInfo.InvariantCulture);
            try
            {
                var entryInstance = Activator.CreateInstance(logEntry);
                var messageField = logEntry.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fileField = logEntry.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var lineField = logEntry.GetField("line", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var modeField = logEntry.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                // Newest entries are the ones a just-run command produced, so walk backwards from the end.
                for (var row = total - 1; row >= 0 && collected.Count < maxEntries; row--)
                {
                    var ok = getEntry.Invoke(null, new[] { row, entryInstance });
                    if (ok is bool && !(bool)ok)
                    {
                        continue;
                    }

                    var message = messageField != null ? Convert.ToString(messageField.GetValue(entryInstance)) : string.Empty;
                    if (filter.Length > 0 && (message == null || message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }

                    var file = fileField != null ? Convert.ToString(fileField.GetValue(entryInstance)) : string.Empty;
                    var line = lineField != null ? Convert.ToString(lineField.GetValue(entryInstance), CultureInfo.InvariantCulture) : "0";
                    var mode = modeField != null ? Convert.ToString(modeField.GetValue(entryInstance), CultureInfo.InvariantCulture) : "0";

                    collected.Add("{\"row\":" + row.ToString(CultureInfo.InvariantCulture) +
                                  ",\"mode\":" + mode +
                                  ",\"file\":\"" + Esc(file) + "\"" +
                                  ",\"line\":" + line +
                                  ",\"message\":\"" + Esc(Truncate(message, ValuePreviewLimit)) + "\"}");
                }
            }
            finally
            {
                end.Invoke(null, null);
            }

            response.AddOutput("totalEntries", total.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("returned", collected.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("entries", "[" + string.Join(",", collected) + "]");
        }

        private static void ConsoleClear(CommandParameters p, CommandResponse response)
        {
            var logEntries = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            var clear = logEntries != null
                ? logEntries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                : null;

            if (clear == null)
            {
                throw new MissingMethodException("UnityEditor.LogEntries.Clear not available on this Unity version.");
            }

            clear.Invoke(null, null);
            response.AddOutput("cleared", "true");
        }

        // ------------------------------------------------------------------ prefs

        private static void PrefsGet(CommandParameters p, CommandResponse response)
        {
            var key = Require(p.prefKey, "prefKey");
            var store = (p.prefStore ?? "editor").ToLowerInvariant();
            var type = (p.prefType ?? "string").ToLowerInvariant();

            string value;
            bool exists;

            if (store == "player")
            {
                exists = PlayerPrefs.HasKey(key);
                switch (type)
                {
                    case "int": value = PlayerPrefs.GetInt(key, 0).ToString(CultureInfo.InvariantCulture); break;
                    case "float": value = PlayerPrefs.GetFloat(key, 0f).ToString("R", CultureInfo.InvariantCulture); break;
                    default: value = PlayerPrefs.GetString(key, string.Empty); break;
                }
            }
            else
            {
                exists = EditorPrefs.HasKey(key);
                switch (type)
                {
                    case "int": value = EditorPrefs.GetInt(key, 0).ToString(CultureInfo.InvariantCulture); break;
                    case "float": value = EditorPrefs.GetFloat(key, 0f).ToString("R", CultureInfo.InvariantCulture); break;
                    case "bool": value = EditorPrefs.GetBool(key, false).ToString(); break;
                    default: value = EditorPrefs.GetString(key, string.Empty); break;
                }
            }

            response.AddOutput("prefStore", store);
            response.AddOutput("prefKey", key);
            response.AddOutput("prefType", type);
            response.AddOutput("exists", exists ? "true" : "false");
            response.AddOutput("value", Truncate(value, 8000));
        }

        private static void PrefsSet(CommandParameters p, CommandResponse response)
        {
            var key = Require(p.prefKey, "prefKey");
            var store = (p.prefStore ?? "editor").ToLowerInvariant();
            var type = (p.prefType ?? "string").ToLowerInvariant();
            var raw = p.fieldValue ?? string.Empty;

            if (store == "player")
            {
                switch (type)
                {
                    case "int": PlayerPrefs.SetInt(key, int.Parse(raw, CultureInfo.InvariantCulture)); break;
                    case "float": PlayerPrefs.SetFloat(key, float.Parse(raw, CultureInfo.InvariantCulture)); break;
                    default: PlayerPrefs.SetString(key, raw); break;
                }
                PlayerPrefs.Save();
            }
            else
            {
                switch (type)
                {
                    case "int": EditorPrefs.SetInt(key, int.Parse(raw, CultureInfo.InvariantCulture)); break;
                    case "float": EditorPrefs.SetFloat(key, float.Parse(raw, CultureInfo.InvariantCulture)); break;
                    case "bool": EditorPrefs.SetBool(key, ParseBool(raw)); break;
                    default: EditorPrefs.SetString(key, raw); break;
                }
            }

            response.AddOutput("prefStore", store);
            response.AddOutput("prefKey", key);
            response.AddOutput("prefType", type);
            response.AddOutput("written", "true");
        }

        // ------------------------------------------------------------------ play mode

        private static void PlayMode(CommandParameters p, CommandResponse response)
        {
            var action = (p.playModeAction ?? "status").ToLowerInvariant();

            switch (action)
            {
                case "enter":
                    EditorApplication.EnterPlaymode();
                    break;
                case "exit":
                    EditorApplication.ExitPlaymode();
                    break;
                case "toggle":
                    if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                    else EditorApplication.EnterPlaymode();
                    break;
                case "status":
                    break;
                default:
                    throw new ArgumentException($"Unknown playModeAction '{action}'. Use enter, exit, toggle, status.");
            }

            response.AddOutput("action", action);
            response.AddOutput("isPlaying", EditorApplication.isPlaying ? "true" : "false");
            response.AddOutput("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode ? "true" : "false");
            response.AddOutput("isCompiling", EditorApplication.isCompiling ? "true" : "false");
            response.AddOutput("isUpdating", EditorApplication.isUpdating ? "true" : "false");
        }

        private static void ExitPlayMode(CommandResponse response)
        {
            // Play mode makes Unity defer recompilation, so a freshly added bridge command stays invisible
            // until play stops. Keeping a dedicated command means recovery never needs the UI.
            var wasPlaying = EditorApplication.isPlaying;
            if (wasPlaying)
            {
                EditorApplication.ExitPlaymode();
            }

            response.AddOutput("wasPlaying", wasPlaying ? "true" : "false");
            response.AddOutput("isPlaying", EditorApplication.isPlaying ? "true" : "false");
            response.AddOutput("isCompiling", EditorApplication.isCompiling ? "true" : "false");
        }

        // ------------------------------------------------------------------ shared helpers

        internal static EditorWindow[] AllWindows()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(x => x != null)
                .OrderBy(x => x.GetType().FullName, StringComparer.Ordinal)
                .ToArray();
        }

        private static EditorWindow ResolveWindow(CommandParameters p)
        {
            var windows = AllWindows();

            if (p.instanceId != 0)
            {
                return windows.FirstOrDefault(x => x.GetInstanceID() == p.instanceId);
            }

            if (!string.IsNullOrEmpty(p.windowType))
            {
                var match = windows.FirstOrDefault(x => TypeMatches(x.GetType(), p.windowType));
                if (match != null) return match;
            }

            if (!string.IsNullOrEmpty(p.windowTitle))
            {
                return windows.FirstOrDefault(x => x.titleContent != null && x.titleContent.text == p.windowTitle);
            }

            return null;
        }

        internal static EditorWindow ResolveWindowOrThrow(CommandParameters p)
        {
            var window = ResolveWindow(p);
            if (window != null)
            {
                return window;
            }

            var open = string.Join(", ", AllWindows().Select(x => x.GetType().Name).Distinct());
            throw new InvalidOperationException(
                $"No open EditorWindow matched (windowType='{p.windowType}', title='{p.windowTitle}', instanceId={p.instanceId}). Open windows: {open}");
        }

        private static Type FindWindowType(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(x => typeof(EditorWindow).IsAssignableFrom(x) && TypeMatches(x, name));
        }

        private static bool TypeMatches(Type type, string name)
        {
            return string.Equals(type.FullName, name, StringComparison.Ordinal)
                || string.Equals(type.Name, name, StringComparison.Ordinal)
                || string.Equals(type.AssemblyQualifiedName, name, StringComparison.Ordinal);
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(x => x != null);
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }

        internal static object ReadMemberByName(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { return field.GetValue(instance); }
                    catch (Exception) { return null; }
                }

                var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.CanRead)
                {
                    try { return property.GetValue(instance, null); }
                    catch (Exception) { return null; }
                }
            }

            return null;
        }

        internal static bool IsCoercible(Type type)
        {
            if (type == null) return false;
            if (type == typeof(string) || type.IsEnum || type.IsPrimitive) return true;
            return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Color);
        }

        private static object Coerce(string raw, Type target)
        {
            if (target == typeof(string))
            {
                return raw;
            }

            if (raw == null)
            {
                throw new ArgumentException($"fieldValue is required for target type {FriendlyTypeName(target)}.");
            }

            var text = raw.Trim();

            if (target.IsEnum)
            {
                int numeric;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                {
                    return Enum.ToObject(target, numeric);
                }

                return Enum.Parse(target, text, true);
            }

            if (target == typeof(bool)) return ParseBool(text);
            if (target == typeof(int)) return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(long)) return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(short)) return short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(byte)) return byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (target == typeof(float)) return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (target == typeof(double)) return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (target == typeof(char)) return text.Length > 0 ? text[0] : '\0';

            if (target == typeof(Vector2))
            {
                var parts = SplitNumbers(text, 2);
                return new Vector2(parts[0], parts[1]);
            }

            if (target == typeof(Vector3))
            {
                var parts = SplitNumbers(text, 3);
                return new Vector3(parts[0], parts[1], parts[2]);
            }

            if (target == typeof(Vector4))
            {
                var parts = SplitNumbers(text, 4);
                return new Vector4(parts[0], parts[1], parts[2], parts[3]);
            }

            if (target == typeof(Color))
            {
                var parts = SplitNumbers(text, 4);
                return new Color(parts[0], parts[1], parts[2], parts[3]);
            }

            // Anything left is a plain data object: JSON is the only sane way to describe it over the bridge.
            try
            {
                return JsonUtility.FromJson(text, target);
            }
            catch (Exception e)
            {
                throw new NotSupportedException(
                    $"Cannot convert '{Truncate(text, 80)}' to {FriendlyTypeName(target)}: {e.Message}");
            }
        }

        private static float[] SplitNumbers(string text, int expected)
        {
            var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new float[expected];
            for (var i = 0; i < expected; i++)
            {
                result[i] = i < parts.Length
                    ? float.Parse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : 0f;
            }

            return result;
        }

        private static bool ParseBool(string text)
        {
            if (string.Equals(text, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(text, "0", StringComparison.Ordinal)) return false;
            return bool.Parse(text);
        }

        private static string[] SplitArgs(string args)
        {
            if (string.IsNullOrEmpty(args))
            {
                return Array.Empty<string>();
            }

            return args.Split('\u001F');
        }

        private static string DescribeValue(object value, int depth)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();

            if (value is string) return Truncate("\"" + value + "\"", ValuePreviewLimit);
            if (type.IsPrimitive || type.IsEnum) return SafeToString(value);
            if (value is Object unityObject) return $"{type.Name}({(unityObject != null ? unityObject.name : "null")})";
            if (value is Vector2 || value is Vector3 || value is Vector4 || value is Color || value is Rect) return SafeToString(value);

            var list = value as IList;
            if (list != null)
            {
                var sb = new StringBuilder();
                sb.Append(FriendlyTypeName(type)).Append("[Count=").Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                if (depth > 0 && list.Count > 0)
                {
                    sb.Append(" { ");
                    var shown = Math.Min(list.Count, 10);
                    for (var i = 0; i < shown; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=").Append(DescribeValue(list[i], depth - 1));
                    }
                    if (shown < list.Count) sb.Append(", ...");
                    sb.Append(" }");
                }

                return Truncate(sb.ToString(), ValuePreviewLimit);
            }

            if (depth > 0)
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                if (fields.Length > 0)
                {
                    var sb = new StringBuilder(FriendlyTypeName(type)).Append(" { ");
                    for (var i = 0; i < fields.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        object nested;
                        try { nested = fields[i].GetValue(value); }
                        catch (Exception) { nested = "<unreadable>"; }
                        sb.Append(fields[i].Name).Append('=').Append(DescribeValue(nested, depth - 1));
                    }
                    sb.Append(" }");
                    return Truncate(sb.ToString(), ValuePreviewLimit);
                }
            }

            return Truncate(FriendlyTypeName(type) + ":" + SafeToString(value), ValuePreviewLimit);
        }

        private static string SafeToString(object value)
        {
            if (value == null) return "null";
            try
            {
                var formattable = value as IFormattable;
                return formattable != null
                    ? formattable.ToString(null, CultureInfo.InvariantCulture)
                    : value.ToString();
            }
            catch (Exception e)
            {
                return "<ToString failed: " + e.Message + ">";
            }
        }

        internal static string FriendlyTypeName(Type type)
        {
            if (type == null) return "void";
            if (!type.IsGenericType) return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName)) + ">";
        }

        private static string Truncate(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit) return text ?? string.Empty;
            return text.Substring(0, limit) + "...(+" + (text.Length - limit).ToString(CultureInfo.InvariantCulture) + " chars)";
        }

        internal static string RectJson(Rect rect)
        {
            return "{\"x\":" + F(rect.x) + ",\"y\":" + F(rect.y) +
                   ",\"w\":" + F(rect.width) + ",\"h\":" + F(rect.height) + "}";
        }

        internal static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"{name} is required.");
            }

            return value;
        }

        internal static string Esc(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length + 16);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static bool IsSpecialGetterOrSetter(this MethodInfo method)
        {
            return method.IsSpecialName
                || method.Name.StartsWith("get_", StringComparison.Ordinal)
                || method.Name.StartsWith("set_", StringComparison.Ordinal)
                || method.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal);
        }
    }
}
