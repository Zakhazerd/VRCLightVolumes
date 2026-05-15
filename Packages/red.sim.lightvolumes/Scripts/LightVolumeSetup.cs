using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.Serialization;

#if UDONSHARP
using VRC.Udon;
#endif

#if UNITY_EDITOR
using Unity.EditorCoroutines.Editor;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Linq;
#endif

namespace VRCLightVolumes {
    [ExecuteAlways]
    public class LightVolumeSetup : MonoBehaviour {

        [SerializeField] public List<LightVolume> LightVolumes = new List<LightVolume>();
        [SerializeField] public List<float> LightVolumesWeights = new List<float>();

        [SerializeField] public List<PointLightVolume> PointLightVolumes = new List<PointLightVolume>();

        [Header("Point Light Volumes")]
        [Tooltip("Resolution used for point light cookie, LUT and cubemap projection textures.")]
        [FormerlySerializedAs("Resolution")]
        public TextureArrayResolution CookieResolution = TextureArrayResolution._128x128;
        [Tooltip("Texture format used for point light cookie, LUT and cubemap projection textures.")]
        [FormerlySerializedAs("Format")]
        public TextureArrayFormat CookieFormat = TextureArrayFormat.RGBAHalf;
        [Tooltip("The minimum brightness at a point due to lighting from a Point Light Volume, before the light is culled. Larger values will result in better performance, but light attenuation will be less physically correct.")]
        [FormerlySerializedAs("LightsBrightnessCutoff")]
        [Range(0.05f, 1f)] public float BrightnessCutoff = 0.35f;
        [Tooltip("Resolution used for per-light shadow maps.")]
        public TextureArrayResolution ShadowResolution = TextureArrayResolution._128x128;
        [Tooltip("Texture format used for per-light shadow maps. RHalf is smaller, RFloat is more precise.")]
        public ShadowTextureFormat ShadowFormat = ShadowTextureFormat.RHalf;

        [Header("Baking")]
        [Tooltip("Bakery usually gives better results and works faster.")]
#if BAKERY_INCLUDED
        public Baking BakingMode = Baking.Bakery;
#else
        public Baking BakingMode = Baking.Progressive;
#endif
        [Tooltip("Removes baked noise in Light Volumes but may slightly reduce sharpness. Recommended to keep it enabled.")]
        public bool Denoise = true;
        [Tooltip("Whether to dilate valid probe data into invalid probes, such as probes that are inside geometry. Helps mitigate light leaking.")]
        public bool DilateInvalidProbes = true;
        [Tooltip("How many iterations to run dilation for. Higher values will result in less leaking, but will also cause longer bakes.")]
        [Range(1, 8)]
        public int DilationIterations = 1;
        [Tooltip("The percentage of rays shot from a probe that should hit backfaces before the probe is considered invalid for the purpose of dilation. 0 means every probe is invalid, 1 means every probe is valid.")] 
        [Range(0, 1)]
        public float DilationBackfaceBias = 0.1f;
        [Tooltip("Automatically fixes Bakery's \"burned\" light probes after a scene bake. But decreases their contrast slightly.")]
        public bool FixLightProbesL1 = true;
        [Tooltip("Downscales each light volume. Useful to make a lower atlas resolution for mobile platforms or to increase overall sharpness and decrease aliasing.")]
        public Downscale DownscaleVolumes = Downscale.None;
        [Header("Visuals")]
        [Tooltip("When enabled, areas outside Light Volumes fall back to light probes. Otherwise, the Light Volume with the smallest weight is used as fallback. It also improves performance.")]
        public bool LightProbesBlending = true;
        [Tooltip("Disables smooth blending with areas outside Light Volumes. Use it if your entire scene's play area is covered by Light Volumes. It also improves performance.")]
        public bool SharpBounds = true;
        [Tooltip("Automatically updates most of the volumes properties in runtime. Enabling/Disabling, Color and Intensity updates automatically even without this option enabled. Position, Rotation and Scale gets updated only for volumes that are marked dynamic.")]
        public bool AutoUpdateVolumes = false;
        [Tooltip("Limits the maximum number of additive volumes and point light volumes that can affect a single pixel. If you have many dynamic additive or point light volumes that may overlap, it's good practice to limit overdraw to maintain performance.")]
        [Min(1)]public int AdditiveMaxOverdraw = 4;
        [Tooltip("Disables min/max brightness limits for modern avatar shaders such as lilToon or Poiyomi. Check this only if you're sure your scene lighting is properly configured.")]
        public bool ForceSceneLighting = false;
        [Header("Debug")]
        [Tooltip("Removes all Light Volume scripts in play mode, except Udon components. Useful for testing in a clean setup, just like in VRChat. For example, Auto Update Volumes and Dynamic Light Volumes will work just like in VRChat.")]
        public bool DestroyInPlayMode = false;

        [SerializeField] public List<LightVolumeData> LightVolumeDataList = new List<LightVolumeData>();

        [Serializable]
        public struct PostProcessor {
            public RenderTexture RT;
            public Material Mat;
            public string TextureName;
            public Action Update;
        }

        //Render Textures that will be applied top to bottom to the Light Volume Atlas at runtime.
        //External scripts can register themselves here using `RegisterPostProcessorCRT`.
        //You probably don't want to mess with this field manually.
        public PostProcessor[] AtlasPostProcessors;

        //Render Textures that will be applied top to bottom to the Point Light Volume cookie texture array at runtime.
        //External scripts can register themselves here using `RegisterCookiePostProcessorCRT` or `RegisterCookiePostProcessor`.
        //You probably don't want to mess with this field manually.
        public PostProcessor[] CookiePostProcessors;

        //Render Textures that will be applied top to bottom to the Point Light Volume shadow depth texture array at runtime.
        //External scripts can register themselves here using `RegisterShadowPostProcessorCRT` or `RegisterShadowPostProcessor`.
        //You probably don't want to mess with this field manually.
        public PostProcessor[] ShadowPostProcessors;

        public bool IsBakeryMode => BakingMode == Baking.Bakery; // Just a shortcut
        public LightVolumeManager LightVolumeManager;

        // Disables syncing with udon script to make it possible to destroy the manager and the other volumes and don't break the udon script
        private bool _dontSync = true;
        public bool DontSync {
            get { return Application.isPlaying ? _dontSync : false; }
            set { _dontSync = value; }
        }

#if UDONSHARP
        // UdonBehaviour is a real udon VM script. We need it to change public variables in play mode
        private UdonBehaviour _lightVolumeManagerBehaviour = null;
#endif

        public Baking _bakingModePrev;

        public bool IsLegacyUVWConverted = false; // Is legacy UVW fix applied. Only need to do it once, so it's a flag for that

        private TextureArrayResolution _resolutionPrev = TextureArrayResolution._128x128;
        private TextureArrayResolution _shadowResolutionPrev = TextureArrayResolution._64x64;
        private ShadowTextureFormat _shadowFormatPrev = ShadowTextureFormat.RHalf;
        private TextureArrayFormat _formatPrev = TextureArrayFormat.RGBAHalf;
#if UNITY_EDITOR
        private EditorCoroutine _generateAtlasCoroutine = null;
        private EditorCoroutine _generateTextureArrayCoroutine = null;
        private EditorCoroutine _generateShadowArrayCoroutine = null;
#endif
        public void RefreshVolumesList() {

            if(DontSync) return;

            // Searching for all light volumes in scene
            var volumes = FindObjectsOfType<LightVolume>(true);
            for (int i = 0; i < volumes.Length; i++) {
                if (volumes[i].CompareTag("EditorOnly")) continue;
                if (!LightVolumes.Contains(volumes[i])) {
                    LightVolumes.Add(volumes[i]);
                    LightVolumesWeights.Add(0.0f);
                }
            }
            // Removing volumes that no more exists
            for (int i = 0; i < LightVolumes.Count; i++) {
                if (LightVolumes[i] == null || LightVolumes[i].CompareTag("EditorOnly")) {
                    LightVolumes.RemoveAt(i);
                    LightVolumesWeights.RemoveAt(i);
                    i--;
                }
            }

            // Searching for all point light volumes in scene
            var pointVolumes = FindObjectsOfType<PointLightVolume>(true);
            for (int i = 0; i < pointVolumes.Length; i++) {
                if (pointVolumes[i].CompareTag("EditorOnly")) continue;
                if (!PointLightVolumes.Contains(pointVolumes[i])) {
                    PointLightVolumes.Add(pointVolumes[i]);
                }
            }
            // Removing point light volumes that no more exists
            for (int i = 0; i < PointLightVolumes.Count; i++) {
                if (PointLightVolumes[i] == null || PointLightVolumes[i].CompareTag("EditorOnly")) {
                    PointLightVolumes.RemoveAt(i);
                    i--;
                }
            }
            SyncUdonScript();
        }

#if UNITY_EDITOR

#if BAKERY_INCLUDED
        private bool _subscribedToBakery = false;
#endif
        private bool _subscribedToUnityLightmapper = false;

        private void OnSelectionChanged() {
            if (Selection.activeObject == gameObject) {
                RefreshVolumesList();
            }
        }

        // Generates LUT and Cubemap array based on all the LUT Textures2D and Cube provided in PointLightVolumes
        List<PointLightVolume> _customTexPointVolumes = new List<PointLightVolume>();
        public void GenerateCustomTexturesArray() {

            SetupDependencies();

            if (LightVolumeManager == null || DontSync) return;

            // Cubemap Textures - store first
            List<Texture> cubeTextures = new List<Texture>(); 
            List<PointLightVolume> cubePLVs = new List<PointLightVolume>();

            // Other texture goes next
            List<Texture> singleTextures = new List<Texture>();
            List<PointLightVolume> singlePLVs = new List<PointLightVolume>();

            int count = PointLightVolumes.Count;
            for (int i = 0; i < count; i++) {
                Texture tex = PointLightVolumes[i].GetCustomTexture();
                if (tex == null) continue;
                if(tex.GetType() == typeof(Cubemap)) {
                    cubeTextures.Add(tex);
                    cubePLVs.Add(PointLightVolumes[i]);
                } else if(tex.GetType() == typeof(Texture2D)) {
                    singleTextures.Add(tex);
                    singlePLVs.Add(PointLightVolumes[i]);
                }
            }

            // Merging lists
            List<Texture> textures = new List<Texture>();
            textures.AddRange(cubeTextures);
            textures.AddRange(singleTextures);
            _customTexPointVolumes.Clear();
            _customTexPointVolumes.AddRange(cubePLVs);
            _customTexPointVolumes.AddRange(singlePLVs);

            // Stop old coroutine if one is in process already
            if (_generateTextureArrayCoroutine != null) {
                EditorCoroutineUtility.StopCoroutine(_generateTextureArrayCoroutine);
                _generateTextureArrayCoroutine = null;
            }

            if(_customTexPointVolumes.Count == 0) {
                LightVolumeManager.CustomTexturesBase = null;
                UpdateCookiePostProcessors();
#if UDONSHARP
                if (Application.isPlaying) {
                    _lightVolumeManagerBehaviour.SetProgramVariable("CustomTextures", LightVolumeManager.CustomTextures);
                    _lightVolumeManagerBehaviour.SetProgramVariable("CubemapsCount", 0);
                } else {
#endif
                    LightVolumeManager.CubemapsCount = 0;
#if UDONSHARP
                }
#endif
                SyncUdonScript();
                return;
            }

            _generateTextureArrayCoroutine = EditorCoroutineUtility.StartCoroutine(TextureArrayGenerator.CreateTexture2DArrayAsync(textures, (int)CookieResolution, (TextureFormat)CookieFormat, (texArray, ids) => {

                if (DontSync) {
                    _generateTextureArrayCoroutine = null;
                    return;
                }

                if (texArray != null) {
                    for (int i = 0; i < ids.Length; i++) {
                        if (_customTexPointVolumes[i] != null) {
                            _customTexPointVolumes[i].CustomID = ids[i];
                            _customTexPointVolumes[i].SyncUdonScript();
                        }
                    }
                }
                LightVolumeManager.CustomTexturesBase = texArray;
                UpdateCookiePostProcessors();
#if UDONSHARP
                if (Application.isPlaying) {
                    _lightVolumeManagerBehaviour.SetProgramVariable("CustomTextures", LightVolumeManager.CustomTextures);
                    _lightVolumeManagerBehaviour.SetProgramVariable("CubemapsCount", cubeTextures.Count);
                } else {
#endif
                    LightVolumeManager.CubemapsCount = cubeTextures.Count;
#if UDONSHARP
                }
#endif
                if (texArray != null) LVUtils.SaveAsAssetDelayed(texArray, $"{Path.GetDirectoryName(SceneManager.GetActiveScene().path)}/{SceneManager.GetActiveScene().name}/VRCLightVolumes/PointLightVolumeArray.asset");

                _generateTextureArrayCoroutine = null;

                SyncUdonScript();

            }), this);

        }

        // Generates the shadow map array based on all PointLightVolumes with assigned shadow maps.
        List<PointLightVolume> _shadowVolumes = new List<PointLightVolume>();
        public void GenerateShadowTexturesArray() {

            SetupDependencies();

            if (LightVolumeManager == null || DontSync) return;

            List<Texture> textures = new List<Texture>();
            _shadowVolumes.Clear();

            int count = PointLightVolumes.Count;
            for (int i = 0; i < count; i++) {
                PointLightVolume pointLightVolume = PointLightVolumes[i];
                if (pointLightVolume == null) continue;
                if (pointLightVolume.ShadowMap != null) {
                    textures.Add(pointLightVolume.ShadowMap);
                    _shadowVolumes.Add(pointLightVolume);
                } else if (pointLightVolume.ShadowID != -1) {
                    pointLightVolume.ShadowID = -1;
                    pointLightVolume.SyncUdonScript();
                    LVUtils.MarkDirty(pointLightVolume);
                }
            }

            if (_generateShadowArrayCoroutine != null) {
                EditorCoroutineUtility.StopCoroutine(_generateShadowArrayCoroutine);
                _generateShadowArrayCoroutine = null;
            }

            if (_shadowVolumes.Count == 0) {
                LightVolumeManager.ShadowTexturesBase = null;
                UpdateShadowPostProcessors();
#if UDONSHARP
                if (Application.isPlaying) {
                    _lightVolumeManagerBehaviour.SetProgramVariable("ShadowTextures", LightVolumeManager.ShadowTextures);
                    _lightVolumeManagerBehaviour.SetProgramVariable("ShadowMapsCount", 0);
                } else {
#endif
                    LightVolumeManager.ShadowMapsCount = 0;
#if UDONSHARP
                }
#endif
                SyncUdonScript();
                return;
            }

            _generateShadowArrayCoroutine = EditorCoroutineUtility.StartCoroutine(TextureArrayGenerator.CreateTexture2DArrayAsync(textures, (int)ShadowResolution, GetShadowTextureFormat(), (texArray, ids) => {

                if (DontSync) {
                    _generateShadowArrayCoroutine = null;
                    return;
                }

                if (texArray != null) {
                    for (int i = 0; i < ids.Length; i++) {
                        if (_shadowVolumes[i] != null) {
                            _shadowVolumes[i].ShadowID = ids[i];
                            _shadowVolumes[i].SyncUdonScript();
                            LVUtils.MarkDirty(_shadowVolumes[i]);
                        }
                    }
                }
                if (texArray != null) texArray.filterMode = FilterMode.Point;
                LightVolumeManager.ShadowTexturesBase = texArray;
                UpdateShadowPostProcessors();
#if UDONSHARP
                if (Application.isPlaying) {
                    _lightVolumeManagerBehaviour.SetProgramVariable("ShadowTextures", LightVolumeManager.ShadowTextures);
                    _lightVolumeManagerBehaviour.SetProgramVariable("ShadowMapsCount", texArray != null ? texArray.depth / 6 : 0);
                    _lightVolumeManagerBehaviour.SetProgramVariable("ShadowResolution", (float)ShadowResolution);
                } else {
#endif
                    LightVolumeManager.ShadowMapsCount = texArray != null ? texArray.depth / 6 : 0;
                    LightVolumeManager.ShadowResolution = (float)ShadowResolution;
#if UDONSHARP
                }
#endif
                if (texArray != null) {
                    string assetPath = $"{Path.GetDirectoryName(SceneManager.GetActiveScene().path)}/{SceneManager.GetActiveScene().name}/VRCLightVolumes/PointLightVolumeShadowArray.asset";
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null) {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                    LVUtils.SaveAsAssetDelayed(texArray, assetPath);
                }

                _generateShadowArrayCoroutine = null;

                SyncUdonScript();

            }), this);

        }


        // Subscribing to OnBaked events
        private void OnEnable() {
#if BAKERY_INCLUDED
            if (!Application.isPlaying && !_subscribedToBakery) {
                ftRenderLightmap.OnFinishedFullRender += OnBakeryFinishedRender;
                ftRenderLightmap.OnPreFullRender += OnBakeryStartedRender;
                _subscribedToBakery = true;
            }
#endif
            if (!Application.isPlaying && !_subscribedToUnityLightmapper) {
                UnityEditor.Experimental.Lightmapping.additionalBakedProbesCompleted += OnAdditionalProbesCompleted;
                Lightmapping.bakeStarted += OnUnityBakingStarted;
                _subscribedToUnityLightmapper = true;
            }

            Selection.selectionChanged += OnSelectionChanged;
            SyncUdonScript();
        }

        // Unsubscribing from OnBaked events
        private void OnDisable() {
#if BAKERY_INCLUDED
            if (!Application.isPlaying && _subscribedToBakery) {
                ftRenderLightmap.OnFinishedFullRender -= OnBakeryFinishedRender;
                ftRenderLightmap.OnPreFullRender -= OnBakeryStartedRender;
                _subscribedToBakery = false;
            }
#endif
            if (!Application.isPlaying && _subscribedToUnityLightmapper) {
                UnityEditor.Experimental.Lightmapping.additionalBakedProbesCompleted -= OnAdditionalProbesCompleted;
                Lightmapping.bakeStarted -= OnUnityBakingStarted;
                _subscribedToUnityLightmapper = false;

            }

            Selection.selectionChanged -= OnSelectionChanged;
            SyncUdonScript();
        }

        private void Awake() {
            SyncUdonScript();
        }

        private void OnValidate() {
            SyncUdonScript();
        }

#if BAKERY_INCLUDED

        // On Bakery Started baking
        private void OnBakeryStartedRender(object sender, EventArgs e) {
            // Attempt to fix a bakery bug
            var volumes = FindObjectsOfType<LightVolume>(true);
            for (int i = 0; i < volumes.Length; i++) {
                volumes[i].SetupBakeryDependencies();
            }
        }

        // On Bakery Finished baking
        private void OnBakeryFinishedRender(object sender, EventArgs e) {
            LightVolume[] volumes = FindObjectsByType<LightVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++) {
                if (volumes[i].Bake && volumes[i].LightVolumeInstance != null) {
                    volumes[i].RecalculateProbesPositions();
                    volumes[i].LightVolumeInstance.InvBakedRotation = Quaternion.Inverse(volumes[i].GetRotation());
                    if (IsBakeryMode && volumes[i].BakeryVolume != null) {
                        volumes[i].Texture0 = volumes[i].BakeryVolume.bakedTexture0;
                        volumes[i].Texture1 = volumes[i].BakeryVolume.bakedTexture1;
                        volumes[i].Texture2 = volumes[i].BakeryVolume.bakedTexture2;
                    }
                }
            }
            if (FixLightProbesL1) FixLightProbes();
            GenerateAtlas();
            Debug.Log($"[LightVolumeSetup] Generating 3D Atlas finished!");
        }

#endif

        // On Unity Lightmapper started baking
        private void OnUnityBakingStarted() {
            if (BakingMode == Baking.Bakery) {
                return;
            }
            LightVolume[] volumes = FindObjectsByType<LightVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++) {
                if (volumes[i].Bake) {
                    Debug.Log($"[LightVolumeSetup] Adding additional probes to bake with Light Volume \"{volumes[i].gameObject.name}\" using Unity Lightmapper. Group {i}");
                    volumes[i].SetAdditionalProbes(i);
                }
            }
        }

        // On Unity Lightmapper baked additional probes
        private void OnAdditionalProbesCompleted() {

            if (BakingMode == Baking.Bakery) {
                return;
            }
            LightVolume[] volumes = FindObjectsByType<LightVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++) {
                if (volumes[i].Bake) {
                    volumes[i].Save3DTexturesProgressive(i);
                    volumes[i].RemoveAdditionalProbes(i);
                    if (volumes[i].LightVolumeInstance != null) volumes[i].LightVolumeInstance.InvBakedRotation = Quaternion.Inverse(volumes[i].GetRotation());
                }
            }
            Debug.Log($"[LightVolumeSetup] Additional probes baking finished! Generating 3D Atlas...");
            GenerateAtlas();
            Debug.Log($"[LightVolumeSetup] Generating 3D Atlas finished!");

        }

        private void Update() {
            if (DontSync) return;
            SetupDependencies();
            ConvertLegacyUVW();
            // Resetup required game objects and components for light volumes in new baking mode
            if (_bakingModePrev != BakingMode) {
                _bakingModePrev = BakingMode;
                var volumes = FindObjectsOfType<LightVolume>();
                for (int i = 0; i < volumes.Length; i++) {
                    volumes[i].SetupBakeryDependencies();
                }
                SyncUdonScript();
            }
            if (_resolutionPrev != CookieResolution || _formatPrev != CookieFormat) {
                _resolutionPrev = CookieResolution;
                _formatPrev = CookieFormat;
                GenerateCustomTexturesArray();
            }
            if (_shadowResolutionPrev != ShadowResolution || _shadowFormatPrev != ShadowFormat) {
                _shadowResolutionPrev = ShadowResolution;
                _shadowFormatPrev = ShadowFormat;
                GenerateShadowTexturesArray();
            }
            if (!Application.isPlaying) {
                LightVolumeManager.UpdateVolumes();
            }
        }

        // Try to convert Legacy UVW data into a new compact data format
        private void ConvertLegacyUVW() {

            if (IsLegacyUVWConverted || LVUtils.IsInPrefabAsset(this) || LightVolumes.Count == 0) return;

            for (int i = 0; i < LightVolumes.Count; i++) {

                if (LightVolumes[i] == null) continue;
                var lightVolumeInstance = LightVolumes[i].LightVolumeInstance;
                if (lightVolumeInstance == null) continue;
                if(lightVolumeInstance.BoundsUvwMin0.w != 0 && lightVolumeInstance.BoundsUvwMin1.w != 0 && lightVolumeInstance.BoundsUvwMin2.w != 0) {
                    continue; // This is already NOT Legacy UVW, skip
                }

                Vector3 scale = lightVolumeInstance.BoundsUvwMax0 - lightVolumeInstance.BoundsUvwMin0;
                Vector3 uvwMin0 = lightVolumeInstance.BoundsUvwMin0;
                Vector3 uvwMin1 = lightVolumeInstance.BoundsUvwMin1;
                Vector3 uvwMin2 = lightVolumeInstance.BoundsUvwMin2;

                lightVolumeInstance.BoundsUvwMin0 = new Vector4(uvwMin0.x, uvwMin0.y, uvwMin0.z, scale.x);
                lightVolumeInstance.BoundsUvwMin1 = new Vector4(uvwMin1.x, uvwMin1.y, uvwMin1.z, scale.y);
                lightVolumeInstance.BoundsUvwMin2 = new Vector4(uvwMin2.x, uvwMin2.y, uvwMin2.z, scale.z);

                LVUtils.MarkDirty(lightVolumeInstance);
            }

            IsLegacyUVWConverted = true;

        }

        

        // Generates atlas and setups udon script
        public void GenerateAtlas() {

            if (LVUtils.IsInPrefabAsset(this) || LightVolumes.Count == 0 || DontSync) return;

            SetupDependencies();

            if(_generateAtlasCoroutine != null) { // Stop old coroutine in case one is in process already
                EditorCoroutineUtility.StopCoroutine(_generateAtlasCoroutine);
                _generateAtlasCoroutine = null;
            }

            // @pimaker: If there are post processors, the 3D texture will run through a Custom Render Texture every frame.
            // Unity dispatches CRT renders on 3D textures in slices by depth, so we want to reduce the z axis of the atlas
            // as much as possible to reduce per-frame drawcalls - even at the cost of slightly higher VRAM efficiency.
            var packingStrategy = AtlasPostProcessors != null && AtlasPostProcessors.Length > 0 && AtlasPostProcessors.Any(pp => pp.RT is CustomRenderTexture)
                ? TexturePackingStrategy.MinimumDepth : TexturePackingStrategy.MinimumVRAM;

            _generateAtlasCoroutine = EditorCoroutineUtility.StartCoroutine(Texture3DAtlasGenerator.CreateAtlas(LightVolumes.ToArray(), (Atlas3D atlas) => {

                if (atlas.Texture == null || DontSync) return; // Return if atlas packing failed

                LightVolumeManager.LightVolumeAtlasBase = atlas.Texture;
                UpdateAtlasPostProcessors();

                LightVolumeDataList.Clear();

                int lvCount = (int)Mathf.Min(LightVolumes.Count, Mathf.Min(Mathf.Floor(atlas.BoundsUvwMax.Length / 3), Mathf.Floor(atlas.BoundsUvwMin.Length / 3)));
                for (int i = 0; i < lvCount; i++) {

                    if (LightVolumes[i] == null) continue;
                    var lightVolumeInstance = LightVolumes[i].LightVolumeInstance;

                    if (lightVolumeInstance == null) continue;

                    int atlasIndex = i * 3;
                    Vector3 scale = atlas.BoundsUvwMax[atlasIndex] - atlas.BoundsUvwMin[atlasIndex];
                    Vector3 uvwMin0 = atlas.BoundsUvwMin[atlasIndex];
                    Vector3 uvwMin1 = atlas.BoundsUvwMin[atlasIndex + 1];
                    Vector3 uvwMin2 = atlas.BoundsUvwMin[atlasIndex + 2];
#if UDONSHARP
                    if (Application.isPlaying) {

                        UdonBehaviour lightVolumeBehaviour = lightVolumeInstance.GetComponent<UdonBehaviour>();

                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMin0", new Vector4(uvwMin0.x, uvwMin0.y, uvwMin0.z, scale.x));
                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMin1", new Vector4(uvwMin1.x, uvwMin1.y, uvwMin1.z, scale.y));
                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMin2", new Vector4(uvwMin2.x, uvwMin2.y, uvwMin2.z, scale.z));

                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMax0", (Vector4) atlas.BoundsUvwMax[atlasIndex]);
                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMax1", (Vector4) atlas.BoundsUvwMax[atlasIndex + 1]);
                        lightVolumeBehaviour.SetProgramVariable("BoundsUvwMax2", (Vector4) atlas.BoundsUvwMax[atlasIndex + 2]);

                    } else {
#endif
                        lightVolumeInstance.BoundsUvwMin0 = new Vector4(uvwMin0.x, uvwMin0.y, uvwMin0.z, scale.x);
                        lightVolumeInstance.BoundsUvwMin1 = new Vector4(uvwMin1.x, uvwMin1.y, uvwMin1.z, scale.y);
                        lightVolumeInstance.BoundsUvwMin2 = new Vector4(uvwMin2.x, uvwMin2.y, uvwMin2.z, scale.z);

                        // Legacy
                        lightVolumeInstance.BoundsUvwMax0 = (Vector4) atlas.BoundsUvwMax[atlasIndex];
                        lightVolumeInstance.BoundsUvwMax1 = (Vector4) atlas.BoundsUvwMax[atlasIndex + 1];
                        lightVolumeInstance.BoundsUvwMax2 = (Vector4) atlas.BoundsUvwMax[atlasIndex + 2];
#if UDONSHARP
                    }
#endif
                    LightVolumeDataList.Add(new LightVolumeData(i < LightVolumesWeights.Count ? LightVolumesWeights[i] : 0, lightVolumeInstance));

                    LVUtils.MarkDirty(lightVolumeInstance);
                }

                LVUtils.SaveAsAssetDelayed(atlas.Texture, $"{Path.GetDirectoryName(SceneManager.GetActiveScene().path)}/{SceneManager.GetActiveScene().name}/VRCLightVolumes/LightVolumeAtlas.asset");

                SyncUdonScript();

                _generateAtlasCoroutine = null;

            }, (int)DownscaleVolumes, packingStrategy), this);

        }

        // Looks for LightVolumeManager udon script and setups it if needed
        public void SetupDependencies() {
            if (this == null || gameObject == null || DontSync) return;
            if (LightVolumeManager == null && !TryGetComponent(out LightVolumeManager)) {
                LightVolumeManager = gameObject.AddComponent<LightVolumeManager>();
            }
#if UDONSHARP
            if (_lightVolumeManagerBehaviour == null) {
                TryGetComponent(out _lightVolumeManagerBehaviour);
            }
#endif
        }

        // Fixes light probes baked with Bakery L1
        private static void FixLightProbes() {

            var probes = LightmapSettings.lightProbes;
            if (probes == null || probes.count == 0) {
                Debug.LogWarning("[LightVolumeSetup] No Light Probes found to fix.");
                return;
            } else if (LVUtils.CheckSHL2(probes.bakedProbes[0])) {
                Debug.Log("[LightVolumeSetup] L2 Light Probes detected - no need to apply L1 Bakery fix.");
                return;
            }

            var shs = probes.bakedProbes;
            for (int i = 0; i < shs.Length; ++i) {
                shs[i] = LVUtils.DeringSH(shs[i]);
            }

            probes.bakedProbes = shs;
            EditorUtility.SetDirty(probes);
            EditorSceneManager.MarkAllScenesDirty();

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveOpenScenes();

            Debug.Log($"[LightVolumeSetup] {shs.Length} Light Probes fixed!");

        }

#endif

        // Syncs udon LightVolumeManager script with this script
        public void SyncUdonScript() {
#if UNITY_EDITOR
            SetupDependencies();
#endif
            if (LightVolumeManager == null || DontSync) return;
#if UDONSHARP
            if (Application.isPlaying) {

                // To sync variables in play-mode, we need to do it directly to the UdonBehaviour
                _lightVolumeManagerBehaviour.SetProgramVariable("AutoUpdateVolumes", AutoUpdateVolumes);
                _lightVolumeManagerBehaviour.SetProgramVariable("LightProbesBlending", LightProbesBlending);
                _lightVolumeManagerBehaviour.SetProgramVariable("SharpBounds", SharpBounds);
                _lightVolumeManagerBehaviour.SetProgramVariable("AdditiveMaxOverdraw", AdditiveMaxOverdraw);
                _lightVolumeManagerBehaviour.SetProgramVariable("AreaLightBrightnessCutoff", BrightnessCutoff);
                _lightVolumeManagerBehaviour.SetProgramVariable("ShadowResolution", (float)ShadowResolution);
                _lightVolumeManagerBehaviour.SetProgramVariable("ForceSceneLighting", ForceSceneLighting);

                if (LightVolumes.Count != 0) {
                    var instances = LightVolumeDataSorter.GetData(LightVolumeDataSorter.SortData(LightVolumeDataList));
                    UdonBehaviour[] lightVolumeInstances = new UdonBehaviour[instances.Length];
                    for (int i = 0; i < instances.Length; i++) {
                        lightVolumeInstances[i] = instances[i].GetComponent<UdonBehaviour>();
                    }
                    _lightVolumeManagerBehaviour.SetProgramVariable("LightVolumeInstances", lightVolumeInstances);
                }

                if (PointLightVolumes.Count != 0) {
                    var instances = GetPointLightVolumeInstances();
                    UdonBehaviour[] pointLightVolumeInstances = new UdonBehaviour[instances.Length];
                    for (int i = 0; i < instances.Length; i++) {
                        pointLightVolumeInstances[i] = instances[i].GetComponent<UdonBehaviour>();
                    }
                    _lightVolumeManagerBehaviour.SetProgramVariable("PointLightVolumeInstances", pointLightVolumeInstances);
                }
                _lightVolumeManagerBehaviour.SendCustomEvent("UpdateVolumes");

            } else {
#endif
                LightVolumeManager.AutoUpdateVolumes = AutoUpdateVolumes;
                LightVolumeManager.LightProbesBlending = LightProbesBlending;
                LightVolumeManager.SharpBounds = SharpBounds;
                LightVolumeManager.AdditiveMaxOverdraw = AdditiveMaxOverdraw;
                LightVolumeManager.LightsBrightnessCutoff = BrightnessCutoff;
                LightVolumeManager.ShadowResolution = (float)ShadowResolution;
                LightVolumeManager.ForceSceneLighting = ForceSceneLighting;

                if (LightVolumes.Count != 0) {
                    LightVolumeManager.LightVolumeInstances = LightVolumeDataSorter.GetData(LightVolumeDataSorter.SortData(LightVolumeDataList));
                }

                if (PointLightVolumes.Count != 0) {
                    LightVolumeManager.PointLightVolumeInstances = GetPointLightVolumeInstances();
                }

                LightVolumeManager.UpdateVolumes();
#if UDONSHARP
            }
#endif
        }

        // All Non-udon mono behaviours should be destroyed in playmode
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CommitSudoku() {
            if (Application.isPlaying) {

                bool isDestroy = false;
                var s = FindObjectsByType<LightVolumeSetup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < s.Length; i++) {
                    if (!s[i].DestroyInPlayMode) {
                        s[i].DontSync = false;
                    } else {
                        isDestroy = true;
                    }
                }
                if(!isDestroy) return;

                // Killing Light Volumes
                var lvs = FindObjectsByType<LightVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < lvs.Length; i++) {
#if BAKERY_INCLUDED
                    if (lvs[i].BakeryVolume != null) Destroy(lvs[i].BakeryVolume.gameObject);
#endif
                    Destroy(lvs[i]);
                }

                // Killing Point Light Volumes
                var plvs = FindObjectsByType<PointLightVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < plvs.Length; i++) {
                    Destroy(plvs[i]);
                }

                // Sudoku
                for (int i = 0; i < s.Length; i++) {
                    Destroy(s[i]);
                }

            }
        }

        private PointLightVolumeInstance[] GetPointLightVolumeInstances() {
            List<PointLightVolumeInstance> list = new List<PointLightVolumeInstance>();
            int count = PointLightVolumes.Count;
            for (int i = 0; i < count; i++) {
                if(PointLightVolumes[i] == null || PointLightVolumes[i].PointLightVolumeInstance == null) continue;
                list.Add(PointLightVolumes[i].PointLightVolumeInstance);
            }
            return list.ToArray();
        }

#if UNITY_EDITOR
        // Registers a Custom Render Texture post processor for the Light Volume 3D atlas.
        public void RegisterPostProcessorCRT(CustomRenderTexture crt) {
            RegisterPostProcessorCRT(ref AtlasPostProcessors, crt, "", UpdateAtlasPostProcessors);
        }

        // Unregisters a Custom Render Texture post processor from the Light Volume 3D atlas.
        public void UnregisterPostProcessorCRT(CustomRenderTexture crt) => UnregisterPostProcessor(crt); // API backwards compat

        // Unregisters a post processor from the Light Volume 3D atlas.
        public void UnregisterPostProcessor(RenderTexture crt) {
            UnregisterPostProcessor(ref AtlasPostProcessors, crt, "", UpdateAtlasPostProcessors);
        }

        // Registers a Render Texture post processor for the Light Volume 3D atlas.
        public void RegisterPostProcessor(PostProcessor pp) {
            RegisterPostProcessor(ref AtlasPostProcessors, pp, "", UpdateAtlasPostProcessors);
        }

        // Registers a Custom Render Texture post processor for the Point Light Volume cookie texture array.
        public void RegisterCookiePostProcessorCRT(CustomRenderTexture crt) {
            RegisterPostProcessorCRT(ref CookiePostProcessors, crt, "cookie", UpdateCookiePostProcessors);
        }

        // Unregisters a Custom Render Texture post processor from the Point Light Volume cookie texture array.
        public void UnregisterCookiePostProcessorCRT(CustomRenderTexture crt) => UnregisterCookiePostProcessor(crt);

        // Unregisters a post processor from the Point Light Volume cookie texture array.
        public void UnregisterCookiePostProcessor(RenderTexture rt) {
            UnregisterPostProcessor(ref CookiePostProcessors, rt, "cookie", UpdateCookiePostProcessors);
        }

        // Registers a Render Texture post processor for the Point Light Volume cookie texture array.
        public void RegisterCookiePostProcessor(PostProcessor pp) {
            RegisterPostProcessor(ref CookiePostProcessors, pp, "cookie", UpdateCookiePostProcessors);
        }

        // Registers a Custom Render Texture post processor for the Point Light Volume shadow depth array.
        public void RegisterShadowPostProcessorCRT(CustomRenderTexture crt) {
            RegisterPostProcessorCRT(ref ShadowPostProcessors, crt, "shadow depth", UpdateShadowPostProcessors);
        }

        // Unregisters a Custom Render Texture post processor from the Point Light Volume shadow depth array.
        public void UnregisterShadowPostProcessorCRT(CustomRenderTexture crt) => UnregisterShadowPostProcessor(crt);

        // Unregisters a post processor from the Point Light Volume shadow depth array.
        public void UnregisterShadowPostProcessor(RenderTexture rt) {
            UnregisterPostProcessor(ref ShadowPostProcessors, rt, "shadow depth", UpdateShadowPostProcessors);
        }

        // Registers a Render Texture post processor for the Point Light Volume shadow depth array.
        public void RegisterShadowPostProcessor(PostProcessor pp) {
            RegisterPostProcessor(ref ShadowPostProcessors, pp, "shadow depth", UpdateShadowPostProcessors);
        }

        // Registers a Custom Render Texture post processor in a shared post processor list.
        private void RegisterPostProcessorCRT(ref PostProcessor[] postProcessors, CustomRenderTexture crt, string targetName, Action updatePostProcessors) {
            if (crt == null) return;
            postProcessors ??= new PostProcessor[0];
            if (ContainsPostProcessor(postProcessors, crt)) return;
            Array.Resize(ref postProcessors, postProcessors.Length + 1);
            postProcessors[^1] = new PostProcessor { RT = crt, Mat = crt.material, TextureName = "_MainTex", Update = crt.Update };
            Debug.Log($"[LightVolumeSetup] Registered {GetPostProcessorLogName(targetName)} CRT: {crt.name}");
            updatePostProcessors?.Invoke();
        }

        // Unregisters a post processor from a shared post processor list.
        private void UnregisterPostProcessor(ref PostProcessor[] postProcessors, RenderTexture rt, string targetName, Action updatePostProcessors) {
            if (rt == null || postProcessors == null) return;
            int index = Array.FindIndex(postProcessors, pp => pp.RT == rt);
            if (index < 0) return;
            PostProcessor[] newArray = new PostProcessor[postProcessors.Length - 1];
            for (int i = 0, j = 0; i < postProcessors.Length; i++) {
                if (i != index) {
                    newArray[j] = postProcessors[i];
                    j++;
                }
            }
            postProcessors = newArray;
            Debug.Log($"[LightVolumeSetup] Unregistered {GetPostProcessorLogName(targetName)}: {rt.name}");
            updatePostProcessors?.Invoke();
        }

        // Registers a Render Texture post processor in a shared post processor list.
        private void RegisterPostProcessor(ref PostProcessor[] postProcessors, PostProcessor pp, string targetName, Action updatePostProcessors) {
            if (pp.RT == null || pp.Mat == null) return;
            postProcessors ??= new PostProcessor[0];
            if (ContainsPostProcessor(postProcessors, pp.RT)) return;
            if (string.IsNullOrEmpty(pp.TextureName)) pp.TextureName = "_MainTex";
            Array.Resize(ref postProcessors, postProcessors.Length + 1);
            postProcessors[^1] = pp;
            Debug.Log($"[LightVolumeSetup] Registered {GetPostProcessorLogName(targetName)}: {pp.RT.name}");
            updatePostProcessors?.Invoke();
        }

        // Checks if a post processor render target is already registered.
        private static bool ContainsPostProcessor(PostProcessor[] postProcessors, RenderTexture rt) {
            for (int i = 0; i < postProcessors.Length; i++) {
                if (postProcessors[i].RT == rt) return true;
            }
            return false;
        }

        // Builds the display name used by post processor log messages.
        private static string GetPostProcessorLogName(string targetName) {
            return string.IsNullOrEmpty(targetName) ? "post processor" : $"{targetName} post processor";
        }

        // Updates the Light Volume 3D atlas post processor chain and stores its active output.
        private void UpdateAtlasPostProcessors() {
            if (LightVolumeManager == null) return;
            LightVolumeManager.LightVolumeAtlas = UpdatePostProcessorChain(
                AtlasPostProcessors,
                LightVolumeManager.LightVolumeAtlasBase,
                UnityEngine.Rendering.TextureDimension.Tex3D,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                FilterMode.Trilinear);
        }

        // Updates the Point Light Volume cookie texture array post processor chain and stores its active output.
        private void UpdateCookiePostProcessors() {
            if (LightVolumeManager == null) return;
            LightVolumeManager.CustomTextures = UpdatePostProcessorChain(
                CookiePostProcessors,
                LightVolumeManager.CustomTexturesBase,
                UnityEngine.Rendering.TextureDimension.Tex2DArray,
                UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat((TextureFormat)CookieFormat, true),
                FilterMode.Trilinear);
        }

        // Updates the Point Light Volume shadow depth array post processor chain and stores its active output.
        private void UpdateShadowPostProcessors() {
            if (LightVolumeManager == null) return;
            LightVolumeManager.ShadowTextures = UpdatePostProcessorChain(
                ShadowPostProcessors,
                LightVolumeManager.ShadowTexturesBase,
                UnityEngine.Rendering.TextureDimension.Tex2DArray,
                UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(GetShadowTextureFormat(), true),
                FilterMode.Point);
        }

        // Applies a post processor chain to a base texture and returns the last valid output.
        private Texture UpdatePostProcessorChain(PostProcessor[] postProcessors, Texture baseTexture, UnityEngine.Rendering.TextureDimension dimension, UnityEngine.Experimental.Rendering.GraphicsFormat graphicsFormat, FilterMode filterMode) {
            if (baseTexture == null || postProcessors == null || postProcessors.Length == 0) return baseTexture;

            Texture prevTexture = baseTexture;
            bool hasValidProcessor = false;
            for (int i = 0; i < postProcessors.Length; i++) {
                PostProcessor pp = postProcessors[i];
                RenderTexture rt = pp.RT;
                Material mat = pp.Mat;
                if (rt == null || mat == null) continue;

                SetupPostProcessorRenderTexture(rt, baseTexture, dimension, graphicsFormat, filterMode);

                string textureName = string.IsNullOrEmpty(pp.TextureName) ? "_MainTex" : pp.TextureName;
                mat.SetTexture(textureName, prevTexture);
                prevTexture = rt;
                hasValidProcessor = true;

                pp.Update?.Invoke();
            }

            return hasValidProcessor ? prevTexture : baseTexture;
        }

        // Enforces dimensions and format on a post processor render target before running its update.
        private static void SetupPostProcessorRenderTexture(RenderTexture rt, Texture baseTexture, UnityEngine.Rendering.TextureDimension dimension, UnityEngine.Experimental.Rendering.GraphicsFormat graphicsFormat, FilterMode filterMode) {
            rt.Release();
            rt.dimension = dimension;
            rt.graphicsFormat = graphicsFormat;
            rt.enableRandomWrite = false;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = filterMode;
            rt.anisoLevel = 0;
            rt.width = Mathf.Max(baseTexture.width, 1);
            rt.height = Mathf.Max(baseTexture.height, 1);
            rt.volumeDepth = Mathf.Max(GetTextureDepth(baseTexture), 1);
            if (rt is CustomRenderTexture crt) {
                crt.updateMode = CustomRenderTextureUpdateMode.Realtime;
            }
            rt.Create();
        }

        // Returns the depth or array-slice count for any texture type used by post processor chains.
        private static int GetTextureDepth(Texture texture) {
            if (texture is Texture3D texture3D) return texture3D.depth;
            if (texture is Texture2DArray textureArray) return textureArray.depth;
            if (texture is RenderTexture renderTexture) return renderTexture.volumeDepth;
            if (texture is Cubemap) return 6;
            return 1;
        }
#endif

        // Returns the selected shadow map texture format.
        public TextureFormat GetShadowTextureFormat() {
            return ShadowFormat == ShadowTextureFormat.RFloat ? TextureFormat.RFloat : TextureFormat.RHalf;
        }

        // Bakes all requested per-light shadow maps.
        public void BakeShadowMaps() {
#if UNITY_EDITOR
            bool isRebaked = false;
            for (int i = 0; i < PointLightVolumes.Count; i++) {
                PointLightVolume pointLightVolume = PointLightVolumes[i];
                if (pointLightVolume == null || !pointLightVolume.RebakeShadows) continue;
                bool isBaked = pointLightVolume.BakeShadowMap($"| {pointLightVolume.gameObject.name} ({i}/{PointLightVolumes.Count})", false);
                isRebaked = isRebaked || isBaked;
            }
            if (isRebaked) GenerateShadowTexturesArray();
#endif
        }

        public enum Baking {
            Progressive,
            Bakery
        }

        public enum TextureArrayFormat {
            RGBA32 = 4,
            RGBAHalf = 17,
            RGBAFloat = 20
        }

        public enum ShadowTextureFormat {
            RHalf,
            RFloat
        }

        public enum TextureArrayResolution {
            _16x16 = 16,
            _32x32 = 32,
            _64x64 = 64,
            _128x128 = 128,
            _256x256 = 256,
            _512x512 = 512,
            _1024x1024 = 1024,
            _2048x2048 = 2048
        }

        public enum Downscale {
            None = 0,
            x2 = 1,
            x4 = 2,
            x8 = 3
        }

    }
}
