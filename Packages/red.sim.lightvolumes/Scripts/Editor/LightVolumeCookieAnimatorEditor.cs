using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

#if UDONSHARP
using System;
using System.Reflection;
#endif

namespace VRCLightVolumes {
    [InitializeOnLoad]
    internal static class LightVolumeCookieAnimatorRegistry {
        internal const string CubemapFaceMaterialPath = "Packages/red.sim.lightvolumes/Materials/LightVolumeCookieAnimatorCubemapFace.mat";

        private static bool _isRefreshQueued = false;
        private static double _nextRefreshTime = 0.0;
        private static LightVolumeCookieAnimator[] _animators = new LightVolumeCookieAnimator[0];
        private static readonly Dictionary<LightVolumeManager, LightVolumeCookieAnimator> _sharedAnimators = new Dictionary<LightVolumeManager, LightVolumeCookieAnimator>();
        private static readonly HashSet<LightVolumeCookieAnimator> _dirtyAnimators = new HashSet<LightVolumeCookieAnimator>();
#if UDONSHARP
        private static bool _isCopyProxyMethodCached = false;
        private static MethodInfo _copyProxyToUdonMethod;
#endif

        // Subscribes editor callbacks that keep cookie animators registered.
        static LightVolumeCookieAnimatorRegistry() {
            LightVolumeCookieAnimator.OnAnimatorValidated += RequestRefresh;
            EditorApplication.delayCall += RefreshAll;
            EditorApplication.update += EditorUpdate;
            EditorApplication.hierarchyChanged += QueueRefresh;
            Undo.undoRedoPerformed += QueueRefresh;
        }

        // Queues a refresh for a changed animator.
        public static void RequestRefresh(LightVolumeCookieAnimator animator) {
            MarkAnimatorDirty(animator);
            QueueRefresh();
        }

        // Periodically refreshes registrations after generated cookie IDs change.
        private static void EditorUpdate() {
            UpdateAnimatedCookies();

            if (EditorApplication.timeSinceStartup < _nextRefreshTime) return;
            _nextRefreshTime = EditorApplication.timeSinceStartup + 1.0;
            QueueRefresh();
        }

        // Queues a delayed refresh to avoid editing serialized data during validation.
        private static void QueueRefresh() {
            if (_isRefreshQueued) return;
            _isRefreshQueued = true;
            EditorApplication.delayCall += RefreshAll;
        }

        // Refreshes all cookie animator registrations in open scenes.
        private static void RefreshAll() {
            _isRefreshQueued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            LightVolumeCookieAnimator[] animators = UnityEngine.Object.FindObjectsOfType<LightVolumeCookieAnimator>(true);
            _animators = animators;
            _sharedAnimators.Clear();
            for (int i = 0; i < animators.Length; i++) {
                LightVolumeCookieAnimator animator = animators[i];
                if (EnsureAnimatorArrayData(animator)) MarkAnimatorDirty(animator);

                LightVolumeSetup setup = ResolveSetup(animator);
                if (setup == null || setup.LightVolumeManager == null) continue;
                if (!animator.isActiveAndEnabled || !HasRenderableEntry(animator)) continue;
                if (!_sharedAnimators.ContainsKey(setup.LightVolumeManager)) {
                    _sharedAnimators.Add(setup.LightVolumeManager, animator);
                }
            }

            for (int i = 0; i < animators.Length; i++) {
                Refresh(animators[i]);
            }
        }

        // Registers or unregisters one animator against its LightVolumeSetup cookie post processor list.
        private static void Refresh(LightVolumeCookieAnimator animator) {
            if (animator == null) return;

            bool changed = EnsureAnimatorArrayData(animator) || IsAnimatorDirty(animator);
            LightVolumeSetup setup = ResolveSetup(animator);
            if (setup == null || setup.LightVolumeManager == null) return;

            LightVolumeCookieAnimator sharedAnimator = GetSharedAnimator(setup.LightVolumeManager, animator);
            LightVolumeCookieAnimator serializedSharedAnimator = sharedAnimator == animator ? null : sharedAnimator;
            bool wasSharedOwner = animator.SharedAnimator == null || animator.SharedAnimator == animator;
            bool sharedChanged = animator.SharedAnimator != serializedSharedAnimator;
            if (sharedChanged) {
                animator.SharedAnimator = serializedSharedAnimator;
                changed = true;
            }

            if (!animator.isActiveAndEnabled || !HasRenderableEntry(animator)) {
                if (wasSharedOwner) setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));
                CopyProxyToUdonIfNeeded(animator, changed);
                return;
            }

            Texture baseTexture = setup.LightVolumeManager.CustomTexturesBase != null ? setup.LightVolumeManager.CustomTexturesBase : setup.LightVolumeManager.CustomTextures;
            if (baseTexture == null) {
                if (wasSharedOwner) setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));
                CopyProxyToUdonIfNeeded(animator, changed);
                return;
            }

            RenderTexture oldRuntimeTexture = animator.RuntimeCookieArray;
            int depth = GetTextureDepth(baseTexture);
            RenderTextureFormat runtimeFormat = GetRuntimeFormat(setup.CookieFormat);
            changed = changed || animator.BaseCookieTexture != baseTexture || animator.CookieArrayWidth != baseTexture.width || animator.CookieArrayHeight != baseTexture.height || animator.CookieArrayDepth != depth || animator.RuntimeFormat != runtimeFormat;
            bool recreated = animator.ConfigureCookieArray(baseTexture, baseTexture.width, baseTexture.height, depth, runtimeFormat);
            changed = changed || recreated;

            if (recreated && oldRuntimeTexture != null) {
                setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, oldRuntimeTexture));
            }

            if (sharedAnimator != animator) {
                if (wasSharedOwner) setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));
                CopyProxyToUdonIfNeeded(animator, changed);
                return;
            }

            setup.RegisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));

            CopyProxyToUdonIfNeeded(animator, changed);
        }

        // Builds a callback post processor descriptor for this animator.
        private static LightVolumeSetup.PostProcessor GetPostProcessor(LightVolumeCookieAnimator animator, RenderTexture renderTexture) {
            return new LightVolumeSetup.PostProcessor {
                RT = renderTexture,
                UpdateWithInput = animator.UpdateCookiePostProcessor
            };
        }

        // Finds LightVolumeSetup through an assigned manager or scene fallback.
        private static LightVolumeSetup ResolveSetup(LightVolumeCookieAnimator animator) {
            if (animator.LightVolumeManager == null) {
                LightVolumeManager foundManager = UnityEngine.Object.FindObjectOfType<LightVolumeManager>();
                if (foundManager != null) {
                    animator.LightVolumeManager = foundManager;
                    MarkAnimatorDirty(animator);
                }
            }

            if (animator.LightVolumeManager != null && animator.LightVolumeManager.TryGetComponent(out LightVolumeSetup setup)) {
                return setup;
            }

            return UnityEngine.Object.FindObjectOfType<LightVolumeSetup>();
        }

        // Gets the shared runtime array owner for this manager.
        private static LightVolumeCookieAnimator GetSharedAnimator(LightVolumeManager manager, LightVolumeCookieAnimator fallback) {
            if (manager != null && _sharedAnimators.TryGetValue(manager, out LightVolumeCookieAnimator sharedAnimator)) {
                return sharedAnimator;
            }
            return fallback;
        }

        // Returns true when at least one entry has both a source texture and a supported target.
        private static bool HasRenderableEntry(LightVolumeCookieAnimator animator) {
            int count = GetAnimationEntryCount(animator);
            for (int i = 0; i < count; i++) {
                if (GetEntrySourceTexture(animator, i) == null) continue;
                if (IsSupportedTargetCookie(GetEntryTarget(animator, i))) return true;
            }
            return false;
        }

        // Normalizes hidden runtime data after legacy fields, source textures or list sizes change.
        private static bool EnsureAnimatorArrayData(LightVolumeCookieAnimator animator) {
            if (animator == null) return false;

            bool changed = false;
            if (animator.TargetPointLightVolumes == null) {
                animator.TargetPointLightVolumes = new PointLightVolumeInstance[0];
                changed = true;
            }
            if (animator.SourceTextures == null) {
                animator.SourceTextures = new Texture[0];
                changed = true;
            }
            if (animator.SourceSlices == null) {
                animator.SourceSlices = new int[0];
                changed = true;
            }
            if (animator.CubemapFaceMaterial == null) {
                Material cubemapFaceMaterial = AssetDatabase.LoadAssetAtPath<Material>(CubemapFaceMaterialPath);
                if (cubemapFaceMaterial != null) {
                    animator.CubemapFaceMaterial = cubemapFaceMaterial;
                    changed = true;
                }
            }

            if (animator.TargetPointLightVolumes.Length == 0 && animator.SourceTextures.Length == 0 && (animator.TargetPointLightVolume != null || animator.SourceTexture != null)) {
                animator.TargetPointLightVolumes = new[] { animator.TargetPointLightVolume };
                animator.SourceTextures = new[] { animator.SourceTexture };
                animator.SourceSlices = new[] { 0 };
                changed = true;
            }

            int count = GetAnimationEntryCount(animator);
            if (animator.SourceSlices.Length != count) {
                animator.SourceSlices = new int[count];
                changed = true;
            }
            for (int i = 0; i < count; i++) {
                if (animator.SourceSlices[i] == 0) continue;
                animator.SourceSlices[i] = 0;
                changed = true;
            }

            if (animator.SourceTextureDepths == null || animator.SourceTextureDepths.Length != count) {
                animator.SourceTextureDepths = new int[count];
                changed = true;
            }

            for (int i = 0; i < count; i++) {
                int sourceDepth = GetTextureDepth(GetEntrySourceTexture(animator, i));
                if (animator.SourceTextureDepths[i] != sourceDepth) {
                    animator.SourceTextureDepths[i] = sourceDepth;
                    changed = true;
                }
            }

            int firstSourceDepth = count > 0 ? animator.SourceTextureDepths[0] : 1;
            if (animator.SourceTextureDepth != firstSourceDepth) {
                animator.SourceTextureDepth = firstSourceDepth;
                changed = true;
            }

            if (count > 0) {
                PointLightVolumeInstance firstTarget = GetEntryTarget(animator, 0);
                Texture firstSource = GetEntrySourceTexture(animator, 0);
                if (animator.TargetPointLightVolume != firstTarget) {
                    animator.TargetPointLightVolume = firstTarget;
                    changed = true;
                }
                if (animator.SourceTexture != firstSource) {
                    animator.SourceTexture = firstSource;
                    changed = true;
                }
                if (animator.SourceSlice != 0) {
                    animator.SourceSlice = 0;
                    changed = true;
                }
            }

            return changed;
        }

        // Returns the configured row count, including legacy single-entry fallback.
        private static int GetAnimationEntryCount(LightVolumeCookieAnimator animator) {
            int count = 0;
            if (animator.TargetPointLightVolumes != null && animator.TargetPointLightVolumes.Length > count) count = animator.TargetPointLightVolumes.Length;
            if (animator.SourceTextures != null && animator.SourceTextures.Length > count) count = animator.SourceTextures.Length;
            if (count > 0) return count;
            if (animator.TargetPointLightVolume != null || animator.SourceTexture != null) return 1;
            return 0;
        }

        // Returns one entry target from the synchronized arrays.
        private static PointLightVolumeInstance GetEntryTarget(LightVolumeCookieAnimator animator, int index) {
            if (animator.TargetPointLightVolumes != null && index < animator.TargetPointLightVolumes.Length) return animator.TargetPointLightVolumes[index];
            if (index == 0) return animator.TargetPointLightVolume;
            return null;
        }

        // Returns one entry source from the synchronized arrays.
        private static Texture GetEntrySourceTexture(LightVolumeCookieAnimator animator, int index) {
            if (animator.SourceTextures != null && index < animator.SourceTextures.Length) return animator.SourceTextures[index];
            if (index == 0) return animator.SourceTexture;
            return null;
        }

        // Returns true when the target light points at a replaceable cookie slot.
        private static bool IsSupportedTargetCookie(PointLightVolumeInstance targetLight) {
            if (targetLight == null) return false;
            if (!targetLight.IsCustomTexture()) return false;
            return targetLight.IsPointLight() || targetLight.IsSpotLight();
        }

        // Returns the slice count for texture types used by the cookie animator.
        private static int GetTextureDepth(Texture texture) {
            if (texture == null) return 1;
            if (texture.dimension == TextureDimension.Cube) return 6;
            if (texture is Texture2DArray textureArray) return Mathf.Max(textureArray.depth, 1);
            if (texture is RenderTexture renderTexture) return Mathf.Max(renderTexture.volumeDepth, 1);
            return 1;
        }

        // Converts the setup cookie format into a runtime render texture format.
        private static RenderTextureFormat GetRuntimeFormat(LightVolumeSetup.TextureArrayFormat format) {
            if (format == LightVolumeSetup.TextureArrayFormat.RGBAFloat) return RenderTextureFormat.ARGBFloat;
            if (format == LightVolumeSetup.TextureArrayFormat.RGBAHalf) return RenderTextureFormat.ARGBHalf;
            return RenderTextureFormat.ARGB32;
        }

        // Updates animated cookie slices continuously in edit mode.
        private static void UpdateAnimatedCookies() {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            for (int i = 0; i < _animators.Length; i++) {
                LightVolumeCookieAnimator animator = _animators[i];
                if (animator == null || !animator.isActiveAndEnabled || !animator.Animate || !HasRenderableEntry(animator)) continue;
                animator.UpdateAnimatedCookie();
            }
        }

        // Marks an animator proxy as needing Udon sync without storing editor-only fields on the UdonSharpBehaviour.
        private static void MarkAnimatorDirty(LightVolumeCookieAnimator animator) {
            if (animator == null) return;
            _dirtyAnimators.Add(animator);
        }

        // Returns true when an animator proxy changed through editor code.
        private static bool IsAnimatorDirty(LightVolumeCookieAnimator animator) {
            return animator != null && _dirtyAnimators.Contains(animator);
        }

        // Copies changed proxy values into the generated UdonBehaviour when UdonSharp is available.
        private static void CopyProxyToUdonIfNeeded(LightVolumeCookieAnimator animator, bool isDirty) {
            if (!isDirty) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            LVUtils.MarkDirty(animator);
#if UDONSHARP
            MethodInfo method = GetCopyProxyToUdonMethod();
            if (method != null) method.Invoke(null, new object[] { animator });
#endif
            _dirtyAnimators.Remove(animator);
        }

#if UDONSHARP
        // Finds UdonSharpEditorUtility.CopyProxyToUdon without creating a hard asmdef dependency.
        private static MethodInfo GetCopyProxyToUdonMethod() {
            if (_isCopyProxyMethodCached) return _copyProxyToUdonMethod;

            _isCopyProxyMethodCached = true;

            Type utilityType = Type.GetType("UdonSharpEditor.UdonSharpEditorUtility, UdonSharp.Editor");
            if (utilityType == null) return null;

            MethodInfo[] methods = utilityType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++) {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "CopyProxyToUdon" && parameters.Length == 1) {
                    _copyProxyToUdonMethod = method;
                    break;
                }
            }

            return _copyProxyToUdonMethod;
        }
#endif
    }

    [CustomEditor(typeof(LightVolumeCookieAnimator))]
    [CanEditMultipleObjects]
    internal class LightVolumeCookieAnimatorEditor : Editor {
        private const float WarningWidth = 22f;
        private const float ColumnSpacing = 4f;

        private SerializedProperty _lightVolumeManagerProperty;
        private SerializedProperty _targetPointLightVolumesProperty;
        private SerializedProperty _sourceTexturesProperty;
        private SerializedProperty _sourceSlicesProperty;
        private SerializedProperty _sourceTextureDepthsProperty;
        private SerializedProperty _targetPointLightVolumeProperty;
        private SerializedProperty _sourceTextureProperty;
        private SerializedProperty _sourceSliceProperty;
        private SerializedProperty _sourceTextureDepthProperty;
        private SerializedProperty _animateProperty;
        private SerializedProperty _cubemapFaceMaterialProperty;
        private ReorderableList _entriesList;
        private GUIContent _warningIconContent;

        // Caches serialized properties and builds the synchronized entries list.
        private void OnEnable() {
            _lightVolumeManagerProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.LightVolumeManager));
            _targetPointLightVolumesProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.TargetPointLightVolumes));
            _sourceTexturesProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceTextures));
            _sourceSlicesProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceSlices));
            _sourceTextureDepthsProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceTextureDepths));
            _targetPointLightVolumeProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.TargetPointLightVolume));
            _sourceTextureProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceTexture));
            _sourceSliceProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceSlice));
            _sourceTextureDepthProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.SourceTextureDepth));
            _animateProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.Animate));
            _cubemapFaceMaterialProperty = serializedObject.FindProperty(nameof(LightVolumeCookieAnimator.CubemapFaceMaterial));
            _warningIconContent = EditorGUIUtility.IconContent("console.warnicon.sml");

            _entriesList = new ReorderableList(serializedObject, _targetPointLightVolumesProperty, true, true, true, true);
            _entriesList.drawHeaderCallback = DrawEntriesHeader;
            _entriesList.drawElementCallback = DrawEntryElement;
            _entriesList.elementHeight = EditorGUIUtility.singleLineHeight + 6f;
            _entriesList.onAddCallback = AddEntry;
            _entriesList.onRemoveCallback = RemoveEntry;
            _entriesList.onReorderCallbackWithDetails = ReorderEntry;
        }

        // Draws the synchronized cookie animation list and runtime status.
        public override void OnInspectorGUI() {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_lightVolumeManagerProperty);
            EditorGUILayout.PropertyField(_animateProperty);

            if (serializedObject.isEditingMultipleObjects) {
                EditorGUILayout.HelpBox("Cookie target list editing is available for one animator at a time.", MessageType.Info);
                bool multiChanged = serializedObject.ApplyModifiedProperties();
                if (multiChanged) RequestRefreshForTargets();
                return;
            }

            NormalizeSerializedArrays();

            Rect listRect = GUILayoutUtility.GetRect(0f, _entriesList.GetHeight(), GUILayout.ExpandWidth(true));
            _entriesList.DoList(listRect);
            HandleEntriesDragAndDrop(listRect);

            bool changed = serializedObject.ApplyModifiedProperties();
            if (changed) RequestRefreshForTargets();
        }

        // Draws list column labels.
        private void DrawEntriesHeader(Rect rect) {
            Rect[] columns = GetElementColumns(rect);
            EditorGUI.LabelField(columns[0], "Point Light");
            EditorGUI.LabelField(columns[1], "Source Texture");
        }

        // Draws one synchronized entry with warning state.
        private void DrawEntryElement(Rect rect, int index, bool isActive, bool isFocused) {
            NormalizeSerializedArrays();

            rect.y += 3f;
            rect.height = EditorGUIUtility.singleLineHeight;
            Rect[] columns = GetElementColumns(rect);

            string warning = GetEntryWarning(index);
            EditorGUI.PropertyField(columns[0], _targetPointLightVolumesProperty.GetArrayElementAtIndex(index), GUIContent.none);
            EditorGUI.PropertyField(columns[1], _sourceTexturesProperty.GetArrayElementAtIndex(index), GUIContent.none);

            if (!string.IsNullOrEmpty(warning)) {
                GUIContent warningContent = new GUIContent(_warningIconContent.image, warning);
                GUI.Label(columns[2], warningContent);
            }
        }

        // Adds one empty synchronized row.
        private void AddEntry(ReorderableList list) {
            InsertEntry(_targetPointLightVolumesProperty.arraySize, null, null);
            _entriesList.index = _targetPointLightVolumesProperty.arraySize - 1;
        }

        // Removes one synchronized row.
        private void RemoveEntry(ReorderableList list) {
            if (list.index < 0 || list.index >= _targetPointLightVolumesProperty.arraySize) return;
            DeleteArrayElement(_targetPointLightVolumesProperty, list.index);
            DeleteArrayElement(_sourceTexturesProperty, list.index);
            DeleteArrayElement(_sourceSlicesProperty, list.index);
            DeleteArrayElement(_sourceTextureDepthsProperty, list.index);
            list.index = Mathf.Clamp(list.index, 0, _targetPointLightVolumesProperty.arraySize - 1);
            SyncLegacyFirstEntry();
        }

        // Mirrors ReorderableList sorting into the parallel arrays.
        private void ReorderEntry(ReorderableList list, int oldIndex, int newIndex) {
            if (oldIndex == newIndex) return;
            _sourceTexturesProperty.MoveArrayElement(oldIndex, newIndex);
            _sourceSlicesProperty.MoveArrayElement(oldIndex, newIndex);
            _sourceTextureDepthsProperty.MoveArrayElement(oldIndex, newIndex);
            SyncLegacyFirstEntry();
        }

        // Normalizes synchronized array sizes and derived source depths.
        private void NormalizeSerializedArrays() {
            if (_cubemapFaceMaterialProperty.objectReferenceValue == null) {
                Material cubemapFaceMaterial = AssetDatabase.LoadAssetAtPath<Material>(LightVolumeCookieAnimatorRegistry.CubemapFaceMaterialPath);
                if (cubemapFaceMaterial != null) _cubemapFaceMaterialProperty.objectReferenceValue = cubemapFaceMaterial;
            }

            if (_targetPointLightVolumesProperty.arraySize == 0 && _sourceTexturesProperty.arraySize == 0 && (_targetPointLightVolumeProperty.objectReferenceValue != null || _sourceTextureProperty.objectReferenceValue != null)) {
                _targetPointLightVolumesProperty.arraySize = 1;
                _sourceTexturesProperty.arraySize = 1;
                _sourceSlicesProperty.arraySize = 1;
                _targetPointLightVolumesProperty.GetArrayElementAtIndex(0).objectReferenceValue = _targetPointLightVolumeProperty.objectReferenceValue;
                _sourceTexturesProperty.GetArrayElementAtIndex(0).objectReferenceValue = _sourceTextureProperty.objectReferenceValue;
                _sourceSlicesProperty.GetArrayElementAtIndex(0).intValue = 0;
            }

            int count = _targetPointLightVolumesProperty.arraySize;
            if (_sourceTexturesProperty.arraySize > count) count = _sourceTexturesProperty.arraySize;
            ResizeObjectArray(_targetPointLightVolumesProperty, count);
            ResizeObjectArray(_sourceTexturesProperty, count);
            ResizeIntArray(_sourceSlicesProperty, count, 0);
            ResizeIntArray(_sourceTextureDepthsProperty, count, 1);

            for (int i = 0; i < count; i++) {
                SerializedProperty sourceSliceProperty = _sourceSlicesProperty.GetArrayElementAtIndex(i);
                if (sourceSliceProperty.intValue != 0) sourceSliceProperty.intValue = 0;

                Texture sourceTexture = _sourceTexturesProperty.GetArrayElementAtIndex(i).objectReferenceValue as Texture;
                _sourceTextureDepthsProperty.GetArrayElementAtIndex(i).intValue = GetTextureDepth(sourceTexture);
            }

            SyncLegacyFirstEntry();
        }

        // Mirrors the first array element into hidden legacy fields for backwards compatibility.
        private void SyncLegacyFirstEntry() {
            if (_targetPointLightVolumesProperty.arraySize == 0) {
                _targetPointLightVolumeProperty.objectReferenceValue = null;
                _sourceTextureProperty.objectReferenceValue = null;
                _sourceSliceProperty.intValue = 0;
                _sourceTextureDepthProperty.intValue = 1;
                return;
            }

            _targetPointLightVolumeProperty.objectReferenceValue = _targetPointLightVolumesProperty.GetArrayElementAtIndex(0).objectReferenceValue;
            _sourceTextureProperty.objectReferenceValue = _sourceTexturesProperty.GetArrayElementAtIndex(0).objectReferenceValue;
            _sourceSliceProperty.intValue = 0;
            _sourceTextureDepthProperty.intValue = _sourceTextureDepthsProperty.GetArrayElementAtIndex(0).intValue;
        }

        // Inserts one synchronized entry.
        private void InsertEntry(int index, PointLightVolumeInstance targetLight, Texture sourceTexture) {
            int oldSize = _targetPointLightVolumesProperty.arraySize;
            int targetIndex = Mathf.Clamp(index, 0, oldSize);
            ResizeObjectArray(_targetPointLightVolumesProperty, oldSize + 1);
            ResizeObjectArray(_sourceTexturesProperty, oldSize + 1);
            ResizeIntArray(_sourceSlicesProperty, oldSize + 1, 0);
            ResizeIntArray(_sourceTextureDepthsProperty, oldSize + 1, 1);
            if (targetIndex < oldSize) {
                _targetPointLightVolumesProperty.MoveArrayElement(oldSize, targetIndex);
                _sourceTexturesProperty.MoveArrayElement(oldSize, targetIndex);
                _sourceSlicesProperty.MoveArrayElement(oldSize, targetIndex);
                _sourceTextureDepthsProperty.MoveArrayElement(oldSize, targetIndex);
            }

            _targetPointLightVolumesProperty.GetArrayElementAtIndex(targetIndex).objectReferenceValue = targetLight;
            _sourceTexturesProperty.GetArrayElementAtIndex(targetIndex).objectReferenceValue = sourceTexture;
            _sourceSlicesProperty.GetArrayElementAtIndex(targetIndex).intValue = 0;
            _sourceTextureDepthsProperty.GetArrayElementAtIndex(targetIndex).intValue = GetTextureDepth(sourceTexture);
            SyncLegacyFirstEntry();
        }

        // Resizes an object-reference array and clears newly created slots.
        private static void ResizeObjectArray(SerializedProperty arrayProperty, int size) {
            int oldSize = arrayProperty.arraySize;
            if (oldSize == size) return;
            arrayProperty.arraySize = size;
            for (int i = oldSize; i < size; i++) {
                arrayProperty.GetArrayElementAtIndex(i).objectReferenceValue = null;
            }
        }

        // Resizes an int array and initializes newly created slots.
        private static void ResizeIntArray(SerializedProperty arrayProperty, int size, int defaultValue) {
            int oldSize = arrayProperty.arraySize;
            if (oldSize == size) return;
            arrayProperty.arraySize = size;
            for (int i = oldSize; i < size; i++) {
                arrayProperty.GetArrayElementAtIndex(i).intValue = defaultValue;
            }
        }

        // Deletes an array element, including object-reference nulling semantics.
        private static void DeleteArrayElement(SerializedProperty arrayProperty, int index) {
            int oldSize = arrayProperty.arraySize;
            arrayProperty.DeleteArrayElementAtIndex(index);
            if (arrayProperty.arraySize == oldSize) arrayProperty.DeleteArrayElementAtIndex(index);
        }

        // Handles drag and drop of point lights, point light volume objects and textures onto the list.
        private void HandleEntriesDragAndDrop(Rect listRect) {
            Event currentEvent = Event.current;
            if (!listRect.Contains(currentEvent.mousePosition)) return;
            if (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform) return;

            List<PointLightVolumeInstance> draggedLights = new List<PointLightVolumeInstance>();
            List<Texture> draggedTextures = new List<Texture>();
            CollectDraggedObjects(draggedLights, draggedTextures);
            if (draggedLights.Count == 0 && draggedTextures.Count == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (currentEvent.type == EventType.DragPerform) {
                Undo.RecordObject(target, "Add Cookie Animation Targets");
                DragAndDrop.AcceptDrag();
                AddDraggedEntries(draggedLights, draggedTextures);
                RequestRefreshForTargets();
            }
            currentEvent.Use();
        }

        // Collects supported drag objects into light and texture lists.
        private static void CollectDraggedObjects(List<PointLightVolumeInstance> draggedLights, List<Texture> draggedTextures) {
            UnityEngine.Object[] draggedObjects = DragAndDrop.objectReferences;
            for (int i = 0; i < draggedObjects.Length; i++) {
                UnityEngine.Object draggedObject = draggedObjects[i];
                if (draggedObject is Texture texture) {
                    draggedTextures.Add(texture);
                    continue;
                }
                if (draggedObject is PointLightVolumeInstance pointLightVolumeInstance) {
                    draggedLights.Add(pointLightVolumeInstance);
                    continue;
                }
                if (draggedObject is PointLightVolume pointLightVolume && pointLightVolume.PointLightVolumeInstance != null) {
                    draggedLights.Add(pointLightVolume.PointLightVolumeInstance);
                    continue;
                }
                if (draggedObject is GameObject gameObject) {
                    PointLightVolumeInstance instance = gameObject.GetComponent<PointLightVolumeInstance>();
                    if (instance != null) {
                        draggedLights.Add(instance);
                        continue;
                    }
                    PointLightVolume pointLight = gameObject.GetComponent<PointLightVolume>();
                    if (pointLight != null && pointLight.PointLightVolumeInstance != null) draggedLights.Add(pointLight.PointLightVolumeInstance);
                }
            }
        }

        // Adds dragged light and texture pairs to the synchronized list.
        private void AddDraggedEntries(List<PointLightVolumeInstance> draggedLights, List<Texture> draggedTextures) {
            if (draggedLights.Count == 0) {
                for (int i = 0; i < draggedTextures.Count; i++) {
                    InsertEntry(_targetPointLightVolumesProperty.arraySize, null, draggedTextures[i]);
                }
                return;
            }

            for (int i = 0; i < draggedLights.Count; i++) {
                Texture sourceTexture = null;
                if (i < draggedTextures.Count) sourceTexture = draggedTextures[i];
                else if (draggedTextures.Count == 1) sourceTexture = draggedTextures[0];
                InsertEntry(_targetPointLightVolumesProperty.arraySize, draggedLights[i], sourceTexture);
            }
        }

        // Returns warning text for one entry, or null if it can render.
        private string GetEntryWarning(int index) {
            PointLightVolumeInstance targetLight = _targetPointLightVolumesProperty.GetArrayElementAtIndex(index).objectReferenceValue as PointLightVolumeInstance;
            Texture sourceTexture = _sourceTexturesProperty.GetArrayElementAtIndex(index).objectReferenceValue as Texture;
            if (targetLight == null || sourceTexture == null) return null;
            if (targetLight.IsAreaLight()) return "Area lights do not use cookie texture slices.";
            if (!targetLight.IsCustomTexture()) return "This light does not use a custom cookie/cubemap texture.";
            if (!targetLight.IsPointLight() && !targetLight.IsSpotLight()) return "Only point and spot lights with custom cookies are supported.";
            if (sourceTexture.dimension == TextureDimension.Tex3D) return "3D source textures are not supported for cookie slice blitting.";

            int cookieID = GetCookieID(targetLight);
            for (int i = 0; i < index; i++) {
                PointLightVolumeInstance otherLight = _targetPointLightVolumesProperty.GetArrayElementAtIndex(i).objectReferenceValue as PointLightVolumeInstance;
                if (otherLight == null) continue;
                if (otherLight == targetLight) return "This Point Light Volume is already listed above.";
                if (!IsSupportedTargetCookie(otherLight)) continue;
                if (GetCookieID(otherLight) == cookieID) return "Another listed light uses the same cookie ID.";
            }

            return null;
        }

        // Returns layout columns for one row or header.
        private static Rect[] GetElementColumns(Rect rect) {
            Rect[] columns = new Rect[3];
            float remainingWidth = rect.width - WarningWidth - ColumnSpacing * 3f;
            float objectWidth = Mathf.Max(remainingWidth * 0.5f, 80f);
            columns[0] = new Rect(rect.x, rect.y, objectWidth, rect.height);
            columns[1] = new Rect(columns[0].xMax + ColumnSpacing, rect.y, objectWidth, rect.height);
            columns[2] = new Rect(columns[1].xMax + ColumnSpacing, rect.y, WarningWidth, rect.height);
            return columns;
        }

        // Returns true when the target light points at a replaceable cookie slot.
        private static bool IsSupportedTargetCookie(PointLightVolumeInstance targetLight) {
            if (targetLight == null) return false;
            if (!targetLight.IsCustomTexture()) return false;
            return targetLight.IsPointLight() || targetLight.IsSpotLight();
        }

        // Returns the positive cookie ID encoded in PointLightVolumeInstance.CustomID.
        private static int GetCookieID(PointLightVolumeInstance targetLight) {
            if (targetLight == null) return int.MaxValue;
            return -(int)targetLight.CustomID - 1;
        }

        // Returns texture depth or cubemap face count for source slice validation.
        private static int GetTextureDepth(Texture texture) {
            if (texture == null) return 1;
            if (texture.dimension == TextureDimension.Cube) return 6;
            if (texture is Texture2DArray textureArray) return Mathf.Max(textureArray.depth, 1);
            if (texture is RenderTexture renderTexture) return Mathf.Max(renderTexture.volumeDepth, 1);
            return 1;
        }

        // Requests registry refresh for all edited animator targets.
        private void RequestRefreshForTargets() {
            for (int i = 0; i < targets.Length; i++) {
                LightVolumeCookieAnimator animator = targets[i] as LightVolumeCookieAnimator;
                if (animator != null) LightVolumeCookieAnimatorRegistry.RequestRefresh(animator);
            }
        }
    }
}
