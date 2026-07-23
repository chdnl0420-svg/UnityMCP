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
        internal const string BridgeVersion = "0.2.7";

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
            "editor_set_field",
            "editor_get_field",
            "editor_invoke_method",
            "editor_click",
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
                case "editor_set_field": SetField(p, response); return true;
                case "editor_get_field": GetField(p, response); return true;
                case "editor_invoke_method": InvokeMethod(p, response); return true;
                case "editor_click": Click(p, response); return true;
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
            var args = SplitArgs(p.methodArgs);

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

        private static void Click(CommandParameters p, CommandResponse response)
        {
            var window = ResolveWindowOrThrow(p);

            // Layout rects only exist once a pass has run, so refresh before resolving an entry index.
            RepaintImmediate(window);

            Vector2 point;
            if (string.Equals(p.targetMode, "entry", StringComparison.OrdinalIgnoreCase))
            {
                Vector2 containerOffset;
                var rect = ResolveLayoutRect(window, p.entryIndex, out containerOffset);

                // Layout rects are local to the IMGUIContainer; SendEvent lands in the host view's
                // space, which also contains the tab strip. Without the offset the click misses high.
                point = new Vector2(
                    containerOffset.x + rect.x + rect.width * 0.5f,
                    containerOffset.y + rect.y + rect.height * 0.5f);

                response.AddOutput("resolvedFrom", "layoutEntry");
                response.AddOutput("entryIndex", p.entryIndex.ToString(CultureInfo.InvariantCulture));
                response.AddOutput("entryRect", RectJson(rect));
                response.AddOutput("containerOffset", F(containerOffset.x) + "," + F(containerOffset.y));
            }
            else
            {
                point = new Vector2(p.x, p.y);
                response.AddOutput("resolvedFrom", "point");
            }

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

        private static void RepaintImmediate(EditorWindow window)
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

            // ReadScreenPixel works in bottom-left screen space while EditorWindow.position is top-left,
            // so the y origin has to be flipped against the display that holds the window.
            var displayHeight = Screen.currentResolution.height;
            var originY = p.noFlipY ? rect.y : displayHeight - (rect.y + rect.height);
            var origin = new Vector2(rect.x, originY);

            var pixels = readScreenPixel.Invoke(null, new object[] { origin, width, height }) as Color[];
            if (pixels == null || pixels.Length == 0)
            {
                throw new InvalidOperationException("ReadScreenPixel returned no pixels (window off-screen or minimized?).");
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

        private static EditorWindow ResolveWindowOrThrow(CommandParameters p)
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

        private static object ReadMemberByName(object instance, string name)
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

        private static string RectJson(Rect rect)
        {
            return "{\"x\":" + F(rect.x) + ",\"y\":" + F(rect.y) +
                   ",\"w\":" + F(rect.width) + ",\"h\":" + F(rect.height) + "}";
        }

        private static string F(float value)
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
