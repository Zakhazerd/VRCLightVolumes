using UnityEditor;
using UnityEngine;

#if UDONSHARP
using VRC.Udon;
#endif

namespace VRCLightVolumes {

    [ExecuteAlways]
    public class PointLightVolume : MonoBehaviour {

        [Tooltip("Defines whether this point light volume can be moved in runtime. Disabling this option slightly improves performance. Don't forget to enable \"Auto Update Volumes\" in your Light Volumes Setup to have this dynamic updates!")]
        public bool Dynamic = false;
        [Tooltip("Point light is the most performant type. Area light is the heaviest and best suited for dynamic, movable sources. For static lighting, it's recommended to bake regular additive light volumes instead.")]
        public LightType Type = LightType.PointLight;
        [Tooltip("Physical radius of a light source if it was a matte glowing sphere for a point light, or a flashlight reflector for a spot light. Larger size emmits more light without increasing overall intensity.")]
        [Min(0.0001f)] public float LightSourceSize = 0.25f;
        [Tooltip("Radius in meters beyond which light is culled. Fewer overlapping lights result in better performance.")]
        [Min(0.0001f)] public float Range = 10f;
        [Tooltip("Multiplies the point light volume’s color by this value.")]
        [ColorUsage(showAlpha: false)] public Color Color = Color.white;
        [Tooltip("Brightness of the point light volume.")]
        public float Intensity = 1f;
        [Tooltip("Parametric uses settings to compute light falloff. LUT uses a texture: X - cone falloff, Y - attenuation (Y only for point lights). Cookie projects a texture for spot lights. Cubemap projects a cubemap for point lights.")]
        public LightShape Shape = LightShape.Parametric;
        [Tooltip("Angle of a spotlight cone in degrees.")]
        [Range(0.1f, 360)] public float Angle = 60f;
        [Tooltip("Cone falloff.")]
        [Range(0.001f, 1)] public float Falloff = 1f;
        [Tooltip("X - cone falloff, Y - attenuation. No compression and RGBA Float or RGBA Half format is recommended.")]
        public Texture2D FalloffLUT = null;
        [Tooltip("Projects a square texture for spot lights.")]
        public Texture2D Cookie = null;
        [Tooltip("Projects a cubemap for point lights.")]
        public Cubemap Cubemap = null;
        [Tooltip("Shows overdrawing range gizmo. Less point light volumes intersections - more performance!")]
        public bool DebugRange = false;

        [Tooltip("Includes this light when shadow maps are re-baked from the Light Volume Setup manager.")]
        public bool RebakeShadows = true;
        [Tooltip("Enables 4-sample PCF filtering for this light's baked cubemap. Disable it for cheaper hard edges.")]
        public bool SoftShadows = true;
        [Tooltip("World-space bias in meters applied when comparing shaded points against this light's baked cubemap. Larger values reduce acne, but can detach contact edges.")]
        [Min(0)] public float Bias = 0.03f;
        [Tooltip("World-space smoothing radius in meters around this light's bias threshold. 0 keeps the bias threshold sharp.")]
        [Min(0)] public float BiasSmoothness = 0.02f;
        [Tooltip("Enables World Space Shadows using the bake position. Disable for Local Space Shadows that move and rotate with this light.")]
        public bool UseWorldSpace = false;

        public int CustomID = 0;
        [HideInInspector] public int ShadowID = -1;
        [HideInInspector] public Cubemap ShadowMap = null;

        public PointLightVolumeInstance PointLightVolumeInstance;
        public LightVolumeSetup LightVolumeSetup;
#if UDONSHARP
        // UdonBehaviour is a real udon VM script. We need it to change public variables in play mode
        private UdonBehaviour _pointLightVolumeBehaviour = null;
#endif

        private Texture2D _falloffLUTPrev = null;
        private Texture2D _cookiePrev = null;
        private Cubemap _cubemapPrev = null;
        private Cubemap _shadowMapPrev = null;
        private LightShape _shapePrev = LightShape.Parametric;
        private LightType _typePrev = LightType.PointLight;

        // To check if object was edited this frame
        private Vector3 _prevPos = Vector3.zero;
        private Quaternion _prevRot = Quaternion.identity;
        private Vector3 _prevScl = Vector3.one;

        // Was it changed on Validate?
        private bool _isValidated = false;

        // Looks for LightVolumeSetup and LightVolumeInstance udon script and setups them if needed
        public void SetupDependencies() {
            if (PointLightVolumeInstance == null && !TryGetComponent(out PointLightVolumeInstance)) {
                PointLightVolumeInstance = gameObject.AddComponent<PointLightVolumeInstance>();
            }
#if UDONSHARP
            if (_pointLightVolumeBehaviour == null) {
                TryGetComponent(out _pointLightVolumeBehaviour);
            }
#endif
            if (LightVolumeSetup == null) {
                LightVolumeSetup = FindObjectOfType<LightVolumeSetup>();
                if (LightVolumeSetup == null) {
                    var go = new GameObject("Light Volume Manager");
                    LightVolumeSetup = go.AddComponent<LightVolumeSetup>();
                    LightVolumeSetup.SyncUdonScript();
                }
            }
        }

        // Returns currently used custom texture depending on the light parameters
        public Texture GetCustomTexture() {
            if (Shape == LightShape.Parametric || Type == LightType.AreaLight) {
                return null;
            } else if (Type == LightType.PointLight) {
                if(Shape == LightShape.LUT) {
                    return FalloffLUT;
                } else if (Shape == LightShape.Custom) {
                    return Cubemap;
                }
            } else if (Type == LightType.SpotLight) {
                if (Shape == LightShape.LUT) {
                    return FalloffLUT;
                } else if (Shape == LightShape.Custom) {
                    return Cookie;
                }
            }
            return null;
        }

        private void Update() {
            if (gameObject == null) return;
            SetupDependencies();
#if UNITY_EDITOR
            // Regenerate texture array
            if (_falloffLUTPrev != FalloffLUT || _cookiePrev != Cookie || _cubemapPrev != Cubemap || _shapePrev != Shape || _typePrev != Type) {
                _falloffLUTPrev = FalloffLUT;
                _cookiePrev = Cookie;
                _cubemapPrev = Cubemap;
                _shapePrev = Shape;
                _typePrev = Type;
                LightVolumeSetup.GenerateCustomTexturesArray();
            }
            // Regenerate Shadow texture array
            if (_shadowMapPrev != ShadowMap) {
                _shadowMapPrev = ShadowMap;
                LightVolumeSetup.GenerateShadowTexturesArray();
            }
            // Sync udon script
            if (_prevPos != transform.position || _prevRot != transform.rotation || _prevScl != transform.localScale) {
                _prevPos = transform.position;
                _prevRot = transform.rotation;
                _prevScl = transform.localScale;
                LightVolumeSetup.SyncUdonScript();
            }

            if (_isValidated) {
                _isValidated = false;
                SyncUdonScript();
                LightVolumeSetup.SyncUdonScript();
            }
#endif
        }

        public void SyncUdonScript() {
            if (gameObject == null) return;
            SetupDependencies();
#if UDONSHARP
            if (Application.isPlaying) {
                // To sync variables in play-mode, we need to do it directly to the UdonBehaviour
                _pointLightVolumeBehaviour.SetProgramVariable("IsDynamic", Dynamic);
                _pointLightVolumeBehaviour.SetProgramVariable("Color", Color);
                _pointLightVolumeBehaviour.SetProgramVariable("Intensity", Intensity);
                _pointLightVolumeBehaviour.SetProgramVariable("IsRangeDirty", true);
                _pointLightVolumeBehaviour.SetProgramVariable("ShadowMapID", (float)GetShadowRuntimeID());
                _pointLightVolumeBehaviour.SetProgramVariable("WorldSpaceShadows", UseWorldSpace);
                _pointLightVolumeBehaviour.SetProgramVariable("SoftShadows", SoftShadows);
                _pointLightVolumeBehaviour.SetProgramVariable("ShadowBias", Bias);
                _pointLightVolumeBehaviour.SetProgramVariable("ShadowBiasSmoothness", BiasSmoothness);
                _pointLightVolumeBehaviour.SetProgramVariable("ShadowBakePosition", PointLightVolumeInstance.ShadowBakePosition);
                _pointLightVolumeBehaviour.SetProgramVariable("ShadowBakeRotation", PointLightVolumeInstance.ShadowBakeRotation);
                // Udon does not support methods with parameters, so under the hood, it's just some global variables.
                // We can first set these parameters and then exetute a parameterless method.
                if (Type == LightType.PointLight) { // Point light
                    if (Shape == LightShape.Custom && Cubemap != null) { // Use Custom Cubemap Texture
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetCustomTexture(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__1_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetCustomTexture");
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) { // Use LUT
                        // SetRange(Range)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetLut(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLut");
                    } else { // Use this light in parametric mode
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetParametric()
                        _pointLightVolumeBehaviour.SendCustomEvent("SetParametric");
                    }
                    // Use it as Point Light
                    // SetPointLight()
                    _pointLightVolumeBehaviour.SendCustomEvent("SetPointLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");
                } else if (Type == LightType.SpotLight) { // Spot Light
                    // SetRange(Range)
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                    _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                    if (Shape == LightShape.Custom && Cookie != null) { // Use Cookie Texture
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetCustomTexture(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__1_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetCustomTexture");
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) { // Use LUT
                        // SetRange(Range)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", Range);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetLut(CustomID)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_id__param", CustomID);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLut");
                    } else { // Use this light in parametric mode
                        // SetRange(LightSourceSize)
                        _pointLightVolumeBehaviour.SetProgramVariable("__0_size__param", LightSourceSize);
                        _pointLightVolumeBehaviour.SendCustomEvent("__0_SetLightSourceSize");
                        // SetParametric()
                        _pointLightVolumeBehaviour.SendCustomEvent("SetParametric");
                    }
                    // Don't use custom tex
                    // SetSpotLight(Angle, Falloff)
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_angleDeg__param", Angle);
                    _pointLightVolumeBehaviour.SetProgramVariable("__0_falloff__param", Falloff);
                    _pointLightVolumeBehaviour.SendCustomEvent("__0_SetSpotLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");

                } else if (Type == LightType.AreaLight) { // Area light
                    // SetAreaLight()
                    _pointLightVolumeBehaviour.SendCustomEvent("SetAreaLight");
                    _pointLightVolumeBehaviour.SendCustomEvent("UpdateRotation");
                }

            } else {
#endif
                PointLightVolumeInstance.IsInitialized = true; // Always override to true in editor with no play mode!
                PointLightVolumeInstance.LightVolumeManager = LightVolumeSetup.LightVolumeManager;

                PointLightVolumeInstance.IsDynamic = Dynamic;
                PointLightVolumeInstance.Color = Color;
                PointLightVolumeInstance.Intensity = Intensity;
                PointLightVolumeInstance.IsRangeDirty = true;
                PointLightVolumeInstance.ShadowMapID = GetShadowRuntimeID();
                PointLightVolumeInstance.WorldSpaceShadows = UseWorldSpace;
                PointLightVolumeInstance.SoftShadows = SoftShadows;
                PointLightVolumeInstance.ShadowBias = Bias;
                PointLightVolumeInstance.ShadowBiasSmoothness = BiasSmoothness;

                if (Type == LightType.PointLight) { // Point light
                    if (Shape == LightShape.Custom && Cubemap != null) {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetCustomTexture(CustomID); // Use Custom Cubemap Texture
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) {
                        PointLightVolumeInstance.SetLightSourceSize(Range);
                        PointLightVolumeInstance.SetLut(CustomID); // Use LUT
                    } else {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetParametric(); // Use this light in parametric mode
                    }
                    PointLightVolumeInstance.SetPointLight(); // Use it as Point Light
                    PointLightVolumeInstance.UpdateRotation();
                } else if (Type == LightType.SpotLight) { // Spot Light
                    PointLightVolumeInstance.SetLightSourceSize(Range);
                    if (Shape == LightShape.Custom && Cookie != null) {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetCustomTexture(CustomID); // Use Cookie Texture
                    } else if (Shape == LightShape.LUT && FalloffLUT != null) {
                        PointLightVolumeInstance.SetLightSourceSize(Range);
                        PointLightVolumeInstance.SetLut(CustomID); // Use LUT
                    } else {
                        PointLightVolumeInstance.SetLightSourceSize(LightSourceSize);
                        PointLightVolumeInstance.SetParametric(); // Use this light in parametric mode
                    }
                    PointLightVolumeInstance.SetSpotLight(Angle, Falloff); // Don't use custom tex
                    PointLightVolumeInstance.UpdateRotation();
                } else if (Type == LightType.AreaLight) { // Area light
                    PointLightVolumeInstance.SetAreaLight();
                    PointLightVolumeInstance.UpdateRotation();
                }

#if UNITY_EDITOR
                // Mark changes to ensure prefab modifications are recorded
                LVUtils.MarkDirty(PointLightVolumeInstance);
#endif

#if UDONSHARP
            }
#endif
        }

        private void Reset() {
            SetupDependencies();
            SyncUdonScript();
            LightVolumeSetup.RefreshVolumesList();
            LightVolumeSetup.SyncUdonScript();
        }

        private void OnEnable() {
            SetupDependencies();
            LightVolumeSetup.RefreshVolumesList();
            LightVolumeSetup.SyncUdonScript();
        }

        private void OnDisable() {
            if (LightVolumeSetup != null) {
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
        }

        private void OnDestroy() {
            if (LightVolumeSetup != null) {
                FalloffLUT = null;
                Cookie = null;
                Cubemap = null;
                ShadowMap = null;
#if UNITY_EDITOR
                LightVolumeSetup.GenerateCustomTexturesArray();
                LightVolumeSetup.GenerateShadowTexturesArray();
#endif
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
        }

        private void OnValidate() {
            _isValidated = true;
        }

        // Returns a valid shadow map ID or disables the shadow for runtime.
        private int GetShadowRuntimeID() {
            return ShadowMap != null ? ShadowID : -1;
        }

        // Returns the editor-only far clip used by the shadow map bake.
        public float GetShadowFarClip() {
            float scale = GetAverageLossyScale();
            float cutoff = LightVolumeSetup != null ? LightVolumeSetup.BrightnessCutoff : 0.35f;
            if (Type == LightType.AreaLight) {
                Vector3 lossyScale = transform.lossyScale;
                float width = Mathf.Max(Mathf.Abs(lossyScale.x), 0.001f);
                float height = Mathf.Max(Mathf.Abs(lossyScale.y), 0.001f);
                return Mathf.Max(Mathf.Sqrt(ComputeAreaLightSquaredBoundingSphere(width, height, Color, Intensity * Mathf.PI, cutoff)), 0.0001f);
            }
            if (Shape == LightShape.LUT && FalloffLUT != null) return Mathf.Max(Range * scale, 0.0001f);
            float size = Mathf.Max(LightSourceSize * scale, 0.0001f);
            return Mathf.Max(Mathf.Sqrt(ComputePointLightSquaredBoundingSphere(Color, Intensity, size, cutoff)), 0.0001f);
        }

        // Returns the same average lossy scale approximation used by PointLightVolumeInstance.
        private float GetAverageLossyScale() {
            Vector3 scale = transform.lossyScale;
            return (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;
        }

        // Computes the point light influence radius squared for the brightness cutoff.
        private static float ComputePointLightSquaredBoundingSphere(Color color, float intensity, float size, float cutoff) {
            float l = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return Mathf.Max(Mathf.PI * 2f * l * Mathf.Abs(intensity) / (cutoff * cutoff) - 1f, 0f) * size * size;
        }

        // Computes the area light influence radius squared for the brightness cutoff.
        private static float ComputeAreaLightSquaredBoundingSphere(float width, float height, Color color, float intensity, float cutoff) {
            float l = Mathf.Max(color.r, Mathf.Max(color.g, color.b)) * Mathf.Abs(intensity);
            if (l <= 0.000001f) return 0f;
            float maxSolidAngle = Mathf.PI * 2f - 0.0001f;
            float minSolidAngle = cutoff / l;
            if (minSolidAngle >= maxSolidAngle) return 0f;
            minSolidAngle = Mathf.Max(minSolidAngle, 0.000001f);
            float a = width * height;
            float w2 = width * width;
            float h2 = height * height;
            float b = 0.25f * (w2 + h2);
            float t = Mathf.Tan(0.25f * minSolidAngle);
            float t2 = Mathf.Max(t * t, 0.000001f);
            float tb = t2 * b;
            float discriminant = Mathf.Sqrt(tb * tb + 4f * t2 * a * a);
            float d2 = (discriminant - tb) * 0.125f / t2;
            return Mathf.Max(d2, 0f);
        }

#if UNITY_EDITOR
        // Bakes or re-bakes the shadow map for this light.
        [ContextMenu("Bake Shadow Map")]
        public void BakeShadowMap() {
            BakeShadowMap("", true);
        }

        // Bakes or re-bakes the shadow map for this light.
        public bool BakeShadowMap(string infoString, bool regenerateArray) {
            SetupDependencies();
            float farClip = GetShadowFarClip();
            int resolution = LightVolumeSetup != null ? (int)LightVolumeSetup.ShadowResolution : 128;
            TextureFormat format = LightVolumeSetup != null ? LightVolumeSetup.GetShadowTextureFormat() : TextureFormat.RHalf;
            Cubemap cubemap = PointLightShadowBaker.BakeShadowMap(this, resolution, farClip, format, infoString);
            if (cubemap == null) return false;

            string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            string path = $"{System.IO.Path.GetDirectoryName(scenePath)}/{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}/VRCLightVolumes/Temp/{gameObject.name}_shadows.asset";
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null) {
                UnityEditor.AssetDatabase.DeleteAsset(path);
            }
            LVUtils.SaveAsAsset(cubemap, path);

            ShadowMap = cubemap;
            PointLightVolumeInstance.ShadowBakePosition = transform.position;
            PointLightVolumeInstance.ShadowBakeRotation = transform.rotation;
            _shadowMapPrev = ShadowMap;
            LVUtils.MarkDirty(this);
            LVUtils.MarkDirty(PointLightVolumeInstance);

            if (regenerateArray && LightVolumeSetup != null) {
                LightVolumeSetup.GenerateShadowTexturesArray();
            }
            SyncUdonScript();
            return true;
        }

#endif

        public enum LightShape {
            Parametric,
            LUT,
            Custom
        }

        public enum LightType {
            PointLight,
            SpotLight,
            AreaLight,
        }

    }

}
