using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

#if UDONSHARP
using System;
using System.Reflection;
#endif

namespace VRCLightVolumes {
    [InitializeOnLoad]
    internal static class LightVolumeCookieAnimatorRegistry {
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
            LightVolumeCookieAnimator.OnAnimatorValidated += QueueRefresh;
            EditorApplication.delayCall += RefreshAll;
            EditorApplication.update += EditorUpdate;
            EditorApplication.hierarchyChanged += QueueRefresh;
            Undo.undoRedoPerformed += QueueRefresh;
        }

        // Periodically refreshes registrations after generated cookie IDs change.
        private static void EditorUpdate() {
            UpdateAnimatedCookies();

            if (EditorApplication.timeSinceStartup < _nextRefreshTime) return;
            _nextRefreshTime = EditorApplication.timeSinceStartup + 1.0;
            QueueRefresh();
        }

        // Queues a refresh for a specific animator validation event.
        private static void QueueRefresh(LightVolumeCookieAnimator animator) {
            MarkAnimatorDirty(animator);
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
                LightVolumeSetup setup = ResolveSetup(animator);
                if (setup == null || setup.LightVolumeManager == null) continue;
                if (!animator.isActiveAndEnabled || animator.SourceTexture == null || !animator.HasValidTargetCookie()) continue;
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

            LightVolumeSetup setup = ResolveSetup(animator);
            if (setup == null || setup.LightVolumeManager == null) return;

            LightVolumeCookieAnimator sharedAnimator = GetSharedAnimator(setup.LightVolumeManager, animator);
            LightVolumeCookieAnimator serializedSharedAnimator = sharedAnimator == animator ? null : sharedAnimator;
            bool wasSharedOwner = animator.SharedAnimator == null || animator.SharedAnimator == animator;
            bool sharedChanged = animator.SharedAnimator != serializedSharedAnimator;
            if (sharedChanged) {
                animator.SharedAnimator = serializedSharedAnimator;
                MarkAnimatorDirty(animator);
            }

            if (!animator.isActiveAndEnabled || animator.SourceTexture == null || !animator.HasValidTargetCookie()) {
                if (wasSharedOwner) setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));
                CopyProxyToUdonIfNeeded(animator, IsAnimatorDirty(animator));
                return;
            }

            Texture baseTexture = setup.LightVolumeManager.CustomTexturesBase != null ? setup.LightVolumeManager.CustomTexturesBase : setup.LightVolumeManager.CustomTextures;
            if (baseTexture == null) {
                if (wasSharedOwner) setup.UnregisterCookiePostProcessor(GetPostProcessor(animator, animator.RuntimeCookieArray));
                CopyProxyToUdonIfNeeded(animator, IsAnimatorDirty(animator));
                return;
            }

            RenderTexture oldRuntimeTexture = animator.RuntimeCookieArray;
            int depth = GetTextureDepth(baseTexture);
            RenderTextureFormat runtimeFormat = GetRuntimeFormat(setup.CookieFormat);
            bool changed = IsAnimatorDirty(animator) || animator.BaseCookieTexture != baseTexture || animator.CookieArrayWidth != baseTexture.width || animator.CookieArrayHeight != baseTexture.height || animator.CookieArrayDepth != depth || animator.RuntimeFormat != runtimeFormat;
            bool recreated = animator.ConfigureCookieArray(baseTexture, baseTexture.width, baseTexture.height, depth, runtimeFormat);
            changed = changed || recreated;
            int sourceDepth = GetTextureDepth(animator.SourceTexture);
            if (animator.SourceTextureDepth != sourceDepth) {
                animator.SourceTextureDepth = sourceDepth;
                changed = true;
            }

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

        // Returns the slice count for texture types used by the cookie animator.
        private static int GetTextureDepth(Texture texture) {
            if (texture == null) return 1;
            if (texture is Texture2DArray textureArray) return Mathf.Max(textureArray.depth, 1);
            if (texture is RenderTexture renderTexture) return Mathf.Max(renderTexture.volumeDepth, 1);
            if (texture is Cubemap) return 6;
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
                if (animator == null || !animator.isActiveAndEnabled || !animator.Animate || animator.SourceTexture == null) continue;
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
        // Draws the default inspector and derived cookie slice status.
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            if (targets.Length != 1) return;

            LightVolumeCookieAnimator animator = (LightVolumeCookieAnimator)target;
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(true)) {
                EditorGUILayout.ObjectField("Runtime Cookie Array", animator.RuntimeCookieArray, typeof(RenderTexture), false);
                EditorGUILayout.IntField("Cookie Array Depth", animator.CookieArrayDepth);
                EditorGUILayout.IntField("Source Texture Depth", animator.SourceTextureDepth);
            }

            if (!animator.HasValidTargetCookie()) {
                EditorGUILayout.HelpBox("Target light does not use a custom cookie or cubemap texture.", MessageType.Info);
            }
        }
    }
}
