using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Collections.Generic;

namespace VRCLightVolumes {
    [CustomEditor(typeof(LightVolumeSetup))]
    public class LightVolumeSetupEditor : Editor {

        private SerializedProperty _volumesProp;
        private SerializedProperty _weightsProp;
        private ReorderableList _lightVolumesList;

        private SerializedProperty _pointLightVolumesProp;
        private ReorderableList _pointLightVolumesList;

        private LightVolumeSetup _lightVolumeSetup;

        private bool _isMultipleInstancesError = false;
        private static readonly GUIContent _cookieResolutionContent = new GUIContent("Cookie Resolution", "Resolution used for point light cookie, LUT and cubemap projection textures.");
        private static readonly GUIContent _cookieFormatContent = new GUIContent("Cookie Format", "Texture format used for point light cookie, LUT and cubemap projection textures.");
        private static readonly GUIContent _shadowResolutionContent = new GUIContent("Shadow Resolution", "Resolution used for per-light shadow maps.");
        private static readonly GUIContent _shadowFormatContent = new GUIContent("Shadow Format", "Texture format used for per-light shadow maps. RHalf is smaller, RFloat is more precise.");
        private static readonly GUIContent _brightnessCutoffContent = new GUIContent("Brightness Cutoff", "The minimum brightness at a point due to lighting from a Point Light Volume, before the light is culled.");

        private void OnEnable() {

            int managersCount = FindObjectsByType<LightVolumeManager>(FindObjectsSortMode.None).Length;
            _isMultipleInstancesError = managersCount > 1;

            _lightVolumeSetup = (LightVolumeSetup)target;

            _volumesProp = serializedObject.FindProperty("LightVolumes");
            _weightsProp = serializedObject.FindProperty("LightVolumesWeights");

            // ============ LIGHT VOLUMES LIST ===============

            _lightVolumesList = new ReorderableList(
                serializedObject,
                _volumesProp,
                true, // draggable
                true, // displayHeader
                false, // displayAddButton
                false  // displayRemoveButton
            );

            // Drawing header
            _lightVolumesList.drawHeaderCallback = (Rect rect) => {

                float totalWidth = rect.width;
                float availableWidth = totalWidth - 15f - 4f;
                float weightWidth = 42f;
                float space = 5f;
                float volumeWidth = availableWidth - weightWidth - space;
                float xOffset = rect.x + 15f;

                var headerCountStyle = new GUIStyle(EditorStyles.numberField) {
                    alignment = TextAnchor.MiddleCenter,
                    contentOffset = Vector2.zero,
                    fixedHeight = EditorGUIUtility.singleLineHeight - 3,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0),
                    fontSize = 11
                };
                headerCountStyle.normal.textColor = EditorStyles.label.normal.textColor;

                Rect volumeHeaderRect = new Rect(xOffset, rect.y, volumeWidth, EditorGUIUtility.singleLineHeight);
                Rect weightHeaderRect = new Rect(xOffset + volumeWidth + space, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);
                Rect fieldRect = new Rect(xOffset + 96, rect.y + 2, 32, rect.height);

                GUIContent title = new GUIContent($"Light Volumes ({_lightVolumeSetup.LightVolumes.Count})");
                title.tooltip = "Max 32 can be visible on scene at the same time.";
                EditorGUI.LabelField(volumeHeaderRect, title);
                EditorGUI.LabelField(weightHeaderRect, "Weight");

                EventType eventType = Event.current.type;
                if (eventType == EventType.DragUpdated || eventType == EventType.DragPerform) {
                    if (!rect.Contains(Event.current.mousePosition)) return;
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (eventType == EventType.DragPerform) {
                        DragAndDrop.AcceptDrag();
                        for (int i = 0; i < DragAndDrop.objectReferences.Length; i++) {
                            GameObject go = (GameObject)DragAndDrop.objectReferences[i];
                            if (go.TryGetComponent(out LightVolume volume)) {
                                int newIndex = _volumesProp.arraySize;
                                if (newIndex == 256) break;
                                _volumesProp.arraySize++;
                                _weightsProp.arraySize = _volumesProp.arraySize;
                                _volumesProp.GetArrayElementAtIndex(newIndex).objectReferenceValue = volume;
                                _weightsProp.GetArrayElementAtIndex(newIndex).floatValue = 0;
                            }
                        }
                        Event.current.Use();
                    }
                }

            };

            // Drawing each element
            _lightVolumesList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {

                if (index < 0 || index >= _volumesProp.arraySize || index >= _weightsProp.arraySize) return;

                SerializedProperty volumeElement = _volumesProp.GetArrayElementAtIndex(index);
                SerializedProperty weightElement = _weightsProp.GetArrayElementAtIndex(index);

                rect.y += 2; // Top padding
                float totalWidth = rect.width;
                float weightWidth = 45f; // Weight width
                float space = 5f;        // Spacing

                Rect iconRect = new Rect(rect.x, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect volumeRect = new Rect(rect.x + 24, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect weightRect = new Rect(rect.x + totalWidth - weightWidth, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);

                if (volumeElement.objectReferenceValue != null && volumeElement.objectReferenceValue.GetType() == typeof(LightVolume)) {
                    var volume = (LightVolume)volumeElement.objectReferenceValue;
                    GUIContent icon = volume.Additive ? EditorGUIUtility.IconContent("d_LightProbes Icon") : EditorGUIUtility.IconContent("d_PreMatLight1@2x");
                    icon.tooltip = volume.Additive ? "Additive Volume" : "Regular Volume";
                    EditorGUI.LabelField(iconRect, icon);
                }

                EditorGUI.LabelField(volumeRect, volumeElement.objectReferenceValue != null ? volumeElement.objectReferenceValue.name : "None");
                EditorGUI.PropertyField(weightRect, weightElement, GUIContent.none);

            };

            // On Moving element around
            _lightVolumesList.onReorderCallbackWithDetails = (ReorderableList list, int oldIndex, int newIndex) => {
                if (oldIndex >= 0 && oldIndex < _weightsProp.arraySize && newIndex >= 0 && newIndex < _weightsProp.arraySize) {
                    _weightsProp.MoveArrayElement(oldIndex, newIndex);
                } else if (_weightsProp.arraySize != _volumesProp.arraySize) {
                    // In case of a sync error
                    _weightsProp.arraySize = _volumesProp.arraySize;
                    EditorUtility.SetDirty(target);
                }
                _lightVolumeSetup.SyncUdonScript();
            };

            // On Click on element
            _lightVolumesList.onSelectCallback = (ReorderableList list) => {
                SerializedProperty volumeElement = _volumesProp.GetArrayElementAtIndex(list.index);
                LightVolume volume = volumeElement.objectReferenceValue as LightVolume;
                if (volume != null) EditorGUIUtility.PingObject(volume.gameObject);
            };

            // ================ POINT LIGHT VOLUMES LIST =============

            _pointLightVolumesProp = serializedObject.FindProperty("PointLightVolumes");

            _pointLightVolumesList = new ReorderableList(
                serializedObject,
                _pointLightVolumesProp,
                true, // draggable
                true, // displayHeader
                false, // displayAddButton
                false  // displayRemoveButton
            );

            // Drawing header
            _pointLightVolumesList.drawHeaderCallback = (Rect rect) => {

                float totalWidth = rect.width;
                float availableWidth = totalWidth - 15f - 4f;
                float weightWidth = 42f;
                float space = 5f;
                float volumeWidth = availableWidth - weightWidth - space;
                float xOffset = rect.x + 15f;

                Rect volumeHeaderRect = new Rect(xOffset, rect.y, volumeWidth, EditorGUIUtility.singleLineHeight);

                GUIContent title = new GUIContent($"Point Light Volumes ({_lightVolumeSetup.PointLightVolumes.Count})");
                title.tooltip = "Max 128 can be visible on scene at the same time.";
                EditorGUI.LabelField(volumeHeaderRect, title);

            };

            // Drawing each element
            _pointLightVolumesList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {

                if (index < 0 || index >= _pointLightVolumesProp.arraySize) return;

                SerializedProperty volumeElement = _pointLightVolumesProp.GetArrayElementAtIndex(index);

                rect.y += 2; // Top padding
                float totalWidth = rect.width;
                float weightWidth = 45f; // Weight width
                float space = 5f;        // Spacing

                Rect iconRect = new Rect(rect.x, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect volumeRect = new Rect(rect.x + 24, rect.y, totalWidth - weightWidth - space, EditorGUIUtility.singleLineHeight);
                Rect weightRect = new Rect(rect.x + totalWidth - weightWidth, rect.y, weightWidth, EditorGUIUtility.singleLineHeight);

                if (volumeElement.objectReferenceValue != null && volumeElement.objectReferenceValue.GetType() == typeof(PointLightVolume)) {
                    var volume = (PointLightVolume)volumeElement.objectReferenceValue;
                    GUIContent icon; 
                    if(volume.Type == PointLightVolume.LightType.SpotLight) {
                        icon = EditorGUIUtility.IconContent("d_Spotlight Icon");
                        icon.tooltip = "Spot Light Volume";
                    } else if (volume.Type == PointLightVolume.LightType.AreaLight) {
                        icon = EditorGUIUtility.IconContent("d_AreaLight Icon");
                        icon.tooltip = "Area Light Volume";
                    } else {
                        icon = EditorGUIUtility.IconContent("d_Light Icon");
                        icon.tooltip = "Point Light Volume";
                    }
                    EditorGUI.LabelField(iconRect, icon);
                }

                EditorGUI.LabelField(volumeRect, volumeElement.objectReferenceValue != null ? volumeElement.objectReferenceValue.name : "None");

            };

            // On Moving element around
            _pointLightVolumesList.onReorderCallbackWithDetails = (ReorderableList list, int oldIndex, int newIndex) => {
                _lightVolumeSetup.SyncUdonScript();
            };

            // On Click on element
            _pointLightVolumesList.onSelectCallback = (ReorderableList list) => {
                SerializedProperty volumeElement = _pointLightVolumesProp.GetArrayElementAtIndex(list.index);
                PointLightVolume volume = volumeElement.objectReferenceValue as PointLightVolume;
                if (volume != null) EditorGUIUtility.PingObject(volume.gameObject);
            };

        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            if (_volumesProp.arraySize != _weightsProp.arraySize) {
                _weightsProp.arraySize = _volumesProp.arraySize;
                for (int i = 0; i < _volumesProp.arraySize; i++) {
                    if (i >= _weightsProp.arraySize - (_volumesProp.arraySize - _weightsProp.arraySize)) {
                        SerializedProperty weightElement = _weightsProp.GetArrayElementAtIndex(i);
                        weightElement.floatValue = i;
                    }
                }
                EditorUtility.SetDirty(target);
            }

            GUILayout.Space(10);

            if (LVUtils.IsInPrefabAsset(_lightVolumeSetup)) {
                EditorGUILayout.HelpBox("This component is part of a prefab asset (not in the scene). Please, use one that is placed on your scene!", MessageType.Warning);
                GUILayout.Space(10);
            } else if (_isMultipleInstancesError) {
                EditorGUILayout.HelpBox("Multiple Light Volume Managers detected in the scene. Please ensure only one is active to avoid unexpected behavior!", MessageType.Error);
                GUILayout.Space(10);
            }

            ulong dataBytes = GetLightVolumeDataBytes();
            GUILayout.Label($"Data size in VRAM: {SizeInVRAM(dataBytes)} MB");
            GUILayout.Label($"Data size in bundle: {SizeInBundle(dataBytes)} MB (Approximately)");

            GUILayout.Space(10);

            List<string> hiddenFields = new List<string>() { "m_Script", "LightVolumes", "PointLightVolumes", "LightVolumesWeights", "LightVolumeAtlas", "LightVolumeDataList", "LightVolumeManager", "_bakingModePrev", "IsLegacyUVWConverted" };
            hiddenFields.Add("CookieResolution");
            hiddenFields.Add("CookieFormat");
            hiddenFields.Add("BrightnessCutoff");
            hiddenFields.Add("ShadowResolution");
            hiddenFields.Add("ShadowFormat");
            hiddenFields.Add("AtlasPostProcessors");
            int plvCount = _lightVolumeSetup.PointLightVolumes.Count;
            bool isShadow = false;
            bool isShadowBatchBake = false;
            for (int i = 0; i < plvCount; i++) {
                PointLightVolume pointLightVolume = _lightVolumeSetup.PointLightVolumes[i];
                if (pointLightVolume == null) continue;
                if (pointLightVolume.ShadowMap != null || pointLightVolume.RebakeShadows) {
                    isShadow = true;
                }
                if (pointLightVolume.RebakeShadows) {
                    isShadowBatchBake = true;
                }
                if (isShadow && isShadowBatchBake) {
                    break;
                }
            }

            if (_lightVolumeSetup.LightVolumes.Count > 0)
                _lightVolumesList.DoLayoutList();

            if (plvCount > 0) {
                _pointLightVolumesList.DoLayoutList();
            }

            GUILayout.Space(-15);

            if (plvCount > 0) {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CookieResolution"), _cookieResolutionContent);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CookieFormat"), _cookieFormatContent);
                if (isShadow) {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ShadowResolution"), _shadowResolutionContent);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ShadowFormat"), _shadowFormatContent);
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BrightnessCutoff"), _brightnessCutoffContent);
            }

            if (_lightVolumeSetup.BakingMode != LightVolumeSetup.Baking.Bakery) {
                hiddenFields.Add("FixLightProbesL1");
                if (!_lightVolumeSetup.DilateInvalidProbes) {
                    hiddenFields.Add("DilationIterations");
                    hiddenFields.Add("DilationBackfaceBias");
                }
            } else {
                hiddenFields.Add("DilateInvalidProbes");
                hiddenFields.Add("DilationIterations");
                hiddenFields.Add("DilationBackfaceBias");
            }

            DrawPropertiesExcluding(serializedObject, hiddenFields.ToArray());

            GUILayout.Space(5);

            GUILayout.BeginHorizontal();

            if (_lightVolumeSetup.LightVolumes.Count > 0) {
                if (GUILayout.Button(new GUIContent("Pack Light Volumes", "Repacks Light Volumes 3D Atlas. Should be done manually if you added new volumes to your scene, or made some changes with their 3D textures."))) {
                    _lightVolumeSetup.GenerateAtlas();
                }
            }

            if (plvCount > 0) {
                GUILayout.Space(5);

                GUI.enabled = isShadowBatchBake;
                if (GUILayout.Button(new GUIContent("Bake Shadows", "Bakes shadow maps for all point, spot and area Light Volumes with Rebake Shadows enabled."))) {
                    _lightVolumeSetup.BakeShadowMaps();
                }
                GUI.enabled = true;
            }

            GUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();

        }

        // Calculates the total size of generated Light Volume textures in bytes.
        private ulong GetLightVolumeDataBytes() {
            if (_lightVolumeSetup.LightVolumeManager == null) return 0;

            LightVolumeManager manager = _lightVolumeSetup.LightVolumeManager;
            ulong bytes = 0;

            bytes += GetTextureBytes(manager.LightVolumeAtlasBase, 8);

            if (_lightVolumeSetup.AtlasPostProcessors != null) {
                for (int i = 0; i < _lightVolumeSetup.AtlasPostProcessors.Length; i++) {
                    bytes += GetTextureBytes(_lightVolumeSetup.AtlasPostProcessors[i].RT, 8);
                }
            }

            bytes += GetTextureBytes(manager.CustomTextures, GetCookieTextureFormatBytes(_lightVolumeSetup.CookieFormat));
            bytes += GetTextureBytes(manager.ShadowTextures, GetShadowTextureFormatBytes(_lightVolumeSetup.ShadowFormat));

            return bytes;
        }

        // Calculates texture size from dimensions and a known bytes-per-texel format.
        private ulong GetTextureBytes(Texture texture, int bytesPerTexel) {
            if (texture == null) return 0;

            int depth = 1;
            if (texture is Texture3D texture3D) {
                depth = texture3D.depth;
            } else if (texture is Texture2DArray textureArray) {
                depth = textureArray.depth;
            } else if (texture is CustomRenderTexture customRenderTexture) {
                depth = customRenderTexture.volumeDepth;
            } else if (texture is RenderTexture renderTexture) {
                depth = renderTexture.volumeDepth;
            } else if (texture is Cubemap) {
                depth = 6;
            }

            return (ulong)Mathf.Max(texture.width, 0) * (ulong)Mathf.Max(texture.height, 0) * (ulong)Mathf.Max(depth, 1) * (ulong)Mathf.Max(bytesPerTexel, 0);
        }

        // Returns bytes per pixel for point light custom texture arrays.
        private int GetCookieTextureFormatBytes(LightVolumeSetup.TextureArrayFormat format) {
            if (format == LightVolumeSetup.TextureArrayFormat.RGBA32) return 4;
            if (format == LightVolumeSetup.TextureArrayFormat.RGBAFloat) return 16;
            return 8;
        }

        // Returns bytes per pixel for shadow texture arrays.
        private int GetShadowTextureFormatBytes(LightVolumeSetup.ShadowTextureFormat format) {
            return format == LightVolumeSetup.ShadowTextureFormat.RFloat ? 4 : 2;
        }

        // Real size in VRAM.
        private string SizeInVRAM(ulong byteCount) {
            double mb = byteCount / (double)(1024 * 1024);
            return mb.ToString("0.00");
        }

        // Approximate size in Asset bundle.
        private string SizeInBundle(ulong byteCount) {
            double mb = byteCount * 0.315f / (double)(1024 * 1024);
            return mb.ToString("0.00");
        }

    }
}
