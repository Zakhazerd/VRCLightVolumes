#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRCLightVolumes {

    public static class PointLightDepthShadowBaker {

        private static class ShaderConstants {
            public static readonly int LightPositionID = Shader.PropertyToID("_VRCLV_LightPosition");

            public const string DepthShaderPath = "Packages/red.sim.lightvolumes/Shaders/Editor/PointLightDepthShadow.shader";
        }

        // Bakes a world-space radial depth cubemap from the light position.
        public static Cubemap BakeDepthCubemap(PointLightVolume pointLightVolume, int resolution, float farClip, string infoString = "") {
            if (pointLightVolume == null) return null;

            Shader depthShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderConstants.DepthShaderPath);
            if (depthShader == null) {
                Debug.LogError($"[PointLightDepthShadowBaker] Failed to load depth shadow shader at '{ShaderConstants.DepthShaderPath}'.", pointLightVolume);
                return null;
            }

            int safeResolution = Mathf.Clamp(resolution, 16, 2048);
            float safeFarClip = Mathf.Max(farClip, 0.0001f);

            RenderTexture oldActive = RenderTexture.active;
            RenderTexture cubeRT = null;
            Texture2D temp = null;
            GameObject cameraObject = null;
            MeshRenderer[] renderers = null;
            bool[] previousForceRenderingOff = null;

            try {
                string title = "Depth Shadow Baking " + infoString;
                EditorUtility.DisplayProgressBar(title, "Preparing static occluders...", 0.1f);

                renderers = ApplyOccluderFilter(out previousForceRenderingOff);

                cubeRT = new RenderTexture(safeResolution, safeResolution, 24, RenderTextureFormat.RHalf) {
                    dimension = TextureDimension.Cube,
                    useMipMap = false,
                    autoGenerateMips = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                cubeRT.Create();

                cameraObject = new GameObject("VRCLV Depth Shadow Camera") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.transform.position = pointLightVolume.transform.position;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(safeFarClip, safeFarClip, safeFarClip, safeFarClip);
                camera.nearClipPlane = Mathf.Min(0.01f, safeFarClip * 0.5f);
                camera.farClipPlane = safeFarClip;
                camera.fieldOfView = 90f;
                camera.aspect = 1f;
                camera.renderingPath = RenderingPath.Forward;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;
                camera.cullingMask = -1;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.SetReplacementShader(depthShader, string.Empty);

                Shader.SetGlobalVector(ShaderConstants.LightPositionID, pointLightVolume.transform.position);

                EditorUtility.DisplayProgressBar(title, "Rendering depth cubemap...", 0.45f);
                if (!camera.RenderToCubemap(cubeRT)) {
                    Debug.LogError($"[PointLightDepthShadowBaker] Failed to render depth shadow cubemap for '{pointLightVolume.gameObject.name}'.", pointLightVolume);
                    return null;
                }

                EditorUtility.DisplayProgressBar(title, "Reading cubemap faces...", 0.8f);
                temp = new Texture2D(safeResolution, safeResolution, TextureFormat.RHalf, false, true);
                Cubemap cubemap = new Cubemap(safeResolution, TextureFormat.RHalf, false) {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0
                };

                CubemapFace[] faces = {
                    CubemapFace.PositiveX,
                    CubemapFace.NegativeX,
                    CubemapFace.PositiveY,
                    CubemapFace.NegativeY,
                    CubemapFace.PositiveZ,
                    CubemapFace.NegativeZ
                };

                for (int i = 0; i < faces.Length; i++) {
                    Graphics.SetRenderTarget(cubeRT, 0, faces[i]);
                    temp.ReadPixels(new Rect(0, 0, safeResolution, safeResolution), 0, 0);
                    temp.Apply(false);
                    cubemap.SetPixels(temp.GetPixels(), faces[i]);
                }
                cubemap.Apply(false);

                return cubemap;
            } finally {
                EditorUtility.ClearProgressBar();
                RestoreOccluderFilter(renderers, previousForceRenderingOff);
                RenderTexture.active = oldActive;

                if (temp != null) Object.DestroyImmediate(temp);
                if (cubeRT != null) {
                    cubeRT.Release();
                    Object.DestroyImmediate(cubeRT);
                }
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
            }
        }

        // Temporarily hides renderers that should not contribute to baked depth shadows.
        private static MeshRenderer[] ApplyOccluderFilter(out bool[] previousForceRenderingOff) {
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            previousForceRenderingOff = new bool[renderers.Length];

            for (int i = 0; i < renderers.Length; i++) {
                MeshRenderer renderer = renderers[i];
                previousForceRenderingOff[i] = renderer.forceRenderingOff;
                bool isOccluder = IsValidOccluder(renderer);
                renderer.forceRenderingOff = previousForceRenderingOff[i] || !isOccluder;
            }

            return renderers;
        }

        // Restores the renderer visibility state changed before baking.
        private static void RestoreOccluderFilter(MeshRenderer[] renderers, bool[] previousForceRenderingOff) {
            if (renderers == null || previousForceRenderingOff == null) return;
            for (int i = 0; i < renderers.Length && i < previousForceRenderingOff.Length; i++) {
                if (renderers[i] != null) {
                    renderers[i].forceRenderingOff = previousForceRenderingOff[i];
                }
            }
        }

        // Checks whether a mesh renderer should be used as a static occluder.
        private static bool IsValidOccluder(MeshRenderer renderer) {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
            if (!GameObjectUtility.AreStaticEditorFlagsSet(renderer.gameObject, StaticEditorFlags.ContributeGI)) return false;
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            return meshFilter != null && meshFilter.sharedMesh != null;
        }

    }
}
#endif
