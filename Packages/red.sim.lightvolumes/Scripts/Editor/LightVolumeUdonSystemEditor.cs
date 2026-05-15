using UnityEditor;

#if UDONSHARP
using System;
using System.Reflection;
#endif

namespace VRCLightVolumes {
    public abstract class LightVolumeUdonSystemEditorBase : Editor {

#if UDONSHARP
        private const string _infoMessage = "This is a system Udon script. Do not change its values manually - they are fully controlled by the script above.";
        private static bool _isUdonSharpHeaderMethodCached = false;
        private static MethodInfo _drawUdonSharpHeaderMethod;
#else
        private const string _infoMessage = "This is a system runtime script. Do not change its values manually - they are fully controlled by the script above.";
#endif

        // Draws a lightweight system-script notice before the regular serialized fields.
        public override void OnInspectorGUI() {
#if UDONSHARP
            if (TryDrawUdonSharpHeader(out bool shouldStopDrawing) && shouldStopDrawing)
                return;
#endif

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_infoMessage, MessageType.Info);
            EditorGUILayout.Space();

#if UDONSHARP
            DrawUdonSharpSerializedFields();
#else
            DrawDefaultInspector();
#endif
        }

#if UDONSHARP
        // Draws regular UdonSharp proxy fields after the UdonSharp header.
        private void DrawUdonSharpSerializedFields() {
            serializedObject.Update();

            SerializedProperty property = serializedObject.GetIterator();
            if (property.NextVisible(true)) {
                do {
                    if (property.propertyPath == "m_Script") continue;

                    EditorGUILayout.PropertyField(property, true);
                } while (property.NextVisible(false));
            }

            serializedObject.ApplyModifiedProperties();
        }

        // Draws the UdonSharp header through reflection so this editor stays usable without UdonSharp.Editor installed.
        private bool TryDrawUdonSharpHeader(out bool shouldStopDrawing) {
            shouldStopDrawing = false;

            MethodInfo method = GetDrawUdonSharpHeaderMethod();
            if (method == null) return false;

            object result = method.Invoke(null, new object[] { targets, false, true });
            shouldStopDrawing = result is bool stopDrawing && stopDrawing;
            return true;
        }

        // Finds UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader without creating a hard asmdef dependency.
        private static MethodInfo GetDrawUdonSharpHeaderMethod() {
            if (_isUdonSharpHeaderMethodCached) return _drawUdonSharpHeaderMethod;

            _isUdonSharpHeaderMethodCached = true;

            Type guiType = Type.GetType("UdonSharpEditor.UdonSharpGUI, UdonSharp.Editor");
            if (guiType == null) return null;

            _drawUdonSharpHeaderMethod = guiType.GetMethod("DrawDefaultUdonSharpBehaviourHeader", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(UnityEngine.Object[]), typeof(bool), typeof(bool) }, null);

            return _drawUdonSharpHeaderMethod;
        }
#endif

    }

    [CanEditMultipleObjects]
    [CustomEditor(typeof(PointLightVolumeInstance))]
    public class PointLightVolumeInstanceEditor : LightVolumeUdonSystemEditorBase { }

    [CanEditMultipleObjects]
    [CustomEditor(typeof(LightVolumeInstance))]
    public class LightVolumeInstanceEditor : LightVolumeUdonSystemEditorBase { }

    [CanEditMultipleObjects]
    [CustomEditor(typeof(LightVolumeManager))]
    public class LightVolumeManagerEditor : LightVolumeUdonSystemEditorBase { }
}
