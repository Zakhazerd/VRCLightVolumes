using UnityEngine;
using System;

#if UDONSHARP
using VRC.SDKBase;
using UdonSharp;
#else
using System.Collections;
using VRCShader = UnityEngine.Shader;
#endif

namespace VRCLightVolumes {
    [DisallowMultipleComponent]
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightVolumeManager : UdonSharpBehaviour
#else
    public class LightVolumeManager : MonoBehaviour
#endif
    {
        public const float Version = 3; // VRC Light Volumes Current version. This value used in shaders (_UdonLightVolumeEnabled) to determine which features are can be used
        private const int MaxLightVolumeCount = 32;
        private const int MaxPointLightCount = 128;
        private const int MaxLightVolumeRotationVectors = MaxLightVolumeCount * 2;
        private const int MaxLightVolumeUvwScaleVectors = MaxLightVolumeCount * 3;
        private const int MaxLightVolumeLegacyUvwVectors = MaxLightVolumeCount * 6;
        private const float ShadowSoftBiasEncodingEpsilon = 0.000001f;

        [Tooltip("Combined texture containing all Light Volumes' textures.")]
        public Texture LightVolumeAtlas;
        [Tooltip("Combined Texture3D containing all baked Light Volume data. This field is not used at runtime, see LightVolumeAtlas instead. It specifies the base for the post process chain, if given.")]
        public Texture3D LightVolumeAtlasBase;
        [Tooltip("When enabled, areas outside Light Volumes fall back to light probes. Otherwise, the Light Volume with the smallest weight is used as fallback. It also improves performance.")]
        public bool LightProbesBlending = true;
        [Tooltip("Disables smooth blending with areas outside Light Volumes. Use it if your entire scene's play area is covered by Light Volumes. It also improves performance.")]
        public bool SharpBounds = true;
        [Tooltip("Automatically updates most of the volumes properties in runtime. Enabling/Disabling, Color and Intensity updates automatically even without this option enabled. Position, Rotation and Scale gets updated only for volumes that are marked dynamic.")]
        public bool AutoUpdateVolumes = false;
        [Tooltip("Limits the maximum number of additive volumes that can affect a single pixel. If you have many dynamic additive volumes that may overlap, it's good practice to limit overdraw to maintain performance.")]
        public int AdditiveMaxOverdraw = 4;
        [Tooltip("Disables min/max brightness limits for modern avatar shaders such as lilToon or Poiyomi. Check this only if you're sure your scene lighting is properly configured.")]
        public bool ForceSceneLighting = false;
        [Tooltip("The minimum brightness at a point due to lighting from a Point Light Volume, before the light is culled. Larger values will result in better performance, but light attenuation will be less physically correct.")]
        public float LightsBrightnessCutoff = 0.35f;
        [Tooltip("All Light Volume instances sorted in decreasing order by weight. You can enable or disable volumes game objects at runtime. Manually disabling unnecessary volumes improves performance.")]
        public LightVolumeInstance[] LightVolumeInstances = new LightVolumeInstance[0];
        [Tooltip("All Point Light Volume instances. You can enable or disable point light volumes game objects at runtime. Manually disabling unnecessary point light volumes improves performance.")]
        public PointLightVolumeInstance[] PointLightVolumeInstances = new PointLightVolumeInstance[0];
        [Tooltip("A texture array that can be used for as Cubemaps, LUT or Cookies")]
        public Texture CustomTextures;
        [Tooltip("Cubemaps count that stored in CustomTextures. Cubemap array elements starts from the beginning, 6 elements each.")]
        public int CubemapsCount = 0;
        [Tooltip("Texture array that stores per-light shadow maps.")]
        public Texture ShadowTextures;
        [Tooltip("Shadow maps count stored in ShadowTextures. Each cubemap uses 6 array elements.")]
        public int ShadowMapsCount = 0;
        [Tooltip("Resolution of one shadow map cubemap face in pixels.")]
        public float ShadowResolution = 128;
        [HideInInspector] public bool IsRangeDirty = false;

        private bool _isInitialized = false;
        private bool _isRegistrySanitized = false;
        private float _prevLightsBrightnessCutoff = 0.35f;
#if UDONSHARP
        private bool _isUpdateRequested = false; // Flag that specifies if volumes update requested.
#else
        private Coroutine _updateCoroutine = null; // Coroutine that auto-updates volumes if auto-update enabled (Non-Udon only)
#endif

        // Light Volumes Data
        private int _enabledCount = 0;
        private int _additiveCount = 0;
        private Vector4[] _invLocalEdgeSmooth = new Vector4[MaxLightVolumeCount];
        private Vector4[] _colors = new Vector4[MaxLightVolumeCount];
        private Vector4[] _boundsUvwScale = new Vector4[MaxLightVolumeUvwScaleVectors];
        private Vector4[] _relativeRotationQuaternion = new Vector4[MaxLightVolumeCount];

        // Point Lights Data
        private int _pointLightCount = 0;
        private int _activeShadowCount = 0;
        private int[] _enabledPointIDs = new int[MaxPointLightCount];
        private Vector4[] _pointLightPosition = new Vector4[MaxPointLightCount];
        private Vector4[] _pointLightColor = new Vector4[MaxPointLightCount];
        private Vector4[] _pointLightDirection = new Vector4[MaxPointLightCount];
        private Vector4[] _pointLightCustomId = new Vector4[MaxPointLightCount];
        private Vector4[] _pointLightShadowData = new Vector4[MaxPointLightCount];
        private Vector4[] _pointLightShadowReprojectionData = new Vector4[MaxPointLightCount];

        // Legacy support Data
        private Matrix4x4[] _invWorldMatrix = new Matrix4x4[MaxLightVolumeCount];
        private Vector4[] _boundsUvw = new Vector4[MaxLightVolumeLegacyUvwVectors];
        private Vector4[] _relativeRotation = new Vector4[MaxLightVolumeRotationVectors];

        // Other
        private int[] _enabledIDs = new int[MaxLightVolumeCount];
        private Vector4[] _boundsScale = new Vector4[3];
        private Vector4[] _bounds = new Vector4[6]; // Legacy

        // Public API for other U# scripts
        public int EnabledCount => _enabledCount;
        public int[] EnabledIDs => _enabledIDs;

        // Restores registry arrays when serialized data or external Udon calls provide null references.
        private void EnsureRegistryArrays() {
            if (LightVolumeInstances == null) LightVolumeInstances = new LightVolumeInstance[0];
            if (PointLightVolumeInstances == null) PointLightVolumeInstances = new PointLightVolumeInstance[0];
        }

#region Shader Property IDs
        // Light Volumes
        private int lightVolumeInvLocalEdgeSmoothID;
        private int lightVolumeColorID;
        private int lightVolumeCountID;
        private int lightVolumeAdditiveCountID;
        private int lightVolumeAdditiveMaxOverdrawID;
        private int lightVolumeEnabledID;
        private int lightVolumeVersionID;
        private int lightVolumeProbesBlendID;
        private int lightVolumeSharpBoundsID;
        private int lightVolumeID;
        private int lightVolumeRotationQuaternionID;
        private int lightVolumeInvWorldMatrixID;
        private int lightVolumeUvwScaleID;
        // Point Lights
        private int _pointLightPositionID;
        private int _pointLightColorID;
        private int _pointLightDirectionID;
        private int _pointLightCustomIdID;
        private int _pointLightCountID;
        private int _pointLightCubeCountID;
        private int _pointLightTextureID;
        private int _pointLightShadowDataID;
        private int _pointLightShadowReprojectionDataID;
        private int _pointLightShadowCountID;
        private int _pointLightShadowTextureID;
        private int _pointLightShadowResolutionID;
        private int _lightBrightnessCutoffID;
        // Legacy support
        private int _areaLightBrightnessCutoffID;
        private int lightVolumeRotationID;
        private int lightVolumeUvwID;
        // Other
        private int forceSceneLightingID;
        
        // Initializing gloabal shader arrays if needed 
        private void TryInitialize() {

#if !UNITY_EDITOR
            if (_isInitialized) return;
#endif
            // Light Volumes
            lightVolumeInvLocalEdgeSmoothID = VRCShader.PropertyToID("_UdonLightVolumeInvLocalEdgeSmooth");
            lightVolumeInvWorldMatrixID = VRCShader.PropertyToID("_UdonLightVolumeInvWorldMatrix");
            lightVolumeColorID = VRCShader.PropertyToID("_UdonLightVolumeColor");
            lightVolumeCountID = VRCShader.PropertyToID("_UdonLightVolumeCount");
            lightVolumeAdditiveCountID = VRCShader.PropertyToID("_UdonLightVolumeAdditiveCount");
            lightVolumeAdditiveMaxOverdrawID = VRCShader.PropertyToID("_UdonLightVolumeAdditiveMaxOverdraw");
            lightVolumeEnabledID = VRCShader.PropertyToID("_UdonLightVolumeEnabled");
            lightVolumeVersionID = VRCShader.PropertyToID("_UdonLightVolumeVersion");
            lightVolumeProbesBlendID = VRCShader.PropertyToID("_UdonLightVolumeProbesBlend");
            lightVolumeSharpBoundsID = VRCShader.PropertyToID("_UdonLightVolumeSharpBounds");
            lightVolumeID = VRCShader.PropertyToID("_UdonLightVolume");
            lightVolumeRotationQuaternionID = VRCShader.PropertyToID("_UdonLightVolumeRotationQuaternion");
            lightVolumeUvwScaleID = VRCShader.PropertyToID("_UdonLightVolumeUvwScale");
            // Point Lights
            _pointLightPositionID = VRCShader.PropertyToID("_UdonPointLightVolumePosition");
            _pointLightColorID = VRCShader.PropertyToID("_UdonPointLightVolumeColor");
            _pointLightDirectionID = VRCShader.PropertyToID("_UdonPointLightVolumeDirection");
            _pointLightCountID = VRCShader.PropertyToID("_UdonPointLightVolumeCount");
            _pointLightCustomIdID = VRCShader.PropertyToID("_UdonPointLightVolumeCustomID");
            _pointLightCubeCountID = VRCShader.PropertyToID("_UdonPointLightVolumeCubeCount");
            _pointLightTextureID = VRCShader.PropertyToID("_UdonPointLightVolumeTexture");
            _pointLightShadowDataID = VRCShader.PropertyToID("_UdonPointLightVolumeShadowData");
            _pointLightShadowReprojectionDataID = VRCShader.PropertyToID("_UdonPointLightVolumeShadowReprojectionData");
            _pointLightShadowCountID = VRCShader.PropertyToID("_UdonPointLightVolumeShadowCount");
            _pointLightShadowTextureID = VRCShader.PropertyToID("_UdonPointLightVolumeShadowTexture");
            _pointLightShadowResolutionID = VRCShader.PropertyToID("_UdonPointLightVolumeShadowResolution");
            _lightBrightnessCutoffID = VRCShader.PropertyToID("_UdonLightBrightnessCutoff");
            // Legacy support
            _areaLightBrightnessCutoffID = VRCShader.PropertyToID("_UdonAreaLightBrightnessCutoff");
            lightVolumeRotationID = VRCShader.PropertyToID("_UdonLightVolumeRotation");
            lightVolumeUvwID = VRCShader.PropertyToID("_UdonLightVolumeUvw");
            // Other
            forceSceneLightingID = VRCShader.PropertyToID("_UdonForceSceneLighting");

#if UNITY_EDITOR
            if (_isInitialized) return;
#endif
            // Light Volumes
            VRCShader.SetGlobalVectorArray(lightVolumeInvLocalEdgeSmoothID, _invLocalEdgeSmooth);
            VRCShader.SetGlobalVectorArray(lightVolumeColorID, _colors);
            VRCShader.SetGlobalMatrixArray(lightVolumeInvWorldMatrixID, _invWorldMatrix);
            VRCShader.SetGlobalVectorArray(lightVolumeRotationQuaternionID, _relativeRotationQuaternion);
            VRCShader.SetGlobalVectorArray(lightVolumeUvwScaleID, _boundsUvwScale);
            // Point Lights
            VRCShader.SetGlobalVectorArray(_pointLightPositionID, _pointLightPosition);
            VRCShader.SetGlobalVectorArray(_pointLightColorID, _pointLightColor);
            VRCShader.SetGlobalVectorArray(_pointLightDirectionID, _pointLightDirection);
            VRCShader.SetGlobalVectorArray(_pointLightCustomIdID, _pointLightCustomId);
            VRCShader.SetGlobalVectorArray(_pointLightShadowDataID, _pointLightShadowData);
            VRCShader.SetGlobalVectorArray(_pointLightShadowReprojectionDataID, _pointLightShadowReprojectionData);
            // Legacy support
            VRCShader.SetGlobalVectorArray(lightVolumeRotationID, _relativeRotation);
            VRCShader.SetGlobalVectorArray(lightVolumeUvwID, _boundsUvw);

            _isInitialized = true;
        }

        #endregion

        // Writes a fully disabled state to shader globals so stale counts do not survive after all volumes disappear.
        private void SetDisabledShaderState() {
            VRCShader.SetGlobalFloat(lightVolumeCountID, 0);
            VRCShader.SetGlobalFloat(lightVolumeAdditiveCountID, 0);
            VRCShader.SetGlobalFloat(_pointLightCountID, 0);
            VRCShader.SetGlobalFloat(_pointLightCubeCountID, 0);
            VRCShader.SetGlobalFloat(_pointLightShadowCountID, 0);
            VRCShader.SetGlobalFloat(lightVolumeEnabledID, 0);
        }

        private bool _old_AutoUpdateVolumes = false;

#if UDONSHARP
        // Works only when changing values directly on UdonBehaviour
        // Low level Udon hacks:
        // _old_(Name) variables are the old values of the variables.
        // _onVarChange_(Name) methods (events) are called when the variable changes.
        public void _onVarChange_AutoUpdateVolumes() {
            if (!_old_AutoUpdateVolumes && AutoUpdateVolumes) RequestUpdateVolumes();
        }
#endif

#if !UDONSHARP || UNITY_EDITOR
        // To make it work when changing values on UdonSharpBehaviour in editor
        private void Update() {
            if (_old_AutoUpdateVolumes != AutoUpdateVolumes) {
                _old_AutoUpdateVolumes = AutoUpdateVolumes;
                if (AutoUpdateVolumes) {
                    RequestUpdateVolumes();
                }
            }
        }
#endif

        private void OnDisable() {
            TryInitialize();
#if !UDONSHARP
            if (_updateCoroutine != null) {
                StopCoroutine(_updateCoroutine);
                _updateCoroutine = null;
            }
#endif
            SetDisabledShaderState();
        }

        private void OnEnable() {
            RequestUpdateVolumes();
        }

        private void Start() {
            _isInitialized = false;
            _isRegistrySanitized = false;
            UpdateVolumes(); // Force update volumes first time at start even if auto update is disabled
        }

        // Initializes Light Volume by adding it to the light volumes array. Automatically calls in runtime on object spawn
        public void InitializeLightVolume(LightVolumeInstance lightVolume) {
            if (lightVolume == null) return;
            EnsureRegistryArrays();
            int count = LightVolumeInstances.Length;
            int emptyIndex = -1;
            // If this volume is already registered, keep the existing slot. Otherwise remember the first free slot.
            for (int i = 0; i < count; i++) {
                if (LightVolumeInstances[i] == lightVolume) {
                    lightVolume.LightVolumeManager = this;
                    lightVolume.IsInitialized = true;
                    return;
                }
                if (emptyIndex < 0 && LightVolumeInstances[i] == null) {
                    emptyIndex = i;
                }
            }
            if (emptyIndex >= 0) {
                LightVolumeInstances[emptyIndex] = lightVolume;
                lightVolume.LightVolumeManager = this;
                lightVolume.IsInitialized = true;
                return;
            }
            // No empty element, then increase the array size
            LightVolumeInstance[] targetArray = new LightVolumeInstance[count + 1];
            Array.Copy(LightVolumeInstances, targetArray, count);
            targetArray[count] = lightVolume;
            lightVolume.IsInitialized = true;
            lightVolume.LightVolumeManager = this;
            LightVolumeInstances = targetArray;
        }

        // Removes Light Volume references from the light volumes array without resizing it.
        public void UnregisterLightVolume(LightVolumeInstance lightVolume) {
            if (lightVolume == null) return;
            EnsureRegistryArrays();
            int count = LightVolumeInstances.Length;
            for (int i = 0; i < count; i++) {
                if (LightVolumeInstances[i] == lightVolume) {
                    LightVolumeInstances[i] = null;
                }
            }
            lightVolume.IsInitialized = false;
        }

        public void InitializePointLightVolume(PointLightVolumeInstance pointLightVolume) {
            if (pointLightVolume == null) return;
            EnsureRegistryArrays();
            int count = PointLightVolumeInstances.Length;
            int emptyIndex = -1;
            // If this light is already registered, keep the existing slot. Otherwise remember the first free slot.
            for (int i = 0; i < count; i++) {
                if (PointLightVolumeInstances[i] == pointLightVolume) {
                    pointLightVolume.LightVolumeManager = this;
                    pointLightVolume.IsInitialized = true;
                    return;
                }
                if (emptyIndex < 0 && PointLightVolumeInstances[i] == null) {
                    emptyIndex = i;
                }
            }
            if (emptyIndex >= 0) {
                PointLightVolumeInstances[emptyIndex] = pointLightVolume;
                pointLightVolume.LightVolumeManager = this;
                pointLightVolume.IsInitialized = true;
                return;
            }
            // No empty element, then increase the array size
            PointLightVolumeInstance[] targetArray = new PointLightVolumeInstance[count + 1];
            Array.Copy(PointLightVolumeInstances, targetArray, count);
            targetArray[count] = pointLightVolume;
            pointLightVolume.IsInitialized = true;
            pointLightVolume.LightVolumeManager = this;
            PointLightVolumeInstances = targetArray;
        }

        // Removes Point Light Volume references from the point light volumes array without resizing it.
        public void UnregisterPointLightVolume(PointLightVolumeInstance pointLightVolume) {
            if (pointLightVolume == null) return;
            EnsureRegistryArrays();
            int count = PointLightVolumeInstances.Length;
            for (int i = 0; i < count; i++) {
                if (PointLightVolumeInstances[i] == pointLightVolume) {
                    PointLightVolumeInstances[i] = null;
                }
            }
            pointLightVolume.IsInitialized = false;
        }

        // Removes stale inactive and duplicate references left in serialized arrays.
        private void SanitizeRegistries() {
            int lightVolumeCount = LightVolumeInstances.Length;
            for (int i = 0; i < lightVolumeCount; i++) {
                LightVolumeInstance instance = LightVolumeInstances[i];
                if (instance == null) continue;
                instance.LightVolumeManager = this;
                if (!instance.gameObject.activeInHierarchy) {
                    LightVolumeInstances[i] = null;
                    instance.IsInitialized = false;
                    continue;
                }
                instance.IsInitialized = true;
                for (int j = 0; j < i; j++) {
                    if (LightVolumeInstances[j] == instance) {
                        LightVolumeInstances[i] = null;
                        break;
                    }
                }
            }

            int pointLightCount = PointLightVolumeInstances.Length;
            for (int i = 0; i < pointLightCount; i++) {
                PointLightVolumeInstance instance = PointLightVolumeInstances[i];
                if (instance == null) continue;
                instance.LightVolumeManager = this;
                if (!instance.gameObject.activeInHierarchy) {
                    PointLightVolumeInstances[i] = null;
                    instance.IsInitialized = false;
                    continue;
                }
                instance.IsInitialized = true;
                for (int j = 0; j < i; j++) {
                    if (PointLightVolumeInstances[j] == instance) {
                        PointLightVolumeInstances[i] = null;
                        break;
                    }
                }
            }

            _isRegistrySanitized = true;
        }

        // Requests to update volumes next frame
        public void RequestUpdateVolumes() {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (!Application.isPlaying) {
                UpdateVolumes();
                return;
            }
#endif
#if UDONSHARP
            if (_isUpdateRequested) return; // Prevent multiple requests
            _isUpdateRequested = true;
            SendCustomEventDelayedFrames(nameof(UpdateVolumesProcess), 1);
#else
            if (_updateCoroutine != null || !isActiveAndEnabled) return;
            _updateCoroutine = StartCoroutine(UpdateVolumesCoroutine());
#endif
        }

#if UDONSHARP
        // Internal method to auto update volumes every frame recursively
        public void UpdateVolumesProcess() {
            if (AutoUpdateVolumes && enabled && gameObject.activeInHierarchy) {
                SendCustomEventDelayedFrames(nameof(UpdateVolumesProcess), 1); // Auto schedule next update if AutoUpdateVolumes is enabled
            } else {
                _isUpdateRequested = false;
            }
            UpdateVolumes(); // Actually update volumes
        }
#else
        private IEnumerator UpdateVolumesCoroutine() {
            do {
                yield return null;
                UpdateVolumes();
            } while (AutoUpdateVolumes);
            _updateCoroutine = null;
        }
#endif

        // Main processing method that recalculates all the volumes data and sets it to the shader variables
        public void UpdateVolumes() {

            TryInitialize();
            
            // Defines if Force Scene Lighting Feature is enabled in scene. 0 if disabled.
            VRCShader.SetGlobalInteger(forceSceneLightingID, ForceSceneLighting ? 1 : 0);

            if (!enabled || !gameObject.activeInHierarchy) {
                SetDisabledShaderState();
                return;
            }

            EnsureRegistryArrays();

            // Recalculate all lights ranges if LightsBrightnessCutoff changed
            if (_prevLightsBrightnessCutoff != LightsBrightnessCutoff) {
                _prevLightsBrightnessCutoff = LightsBrightnessCutoff;
                IsRangeDirty = true;
            }

            if (!_isRegistrySanitized) {
                SanitizeRegistries();
            }

            // Searching for enabled volumes. Counting Additive volumes.
            _enabledCount = 0;
            _additiveCount = 0;
            for (int i = 0; i < LightVolumeInstances.Length && _enabledCount < MaxLightVolumeCount; i++) {
                LightVolumeInstance instance = LightVolumeInstances[i];
                if (instance == null) continue;
                if (!instance.gameObject.activeInHierarchy) {
                    instance.LightVolumeManager = this;
                    instance.IsInitialized = false;
                    LightVolumeInstances[i] = null;
                    continue;
                }
                if (instance.Intensity != 0 && instance.Color != Color.black) {
#if UDONSHARP
    #if COMPILER_UDONSHARP
                    if (instance.IsDynamic) instance.UpdateTransform();
    #else
                    instance.UpdateTransform();
    #endif
#else
                    if (Application.isPlaying) {
                        if (instance.IsDynamic) instance.UpdateTransform();
                    } else {
                        instance.UpdateTransform();
                    }
#endif
                    if (instance.IsAdditive) _additiveCount++;
                    _enabledIDs[_enabledCount] = i;
                    _enabledCount++;
                }
            }

            // Filling arrays with enabled volumes
            for (int i = 0; i < _enabledCount; i++) {

                int enabledId = _enabledIDs[i];
                int i2 = i * 2;
                int i3 = i * 3;
                int i6 = i * 6;

                LightVolumeInstance instance = LightVolumeInstances[enabledId];

                // Setting volume transform
                _invWorldMatrix[i] = instance.InvWorldMatrix;
                _invLocalEdgeSmooth[i] = instance.InvLocalEdgeSmoothing; // Setting volume edge smoothing

                Vector4 c = instance.Color.linear * instance.Intensity; // Changing volume color
                c.w = instance.IsRotated ? 1 : 0; // Color alpha stores if volume rotated or not
                _colors[i] = c;

                // Setting volume relative rotation
                _relativeRotationQuaternion[i] = instance.RelativeRotation;
                _relativeRotation[i2] = instance.RelativeRotationRow0; // Legacy
                _relativeRotation[i2 + 1] = instance.RelativeRotationRow1; // Legacy

                // Setting volume UVW bounds
                _boundsScale[0] = instance.BoundsUvwMin0;
                _boundsScale[1] = instance.BoundsUvwMin1;
                _boundsScale[2] = instance.BoundsUvwMin2;
                // Legacy
                _bounds[0] = instance.BoundsUvwMin0;
                _bounds[1] = instance.BoundsUvwMax0;
                _bounds[2] = instance.BoundsUvwMin1;
                _bounds[3] = instance.BoundsUvwMax1;
                _bounds[4] = instance.BoundsUvwMin2;
                _bounds[5] = instance.BoundsUvwMax2;

                Array.Copy(_boundsScale, 0, _boundsUvwScale, i3, 3);
                Array.Copy(_bounds, 0, _boundsUvw, i6, 6); // Legacy

            }

            // Searching for enabled point light volumes
            _pointLightCount = 0;
            for (int i = 0; i < PointLightVolumeInstances.Length && _pointLightCount < MaxPointLightCount; i++) {
                PointLightVolumeInstance instance = PointLightVolumeInstances[i];
                if (instance == null) continue;
                if (!instance.gameObject.activeInHierarchy) {
                    instance.LightVolumeManager = this;
                    instance.IsInitialized = false;
                    PointLightVolumeInstances[i] = null;
                    continue;
                }
                if (IsRangeDirty) { // If Brightness cutoff changed, force recalculate every light's range
                    instance.UpdateRange();
                }
                if (instance.Intensity != 0 && instance.Color != Color.black) {
#if UDONSHARP
    #if COMPILER_UDONSHARP
                    if (instance.IsDynamic) instance.UpdateTransform();
    #else
                    instance.UpdateTransform();
    #endif
#else
                    if (Application.isPlaying) {
                        if (instance.IsDynamic) instance.UpdateTransform();
                    } else {
                        instance.UpdateTransform();
                    }
#endif
                    _enabledPointIDs[_pointLightCount] = i;
                    _pointLightCount++;
                }
            }

            IsRangeDirty = false; // reset range dirtiness

            // Filling arrays with enabled point light volumes
            _activeShadowCount = 0;
            for (int i = 0; i < _pointLightCount; i++) {
                PointLightVolumeInstance instance = PointLightVolumeInstances[_enabledPointIDs[i]];

                // Recalculate squared range of the light if dirty
                if (IsRangeDirty || instance.IsRangeDirty) {
                    instance.UpdateRange();
                }

                Vector4 pos = instance.PositionData;
                if (!instance.IsAreaLight()) {
                    if (instance.IsLut()) pos.w /= instance.SquaredScale;
                    else pos.w *= instance.SquaredScale;
                }
                _pointLightPosition[i] = pos;

                Vector4 c = instance.Color.linear * instance.Intensity;
                c.w = instance.AngleData;
                _pointLightColor[i] = c;

                _pointLightDirection[i] = instance.DirectionData;
                _pointLightCustomId[i].x = instance.CustomID;
                _pointLightCustomId[i].y = 0;
                _pointLightCustomId[i].z = instance.SquaredRange;
                bool hasShadow = ShadowMapsCount > 0 && instance.ShadowMapID >= 0 && instance.ShadowMapID < ShadowMapsCount;
                if (hasShadow) _activeShadowCount++;
                bool useLocalSpaceShadows = hasShadow && !instance.WorldSpaceShadows;
                float shadowMapID = hasShadow ? (useLocalSpaceShadows ? -instance.ShadowMapID - 1 : instance.ShadowMapID + 1) : 0;
                float shadowBias = hasShadow ? Mathf.Max(instance.ShadowBias, 0) : 0;
                _pointLightCustomId[i].w = 0;
                _pointLightShadowData[i].x = shadowMapID;
                // Offset soft encoded bias so zero-bias soft shadows are distinguishable from disabled PCF.
                _pointLightShadowData[i].y = hasShadow && instance.SoftShadows ? -shadowBias - ShadowSoftBiasEncodingEpsilon : shadowBias;
                _pointLightShadowData[i].z = hasShadow ? Mathf.Max(instance.ShadowBiasSmoothness, 0) : 0;
                _pointLightShadowData[i].w = 0;
                if (useLocalSpaceShadows) {
                    Quaternion shadowRotation = instance.ShadowBakeRotation * Quaternion.Inverse(instance.transform.rotation);
                    _pointLightShadowReprojectionData[i].x = shadowRotation.x;
                    _pointLightShadowReprojectionData[i].y = shadowRotation.y;
                    _pointLightShadowReprojectionData[i].z = shadowRotation.z;
                    _pointLightShadowReprojectionData[i].w = shadowRotation.w;
                } else {
                    _pointLightShadowReprojectionData[i].x = instance.ShadowBakePosition.x;
                    _pointLightShadowReprojectionData[i].y = instance.ShadowBakePosition.y;
                    _pointLightShadowReprojectionData[i].z = instance.ShadowBakePosition.z;
                    _pointLightShadowReprojectionData[i].w = hasShadow ? 1 : 0;
                }
            }

            bool isAtlas = LightVolumeAtlas != null;

            // Setting light volumes version
            VRCShader.SetGlobalFloat(lightVolumeVersionID, Version);

            // Disabling light volumes system if no atlas or no volumes
            if ((!isAtlas || _enabledCount == 0) && _pointLightCount == 0) {
                SetDisabledShaderState();
                return;
            }

            // 3D texture and it's parameters
            if (isAtlas) {
                VRCShader.SetGlobalTexture(lightVolumeID, LightVolumeAtlas);
            }

            // Regular Light Volumes
            VRCShader.SetGlobalFloat(lightVolumeCountID, _enabledCount);
            VRCShader.SetGlobalFloat(lightVolumeAdditiveCountID, _additiveCount);
            
            // Defines if Light Probes Blending enabled in scene
            VRCShader.SetGlobalFloat(lightVolumeProbesBlendID, LightProbesBlending ? 1 : 0);
            VRCShader.SetGlobalFloat(lightVolumeSharpBoundsID, SharpBounds ? 1 : 0);

            // Max Overdraw
            VRCShader.SetGlobalFloat(lightVolumeAdditiveMaxOverdrawID, AdditiveMaxOverdraw);

            if (_enabledCount != 0) {
                // All light volumes inv Edge smooth
                VRCShader.SetGlobalVectorArray(lightVolumeInvLocalEdgeSmoothID, _invLocalEdgeSmooth);

                // All light volumes UVW
                VRCShader.SetGlobalVectorArray(lightVolumeUvwScaleID, _boundsUvwScale);

                // Volume Transform Matrix
                VRCShader.SetGlobalMatrixArray(lightVolumeInvWorldMatrixID, _invWorldMatrix);

                // Volume's relative rotation
                VRCShader.SetGlobalVectorArray(lightVolumeRotationQuaternionID, _relativeRotationQuaternion);

                // Volume's color correction
                VRCShader.SetGlobalVectorArray(lightVolumeColorID, _colors);

                // Legacy data setting
                VRCShader.SetGlobalVectorArray(lightVolumeUvwID, _boundsUvw);
                VRCShader.SetGlobalVectorArray(lightVolumeRotationID, _relativeRotation);
            }

            // Point Lights
            VRCShader.SetGlobalFloat(_pointLightCountID, _pointLightCount);
            VRCShader.SetGlobalFloat(_pointLightCubeCountID, CubemapsCount);
            int shadowCount = _activeShadowCount > 0 ? ShadowMapsCount : 0;
            VRCShader.SetGlobalFloat(_pointLightShadowCountID, shadowCount);
            VRCShader.SetGlobalFloat(_pointLightShadowResolutionID, ShadowResolution);
            if (_pointLightCount != 0) {
                VRCShader.SetGlobalVectorArray(_pointLightColorID, _pointLightColor);
                VRCShader.SetGlobalVectorArray(_pointLightPositionID, _pointLightPosition);
                VRCShader.SetGlobalVectorArray(_pointLightDirectionID, _pointLightDirection);
                VRCShader.SetGlobalVectorArray(_pointLightCustomIdID, _pointLightCustomId);
                if (_activeShadowCount > 0) {
                    VRCShader.SetGlobalVectorArray(_pointLightShadowDataID, _pointLightShadowData);
                    VRCShader.SetGlobalVectorArray(_pointLightShadowReprojectionDataID, _pointLightShadowReprojectionData);
                }
                VRCShader.SetGlobalFloat(_lightBrightnessCutoffID, LightsBrightnessCutoff);
                VRCShader.SetGlobalFloat(_areaLightBrightnessCutoffID, LightsBrightnessCutoff); // Legacy
            }
            if(CustomTextures != null) {
                VRCShader.SetGlobalTexture(_pointLightTextureID, CustomTextures);
            }
            if (_activeShadowCount > 0 && ShadowTextures != null) {
                VRCShader.SetGlobalTexture(_pointLightShadowTextureID, ShadowTextures);
            }

            // Defines if Light Volumes enabled in scene. 0 if disabled. And a version number if enabled
            VRCShader.SetGlobalFloat(lightVolumeEnabledID, 1);

        }
    }
}
