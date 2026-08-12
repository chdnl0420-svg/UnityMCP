using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;
using System.IO;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Component wiring: attach a component to a scene object and fill its serialized fields.
    ///
    /// Neither existing command family could do this. The runtime commands drive NGUI at play time,
    /// and EditorToolBridge drives EditorWindows - its editor_set_field walks an EditorWindow's own
    /// instance fields, not a component's. So the one thing UI work actually needs, putting an object
    /// into a [SerializeField] slot, had no route but dozens of Hierarchy-to-Inspector drags by hand.
    /// Editing the prefab YAML instead is worse: it skips the UI tool's own save path.
    ///
    /// Two routes are offered on purpose.
    ///   1. <c>set_component_field</c> writes through SerializedObject. Deterministic and fast.
    ///   2. <c>drag_object_to_field</c> performs a real DragAndDrop onto a real ObjectField, so the
    ///      same code a human drag runs - type filtering, Undo, ApplyModifiedProperties - runs here
    ///      too. The field is drawn in <see cref="WiringProbeWindow"/> because the Inspector does not
    ///      report where it drew a given property.
    ///
    /// Every write re-reads the property afterwards and reports before/after. Unity drops a
    /// wrong-typed object reference without raising anything, so "changed: false" on a re-read is the
    /// only honest way to catch it - echoing back what was requested would report success either way.
    /// </summary>
    internal static class ComponentWiringBridge
    {
        internal static readonly string[] SupportedCommands =
        {
            "list_components",
            "add_component",
            "remove_component",
            "get_component_field",
            "set_component_field",
            "drag_object_to_field",
        };

        internal static bool TryExecute(string command, CommandParameters p, CommandResponse response)
        {
            switch (command)
            {
                case "list_components": ListComponents(p, response); return true;
                case "add_component": AddComponent(p, response); return true;
                case "remove_component": RemoveComponent(p, response); return true;
                case "get_component_field": GetComponentField(p, response); return true;
                case "set_component_field": SetComponentField(p, response); return true;
                case "drag_object_to_field": DragObjectToField(p, response); return true;
                case "duplicate_object": DuplicateObject(p, response); return true;
                case "wire_prefab_field": WirePrefabField(p, response); return true;
                case "remove_prefab_component": RemovePrefabComponent(p, response); return true;
            }

            return false;
        }

        // --- commands -------------------------------------------------------------------------

        private static void ListComponents(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var components = target.GetComponents<Component>().Where(x => x != null).ToList();

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("activeSelf", target.activeSelf.ToString());
            response.AddOutput("componentCount", components.Count.ToString());

            for (var i = 0; i < components.Count; i++)
            {
                var serialized = new SerializedObject(components[i]);

                response.AddOutput(
                    $"component[{i}]",
                    $"{components[i].GetType().Name} :: {string.Join(", ", SerializedFieldNames(serialized))}");
            }
        }

        private static void AddComponent(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var typeName = Require(p.componentName, "componentName");
            var type = ResolveComponentType(typeName);

            var existing = target.GetComponents<Component>().Where(x => x != null && x.GetType() == type).ToList();

            // Adding a second copy is almost always a mistake, so it needs saying out loud.
            if (existing.Count > 0 && !p.allowDuplicate)
            {
                response.AddOutput("target", HierarchyPath(target));
                response.AddOutput("component", type.Name);
                response.AddOutput("added", "false");
                response.AddOutput("reason", "already present (pass allowDuplicate to add another)");
                response.AddOutput("existingCount", existing.Count.ToString());
                return;
            }

            var component = Undo.AddComponent(target, type);

            if (component == null)
            {
                throw new InvalidOperationException(
                    $"AddComponent({type.Name}) returned null on {HierarchyPath(target)} - the type may require a component that is missing.");
            }

            EditorUtility.SetDirty(component);
            MarkSceneDirty(target);

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("component", type.Name);
            response.AddOutput("added", "true");
            response.AddOutput("fields", string.Join(", ", SerializedFieldNames(new SerializedObject(component))));
        }

        private static void RemoveComponent(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var component = FindComponent(target, Require(p.componentName, "componentName"), p.componentIndex);

            Undo.DestroyObjectImmediate(component);
            MarkSceneDirty(target);

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("component", p.componentName);
            response.AddOutput("removed", "true");
            response.AddOutput("remaining", target.GetComponents<Component>()
                .Where(x => x != null)
                .Count(x => MatchesTypeName(x.GetType(), p.componentName))
                .ToString());
        }

        private static void GetComponentField(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var component = FindComponent(target, Require(p.componentName, "componentName"), p.componentIndex);
            var path = NormalizePropertyPath(p.fieldPath);
            var serialized = new SerializedObject(component);
            var property = FindPropertyOrThrow(serialized, path, component);

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("component", component.GetType().Name);
            response.AddOutput("fieldPath", path);
            response.AddOutput("propertyType", property.propertyType.ToString());
            response.AddOutput("value", DescribeProperty(property));

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                response.AddOutput("arraySize", property.arraySize.ToString());

                for (var i = 0; i < property.arraySize; i++)
                {
                    response.AddOutput($"element[{i}]", DescribeProperty(property.GetArrayElementAtIndex(i)));
                }
            }
        }

        private static void SetComponentField(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var component = FindComponent(target, Require(p.componentName, "componentName"), p.componentIndex);
            var path = NormalizePropertyPath(p.fieldPath);
            var serialized = new SerializedObject(component);
            var property = FindPropertyOrThrow(serialized, path, component);

            var before = DescribeProperty(property);
            var kind = string.IsNullOrEmpty(p.fieldKind) ? "objectRef" : p.fieldKind.Trim();
            string requested;

            switch (kind)
            {
                case "objectRef":
                    var value = ResolveValueObject(p);
                    requested = $"{value.name} ({value.GetType().Name})";
                    property.objectReferenceValue = value;
                    break;

                case "null":
                    requested = "null";
                    property.objectReferenceValue = null;
                    break;

                case "arraySize":
                    requested = Require(p.fieldValue, "fieldValue");
                    property.arraySize = int.Parse(requested, CultureInfo.InvariantCulture);
                    break;

                case "int":
                    requested = Require(p.fieldValue, "fieldValue");
                    property.intValue = int.Parse(requested, CultureInfo.InvariantCulture);
                    break;

                case "float":
                    requested = Require(p.fieldValue, "fieldValue");
                    property.floatValue = float.Parse(requested, CultureInfo.InvariantCulture);
                    break;

                case "bool":
                    requested = Require(p.fieldValue, "fieldValue");
                    property.boolValue = ParseBool(requested);
                    break;

                case "string":
                    requested = p.fieldValue ?? string.Empty;
                    property.stringValue = requested;
                    break;

                case "enum":
                    requested = Require(p.fieldValue, "fieldValue");
                    property.enumValueIndex = ResolveEnumIndex(property, requested);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown fieldKind '{kind}'. Use objectRef, null, arraySize, int, float, bool, string or enum.");
            }

            // ApplyModifiedProperties records its own undo entry, so no separate Undo.RecordObject.
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            MarkSceneDirty(target);

            // Re-read from a fresh SerializedObject. A reference Unity refused (wrong type) is gone by
            // now, and that is exactly the case a caller must not mistake for success.
            var after = DescribeProperty(FindPropertyOrThrow(new SerializedObject(component), path, component));

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("component", component.GetType().Name);
            response.AddOutput("fieldPath", path);
            response.AddOutput("fieldKind", kind);
            response.AddOutput("requested", requested);
            response.AddOutput("before", before);
            response.AddOutput("after", after);
            response.AddOutput("changed", before != after ? "true" : "false");
        }

        private static void DragObjectToField(CommandParameters p, CommandResponse response)
        {
            var target = FindSceneObject(p.targetPath, p.targetName);
            var component = FindComponent(target, Require(p.componentName, "componentName"), p.componentIndex);
            var path = NormalizePropertyPath(p.fieldPath);
            var source = ResolveValueObject(p);

            var before = DescribeProperty(FindPropertyOrThrow(new SerializedObject(component), path, component));

            var mode = string.IsNullOrEmpty(p.dropMode) ? "probe" : p.dropMode.Trim().ToLowerInvariant();

            EditorWindow window;
            Vector2 point;
            WiringProbeWindow probe = null;

            if (mode == "probe")
            {
                probe = WiringProbeWindow.Open(component, path);
                EditorToolBridge.RepaintImmediate(probe);

                if (!string.IsNullOrEmpty(probe.LastError))
                {
                    probe.Close();
                    throw new InvalidOperationException($"Probe could not draw the field: {probe.LastError}");
                }

                if (!probe.HasFieldRect)
                {
                    probe.Close();
                    throw new InvalidOperationException("Probe drew no field rect, so there is nothing to drop onto.");
                }

                window = probe;
                point = probe.FieldRect.center;
            }
            else if (mode == "window")
            {
                window = ResolveWindow(p);
                point = new Vector2(p.x, p.y);
            }
            else
            {
                throw new ArgumentException("dropMode must be 'probe' (default) or 'window'.");
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new[] { source };
            DragAndDrop.paths = new string[0];

            // StartDrag is what a real mouse drag calls, but outside a MouseDrag event it can hand the
            // gesture to the OS and never come back. An ObjectField reads DragAndDrop.objectReferences
            // directly, so the payload alone is enough; keep StartDrag behind a flag for the case where
            // a target turns out to need the full gesture.
            if (p.startDrag)
            {
                DragAndDrop.StartDrag("mcp drag_object_to_field");
            }

            var updated = SendDragEvent(window, EventType.DragUpdated, point);
            var visualMode = DragAndDrop.visualMode.ToString();
            var performed = SendDragEvent(window, EventType.DragPerform, point);

            EditorToolBridge.RepaintImmediate(window);

            EditorUtility.SetDirty(component);
            MarkSceneDirty(target);

            var after = DescribeProperty(FindPropertyOrThrow(new SerializedObject(component), path, component));

            if (probe != null)
            {
                probe.Close();
            }

            response.AddOutput("target", HierarchyPath(target));
            response.AddOutput("component", component.GetType().Name);
            response.AddOutput("fieldPath", path);
            response.AddOutput("dropMode", mode);
            response.AddOutput("dropPoint", $"{point.x:0.##},{point.y:0.##}");
            response.AddOutput("source", $"{source.name} ({source.GetType().Name})");
            response.AddOutput("dragUpdatedDelivered", updated.ToString());
            // visualMode says the field accepted the payload while hovering. It is not proof the drop
            // landed - that is what before/after is for - but a "Rejected" here names the reason.
            response.AddOutput("visualMode", visualMode);
            response.AddOutput("dragPerformDelivered", performed.ToString());
            response.AddOutput("before", before);
            response.AddOutput("after", after);
            response.AddOutput("changed", before != after ? "true" : "false");
        }

        // --- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Scene objects only, inactive included. Wiring targets are almost always inactive UI, so the
        /// includeInactive flag the runtime commands honour would make nearly every call fail.
        /// </summary>
        private static GameObject FindSceneObject(string path, string name)
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => !EditorUtility.IsPersistent(x))
                .Where(x => x.scene.IsValid());

            List<GameObject> matches;
            string wanted;

            if (!string.IsNullOrEmpty(path))
            {
                wanted = path;
                matches = all.Where(x => HierarchyPath(x) == path).ToList();
            }
            else if (!string.IsNullOrEmpty(name))
            {
                wanted = name;
                matches = all.Where(x => x.name == name).ToList();
            }
            else
            {
                throw new ArgumentException("targetPath or targetName is required.");
            }

            if (matches.Count == 0)
            {
                throw new ArgumentException($"Scene object not found: {wanted}");
            }

            // Picking the first of several would wire the wrong object and look like it worked.
            if (matches.Count > 1)
            {
                throw new ArgumentException(
                    $"Ambiguous target: {matches.Count} scene objects match '{wanted}'. Pass the full targetPath.");
            }

            return matches[0];
        }

        private static Component FindComponent(GameObject target, string componentName, int index)
        {
            var matches = target.GetComponents<Component>()
                .Where(x => x != null && MatchesTypeName(x.GetType(), componentName))
                .ToList();

            if (matches.Count == 0)
            {
                var present = string.Join(", ", target.GetComponents<Component>()
                    .Where(x => x != null)
                    .Select(x => x.GetType().Name));

                throw new ArgumentException(
                    $"No component '{componentName}' on {HierarchyPath(target)}. Present: {present}");
            }

            if (index < 0 || index >= matches.Count)
            {
                throw new ArgumentException(
                    $"componentIndex {index} is out of range: {HierarchyPath(target)} has {matches.Count} '{componentName}'.");
            }

            return matches[index];
        }

        /// <summary>
        /// Copies a scene object under another parent, keeping its local transform and active state.
        /// Prefab authoring needs this when one slot holds the only copy of an effect and the other
        /// slots must each own theirs; wiring alone cannot do it because a field can only point at
        /// an object that already exists.
        ///
        /// targetPath/targetName = what to copy, valuePath/valueName = the new parent,
        /// fieldValue = optional name for the copy (defaults to the source name, without "(Clone)").
        /// </summary>
        private static void DuplicateObject(CommandParameters p, CommandResponse response)
        {
            var source = FindSceneObject(p.targetPath, p.targetName);
            var parent = FindSceneObject(p.valuePath, p.valueName);

            var clone = Object.Instantiate(source, parent.transform);

            clone.name = string.IsNullOrEmpty(p.fieldValue) ? source.name : p.fieldValue;
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;
            clone.SetActive(source.activeSelf);

            Undo.RegisterCreatedObjectUndo(clone, "duplicate_object");
            EditorUtility.SetDirty(parent);

            response.AddOutput("source", HierarchyPath(source));
            response.AddOutput("parent", HierarchyPath(parent));
            response.AddOutput("clone", HierarchyPath(clone));
            response.AddOutput("cloneName", clone.name);
            response.AddOutput("activeSelf", clone.activeSelf.ToString());
        }

        /// <summary>
        /// Wires a serialized field on a prefab asset without opening Prefab Mode.
        /// Scene wiring cannot reach an asset, and a prefab that is only spawned at runtime
        /// never sits in a scene to be wired there — so neither existing path covers it.
        ///
        /// targetPath = prefab asset path, componentName/fieldPath = what to write,
        /// valuePath = child path inside the prefab that the field should point at
        /// (empty means the prefab root itself).
        /// </summary>
        private static void WirePrefabField(CommandParameters p, CommandResponse response)
        {
            var prefabPath = Require(p.targetPath, "targetPath");
            var root = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                // A prefab's scripts usually hang off children, not the root, so the owner is
                // addressed by path. Without this the command could only reach root components.
                var owner = root;

                if (!string.IsNullOrEmpty(p.componentPath))
                {
                    var ownerTransform = root.transform.Find(p.componentPath);

                    if (ownerTransform == null)
                    {
                        throw new ArgumentException(
                            $"Component owner not found inside prefab '{prefabPath}': {p.componentPath}");
                    }

                    owner = ownerTransform.gameObject;
                }

                var component = FindComponent(owner, Require(p.componentName, "componentName"), p.componentIndex);
                var path = NormalizePropertyPath(p.fieldPath);
                var serialized = new SerializedObject(component);
                var property = FindPropertyOrThrow(serialized, path, component);

                var before = DescribeProperty(property);

                // An array has to be grown before anything can be written into an index, so the
                // caller says which kind of write this is. Defaulting to objectRef keeps older calls working.
                var kind = string.IsNullOrEmpty(p.fieldKind) ? "objectRef" : p.fieldKind;
                string after;

                switch (kind)
                {
                    case "objectRef":
                        var child = string.IsNullOrEmpty(p.valuePath)
                            ? root.transform
                            : root.transform.Find(p.valuePath);

                        if (child == null)
                        {
                            throw new ArgumentException(
                                $"Child not found inside prefab '{prefabPath}': {p.valuePath}");
                        }

                        // A field typed as a component needs the component itself, not the GameObject -
                        // valueComponentName says which one to pull off that child.
                        if (string.IsNullOrEmpty(p.valueComponentName))
                        {
                            property.objectReferenceValue = child.gameObject;
                            after = child.name;
                        }
                        else
                        {
                            var valueComponent = FindComponent(
                                child.gameObject, p.valueComponentName, p.valueComponentIndex);

                            property.objectReferenceValue = valueComponent;
                            after = $"{child.name} ({valueComponent.GetType().Name})";
                        }

                        break;

                    case "null":
                        property.objectReferenceValue = null;
                        after = "null";
                        break;

                    case "arraySize":
                        after = Require(p.fieldValue, "fieldValue");
                        property.arraySize = int.Parse(after, CultureInfo.InvariantCulture);
                        break;

                    default:
                        throw new ArgumentException(
                            $"Unknown fieldKind '{kind}' for wire_prefab_field. Use objectRef, null or arraySize.");
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();

                // Unity always writes prefabs with bare LF. A project whose prefabs are stored with
                // CRLF would otherwise show every single line as changed in the diff, so remember
                // what the file used before saving and put it back afterwards.
                var usedCrLf = FileUsesCrLf(prefabPath);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (usedCrLf)
                {
                    RestoreCrLf(prefabPath);
                }

                response.AddOutput("prefab", prefabPath);
                response.AddOutput("owner", string.IsNullOrEmpty(p.componentPath) ? "(root)" : p.componentPath);
                response.AddOutput("component", component.GetType().Name);
                response.AddOutput("field", path);
                response.AddOutput("kind", kind);
                response.AddOutput("before", before);
                response.AddOutput("after", after);
            }
            finally
            {
                // The loaded copy lives in a hidden scene; leaving it behind leaks that scene
                // and later loads of the same prefab trip over it.
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Deletes a component from a prefab asset. remove_component only reaches scene objects, so a
        /// component that exists solely inside a prefab could not be removed through MCP at all.
        /// </summary>
        private static void RemovePrefabComponent(CommandParameters p, CommandResponse response)
        {
            var prefabPath = Require(p.targetPath, "targetPath");
            var root = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                var owner = root;

                if (!string.IsNullOrEmpty(p.componentPath))
                {
                    var ownerTransform = root.transform.Find(p.componentPath);

                    if (ownerTransform == null)
                    {
                        throw new ArgumentException(
                            $"Component owner not found inside prefab '{prefabPath}': {p.componentPath}");
                    }

                    owner = ownerTransform.gameObject;
                }

                var component = FindComponent(owner, Require(p.componentName, "componentName"), p.componentIndex);
                var removed = component.GetType().Name;

                Object.DestroyImmediate(component, true);

                var usedCrLf = FileUsesCrLf(prefabPath);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (usedCrLf)
                {
                    RestoreCrLf(prefabPath);
                }

                response.AddOutput("prefab", prefabPath);
                response.AddOutput("owner", string.IsNullOrEmpty(p.componentPath) ? "(root)" : p.componentPath);
                response.AddOutput("removed", removed);
                response.AddOutput("remaining", string.Join(", ", owner.GetComponents<Component>()
                    .Where(x => x != null)
                    .Select(x => x.GetType().Name)));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Raw byte values, not char literals - keeping the escapes out of this file is what stops
        // a rewrite from turning them into real newlines.
        private const byte LineFeed = 10;
        private const byte CarriageReturn = 13;

        /// <summary>Does this file use CRLF line endings? The first break in the file decides.</summary>
        private static bool FileUsesCrLf(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using (var stream = File.OpenRead(path))
            {
                var buffer = new byte[8192];
                var read = stream.Read(buffer, 0, buffer.Length);

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != LineFeed)
                    {
                        continue;
                    }

                    return i > 0 && buffer[i - 1] == CarriageReturn;
                }
            }

            return false;
        }

        /// <summary>
        /// Rewrites lone LF bytes as CRLF, leaving existing CRLF pairs alone. Works on raw bytes so
        /// the file's encoding and byte-order mark survive untouched.
        /// </summary>
        private static void RestoreCrLf(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var result = new List<byte>(bytes.Length + (bytes.Length / 40));

            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == LineFeed && (i == 0 || bytes[i - 1] != CarriageReturn))
                {
                    result.Add(CarriageReturn);
                }

                result.Add(bytes[i]);
            }

            File.WriteAllBytes(path, result.ToArray());
        }

        private static Object ResolveValueObject(CommandParameters p)
        {
            if (!string.IsNullOrEmpty(p.valueAssetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(p.valueAssetPath);

                if (asset == null)
                {
                    throw new ArgumentException($"Asset not found: {p.valueAssetPath}");
                }

                return asset;
            }

            var target = FindSceneObject(p.valuePath, p.valueName);

            if (string.IsNullOrEmpty(p.valueComponentName))
            {
                return target;
            }

            return FindComponent(target, p.valueComponentName, p.valueComponentIndex);
        }

        private static SerializedProperty FindPropertyOrThrow(SerializedObject serialized, string path, Component component)
        {
            var property = serialized.FindProperty(path);

            if (property == null)
            {
                throw new ArgumentException(
                    $"No serialized property '{path}' on {component.GetType().Name}. Present: {string.Join(", ", SerializedFieldNames(serialized))}");
            }

            return property;
        }

        /// <summary>Accepts "slots[2]" as shorthand for Unity's own "slots.Array.data[2]".</summary>
        private static string NormalizePropertyPath(string raw)
        {
            var path = Require(raw, "fieldPath");

            // Protect an already-correct path so the shorthand rewrite cannot double up on it.
            const string marker = "\u0001";
            path = path.Replace(".Array.data[", marker);
            path = Regex.Replace(path, @"\[(\d+)\]", ".Array.data[$1]");

            return path.Replace(marker, ".Array.data[");
        }

        private static IEnumerable<string> SerializedFieldNames(SerializedObject serialized)
        {
            var names = new List<string>();
            var iterator = serialized.GetIterator();
            var enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (iterator.name != "m_Script")
                {
                    names.Add(iterator.name);
                }
            }

            return names;
        }

        private static string DescribeProperty(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    var value = property.objectReferenceValue;
                    return value == null ? "null" : $"{value.name} ({value.GetType().Name})";
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue.ToString();
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Enum:
                    return $"{property.enumValueIndex}:{SafeEnumName(property)}";
                case SerializedPropertyType.Color:
                    return property.colorValue.ToString();
                case SerializedPropertyType.Vector2:
                    return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3:
                    return property.vector3Value.ToString();
            }

            if (property.isArray)
            {
                var elements = new List<string>();

                for (var i = 0; i < property.arraySize; i++)
                {
                    elements.Add(DescribeProperty(property.GetArrayElementAtIndex(i)));
                }

                return $"[{string.Join(", ", elements)}]";
            }

            return property.propertyType.ToString();
        }

        private static string SafeEnumName(SerializedProperty property)
        {
            var names = property.enumNames;

            return property.enumValueIndex >= 0 && property.enumValueIndex < names.Length
                ? names[property.enumValueIndex]
                : "?";
        }

        private static int ResolveEnumIndex(SerializedProperty property, string raw)
        {
            var names = property.enumNames;

            for (var i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], raw, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            int index;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                index >= 0 && index < names.Length)
            {
                return index;
            }

            throw new ArgumentException($"'{raw}' is not one of: {string.Join(", ", names)}");
        }

        private static Type ResolveComponentType(string typeName)
        {
            var candidates = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (Exception)
                    {
                        // A reflection-only or partially loaded assembly cannot be enumerated; skipping it
                        // is fine because the type we want lives in a normally loaded one.
                        return Type.EmptyTypes;
                    }
                })
                .Where(x => typeof(Component).IsAssignableFrom(x) && !x.IsAbstract)
                .Where(x => x.Name == typeName || x.FullName == typeName)
                .Distinct()
                .ToList();

            if (candidates.Count == 0)
            {
                throw new ArgumentException($"Component type not found: {typeName}");
            }

            if (candidates.Count > 1)
            {
                throw new ArgumentException(
                    $"Ambiguous component type '{typeName}': {string.Join(", ", candidates.Select(x => x.FullName))}. Pass the full name.");
            }

            return candidates[0];
        }

        private static bool MatchesTypeName(Type type, string name)
        {
            return type.Name == name || type.FullName == name;
        }

        private static string HierarchyPath(GameObject target)
        {
            var names = new List<string>();

            for (var current = target.transform; current != null; current = current.parent)
            {
                names.Add(current.name);
            }

            names.Reverse();

            return string.Join("/", names);
        }

        private static void MarkSceneDirty(GameObject target)
        {
            var scene = target.scene;

            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private static EditorWindow ResolveWindow(CommandParameters p)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(x => x != null).ToList();

            if (p.instanceId != 0)
            {
                var byId = windows.FirstOrDefault(x => x.GetInstanceID() == p.instanceId);

                if (byId == null)
                {
                    throw new ArgumentException($"No editor window with instanceId {p.instanceId}.");
                }

                return byId;
            }

            if (!string.IsNullOrEmpty(p.windowType))
            {
                var byType = windows.FirstOrDefault(x =>
                    x.GetType().Name == p.windowType || x.GetType().FullName == p.windowType);

                if (byType == null)
                {
                    throw new ArgumentException($"No open editor window of type {p.windowType}.");
                }

                return byType;
            }

            if (!string.IsNullOrEmpty(p.windowTitle))
            {
                var byTitle = windows.FirstOrDefault(x => x.titleContent.text == p.windowTitle);

                if (byTitle == null)
                {
                    throw new ArgumentException($"No open editor window titled '{p.windowTitle}'.");
                }

                return byTitle;
            }

            throw new ArgumentException("dropMode 'window' needs windowType, windowTitle or instanceId.");
        }

        private static bool SendDragEvent(EditorWindow window, EventType type, Vector2 point)
        {
            var evt = new Event
            {
                type = type,
                mousePosition = point,
                button = 0,
                clickCount = 0,
                delta = Vector2.zero,
            };

            // EditorWindow.SendEvent is internal, so it is reached through EditorToolBridge's reflection
            // helper rather than duplicated here.
            var delivered = EditorToolBridge.SendEvent(window, evt);
            EditorToolBridge.RepaintImmediate(window);

            return delivered;
        }

        private static bool ParseBool(string raw)
        {
            bool parsed;

            if (bool.TryParse(raw, out parsed))
            {
                return parsed;
            }

            return raw == "1";
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"{name} is required.");
            }

            return value;
        }
    }
}
