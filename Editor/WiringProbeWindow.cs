using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Draws exactly one serialized property so a real drag-and-drop can be aimed at it.
    ///
    /// The Inspector will not say where it drew a given property: its layout dump carries rects and
    /// style names but no labels, and a custom inspector can lay a component out in any order it
    /// likes. Guessing a coordinate there means dropping onto whatever happens to be at that y.
    ///
    /// This window draws the one field the caller named and remembers the rect it drew it in, so the
    /// drop lands on that field and nothing else. The field itself is an ordinary
    /// <see cref="EditorGUILayout.PropertyField(SerializedProperty, bool, GUILayoutOption[])"/>, so a
    /// drop here runs the same code a drop in the Inspector would - type filtering, Undo, and
    /// ApplyModifiedProperties included.
    /// </summary>
    internal sealed class WiringProbeWindow : EditorWindow
    {
        private Object probeTarget;
        private string probePropertyPath;
        private SerializedObject serialized;

        internal Rect FieldRect { get; private set; }

        internal bool HasFieldRect { get; private set; }

        internal string LastError { get; private set; }

        /// <summary>
        /// Opens (or reuses) the probe on one property and lays it out once, so the caller can read
        /// <see cref="FieldRect"/> immediately after this returns.
        /// </summary>
        internal static WiringProbeWindow Open(Object target, string propertyPath)
        {
            var window = GetWindow<WiringProbeWindow>(true, "MCP Wiring Probe", false);

            window.probeTarget = target;
            window.probePropertyPath = propertyPath;
            window.serialized = null;
            window.HasFieldRect = false;
            window.LastError = null;

            // Small but not tiny: a PropertyField needs real width or the drop area is a few pixels wide.
            window.position = new Rect(window.position.x, window.position.y, 420f, 90f);
            window.ShowUtility();

            return window;
        }

        private void OnGUI()
        {
            if (probeTarget == null)
            {
                LastError = "probe target is null";
                EditorGUILayout.LabelField(LastError);
                return;
            }

            if (serialized == null || serialized.targetObject != probeTarget)
            {
                serialized = new SerializedObject(probeTarget);
            }

            serialized.Update();

            var property = serialized.FindProperty(probePropertyPath);

            if (property == null)
            {
                LastError = $"no serialized property '{probePropertyPath}' on {probeTarget.GetType().Name}";
                EditorGUILayout.LabelField(LastError);
                return;
            }

            LastError = null;

            EditorGUILayout.LabelField($"{probeTarget.name} . {probePropertyPath}", EditorStyles.miniLabel);
            EditorGUILayout.PropertyField(property, GUIContent.none, true);

            // The rect is only final once a repaint has run; a layout pass reports zero-size rects.
            if (Event.current.type == EventType.Repaint)
            {
                FieldRect = GUILayoutUtility.GetLastRect();
                HasFieldRect = FieldRect.width > 0f && FieldRect.height > 0f;
            }

            serialized.ApplyModifiedProperties();
        }
    }
}
