using UnityEngine;
using UnityEngine.Rendering;

#if UDONSHARP
using UdonSharp;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
#endif

namespace VRCLightVolumes {
    [ExecuteAlways]
    [DefaultExecutionOrder(100)]
#if UDONSHARP
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightVolumeCookieAnimator : UdonSharpBehaviour
#else
    public class LightVolumeCookieAnimator : MonoBehaviour
#endif
    {
        [Tooltip("Light Volume Manager that owns the point light cookie texture array.")]
        public LightVolumeManager LightVolumeManager;
        [Tooltip("Point Light Volume Instances whose cookie texture slices should be animated.")]
        public PointLightVolumeInstance[] TargetPointLightVolumes = new PointLightVolumeInstance[0];
        [Tooltip("Texture, Render Texture, Custom Render Texture or Cubemap sources copied into matching target cookie slices every frame.")]
        public Texture[] SourceTextures = new Texture[0];
        [HideInInspector]
        public int[] SourceSlices = new int[0];
        [HideInInspector] public int[] SourceTextureDepths = new int[0];

        [HideInInspector]
        public PointLightVolumeInstance TargetPointLightVolume;
        [HideInInspector]
        public Texture SourceTexture;
        [HideInInspector] public int SourceSlice = 0;
        [Tooltip("Enables per-frame blitting into the selected cookie slice.")]
        public bool Animate = true;

        [HideInInspector] public Texture BaseCookieTexture;
        [HideInInspector] public RenderTexture RuntimeCookieArray;
        [HideInInspector] public int CookieArrayWidth = 0;
        [HideInInspector] public int CookieArrayHeight = 0;
        [HideInInspector] public int CookieArrayDepth = 0;
        [HideInInspector] public int SourceTextureDepth = 1;
        [HideInInspector] public RenderTextureFormat RuntimeFormat = RenderTextureFormat.ARGBHalf;
        [HideInInspector] public LightVolumeCookieAnimator SharedAnimator;
        [Tooltip("Material used to copy Cubemap source faces into the cookie texture array.")]
        [HideInInspector] public Material CubemapFaceMaterial;

        private bool _isInitialized = false;
        private bool _baseTextureCopied = false;
        private bool _cubemapShaderPropertiesInitialized = false;
        private int _cubemapMainTexID = 0;
        private int _cubemapFaceIndexID = 0;
#if !COMPILER_UDONSHARP
        private Material _cubemapFaceRuntimeMaterial;
#endif

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        public static System.Action<LightVolumeCookieAnimator> OnAnimatorValidated;

        // Requests editor registration refresh when inspector data changes.
        private void OnValidate() {
            _isInitialized = false;
            _baseTextureCopied = false;
            OnAnimatorValidated?.Invoke(this);
        }
#endif

        // Initializes the runtime cookie array and binds it to the Light Volume Manager.
        private void Start() {
#if COMPILER_UDONSHARP
            RuntimeCookieArray = null;
#endif
            Initialize();
        }

        // Initializes when the component is enabled from an inactive state.
        private void OnEnable() {
            _isInitialized = false;
            _baseTextureCopied = false;
        }

        // Restores the base cookie texture when this animator is disabled.
        private void OnDisable() {
            RestoreBaseCookieTexture();
#if !COMPILER_UDONSHARP
            DestroyCubemapFaceRuntimeMaterial();
#endif
        }

        // Updates one cookie slice every frame.
        private void Update() {
            UpdateAnimatedCookie();
        }

        // Updates the animated cookie slice when animation is enabled.
        public void UpdateAnimatedCookie() {
            if (!Animate) return;
            if (!_isInitialized) Initialize();
            if (RuntimeCookieArray != null && LightVolumeManager != null && LightVolumeManager.CustomTextures != RuntimeCookieArray) ApplyManagerCookieTexture(RuntimeCookieArray);
            BlitTargetCookies();
        }

        // Initializes this animator when another animator uses it as a shared runtime texture owner.
        public void InitializeAnimator() {
            Initialize();
        }

        // Configures dimensions used by the editor post processor registration.
        public bool ConfigureCookieArray(Texture baseCookieTexture, int width, int height, int depth, RenderTextureFormat runtimeFormat) {
            bool baseChanged = BaseCookieTexture != baseCookieTexture;
            BaseCookieTexture = baseCookieTexture;
            CookieArrayWidth = width;
            CookieArrayHeight = height;
            CookieArrayDepth = Mathf.Max(depth, 1);
            RuntimeFormat = runtimeFormat;
            if (baseChanged) _baseTextureCopied = false;
            if (SharedAnimator != null && SharedAnimator != this) return false;
            bool recreate = ShouldRecreateRuntimeCookieArray(CookieArrayWidth, CookieArrayHeight, CookieArrayDepth);
            EnsureRuntimeCookieArray();
            return recreate;
        }

        // Editor post processor callback that copies the previous cookie chain output once.
        public void UpdateCookiePostProcessor(Texture inputTexture) {
            if (inputTexture != null) {
                BaseCookieTexture = inputTexture;
                CookieArrayWidth = inputTexture.width;
                CookieArrayHeight = inputTexture.height;
#if !COMPILER_UDONSHARP
                CookieArrayDepth = GetFallbackTextureDepth();
#endif
            }
            if (!EnsureRuntimeCookieArray()) return;
            CopyBaseCookieTexture();
            if (Animate) BlitTargetCookies();
        }

        // Returns true when the target light uses a cookie texture this animator can replace.
        public bool HasValidTargetCookie() {
            int count = GetConfiguredEntryCount();
            for (int i = 0; i < count; i++) {
                if (IsSupportedTargetCookie(GetEntryTarget(i))) return true;
            }
            return false;
        }

        // Initializes the runtime texture from serialized editor data or manager fallback data.
        private void Initialize() {
#if !UDONSHARP
            if (LightVolumeManager == null) LightVolumeManager = FindObjectOfType<LightVolumeManager>();
#endif
            if (LightVolumeManager == null) return;

            if (SharedAnimator != null && SharedAnimator != this) {
                SharedAnimator.InitializeAnimator();
                if (SharedAnimator.RuntimeCookieArray == null) return;
                RuntimeCookieArray = SharedAnimator.RuntimeCookieArray;
                BaseCookieTexture = SharedAnimator.BaseCookieTexture;
                CookieArrayWidth = SharedAnimator.CookieArrayWidth;
                CookieArrayHeight = SharedAnimator.CookieArrayHeight;
                CookieArrayDepth = SharedAnimator.CookieArrayDepth;
                RuntimeFormat = SharedAnimator.RuntimeFormat;
                ApplyManagerBaseCookieTexture();
                ApplyManagerCookieTexture(RuntimeCookieArray);
                _isInitialized = true;
                return;
            }

            if (BaseCookieTexture == null) {
                BaseCookieTexture = LightVolumeManager.CustomTexturesBase != null ? LightVolumeManager.CustomTexturesBase : LightVolumeManager.CustomTextures;
            }

            if (!EnsureRuntimeCookieArray()) return;
            if (!_baseTextureCopied) CopyBaseCookieTexture();

            ApplyManagerBaseCookieTexture();
            ApplyManagerCookieTexture(RuntimeCookieArray);
            _isInitialized = true;
        }

        // Creates or recreates the runtime Texture2DArray render target if needed.
        private bool EnsureRuntimeCookieArray() {
            int width = CookieArrayWidth > 0 ? CookieArrayWidth : GetFallbackTextureWidth();
            int height = CookieArrayHeight > 0 ? CookieArrayHeight : GetFallbackTextureHeight();
            int depth = CookieArrayDepth > 0 ? CookieArrayDepth : GetFallbackTextureDepth();
            if (width <= 0 || height <= 0) return false;
            CookieArrayDepth = Mathf.Max(depth, 1);

            bool recreate = ShouldRecreateRuntimeCookieArray(width, height, CookieArrayDepth);
            if (!recreate) return RuntimeCookieArray != null;

#if !COMPILER_UDONSHARP
            if (RuntimeCookieArray != null) {
                RenderTexture.active = null;
                RuntimeCookieArray.Release();
            }
#endif

            RuntimeCookieArray = new RenderTexture(width, height, 0, RuntimeFormat, RenderTextureReadWrite.Linear);
            RuntimeCookieArray.dimension = TextureDimension.Tex2DArray;
            RuntimeCookieArray.volumeDepth = CookieArrayDepth;
            RuntimeCookieArray.useMipMap = false;
            RuntimeCookieArray.autoGenerateMips = false;
#if !COMPILER_UDONSHARP
            RuntimeCookieArray.name = "LightVolumeCookieAnimator_CookieArray";
            RuntimeCookieArray.enableRandomWrite = false;
            RuntimeCookieArray.wrapMode = TextureWrapMode.Clamp;
            RuntimeCookieArray.filterMode = FilterMode.Trilinear;
            RuntimeCookieArray.anisoLevel = 0;
#endif
            RuntimeCookieArray.Create();
            _baseTextureCopied = false;
            return true;
        }

        // Checks if the runtime cookie array must be created again.
        private bool ShouldRecreateRuntimeCookieArray(int width, int height, int depth) {
#if COMPILER_UDONSHARP
            return RuntimeCookieArray == null;
#else
            return RuntimeCookieArray == null || RuntimeCookieArray.width != width || RuntimeCookieArray.height != height || RuntimeCookieArray.volumeDepth != depth || !RuntimeCookieArray.IsCreated();
#endif
        }

        // Copies every base cookie slice once so untouched slices stay valid.
        private void CopyBaseCookieTexture() {
            if (BaseCookieTexture == null || RuntimeCookieArray == null) return;

            int depth = Mathf.Max(CookieArrayDepth, 1);
            for (int i = 0; i < depth; i++) {
                BlitTextureSlice(BaseCookieTexture, RuntimeCookieArray, i, i);
            }
            _baseTextureCopied = true;
        }

        // Blits every valid animated source into its matching target light cookie slice.
        private void BlitTargetCookies() {
            if (RuntimeCookieArray == null) return;
            int count = GetConfiguredEntryCount();
            for (int i = 0; i < count; i++) {
                BlitCookieEntry(i);
            }
        }

        // Blits one configured cookie animation entry.
        private void BlitCookieEntry(int entryIndex) {
            Texture sourceTexture = GetEntrySourceTexture(entryIndex);
            PointLightVolumeInstance targetLight = GetEntryTarget(entryIndex);
            if (sourceTexture == null || !IsSupportedTargetCookie(targetLight)) return;

            int customTextureId = -(int)targetLight.CustomID - 1;
            if (customTextureId < 0) return;
            if (HasDuplicateTargetBefore(entryIndex, targetLight, customTextureId)) return;

            if (targetLight.IsSpotLight()) {
                int targetSlice = LightVolumeManager.CubemapsCount * 5 + customTextureId;
                if (IsValidTargetSlice(targetSlice)) BlitSourceSlice(sourceTexture, RuntimeCookieArray, GetEntrySourceSlice(entryIndex, 0), targetSlice);
                return;
            }

            if (customTextureId >= LightVolumeManager.CubemapsCount) return;
            int firstTargetSlice = customTextureId * 6;
            for (int i = 0; i < 6; i++) {
                int targetSlice = firstTargetSlice + i;
                if (IsValidTargetSlice(targetSlice)) BlitSourceSlice(sourceTexture, RuntimeCookieArray, GetEntrySourceSlice(entryIndex, i), targetSlice);
            }
        }

        // Returns true when the entry duplicates an earlier target light or cookie ID.
        private bool HasDuplicateTargetBefore(int entryIndex, PointLightVolumeInstance targetLight, int customTextureId) {
            for (int i = 0; i < entryIndex; i++) {
                PointLightVolumeInstance otherTarget = GetEntryTarget(i);
                if (otherTarget == targetLight) return true;
                if (!IsSupportedTargetCookie(otherTarget)) continue;
                int otherTextureId = -(int)otherTarget.CustomID - 1;
                if (otherTextureId == customTextureId) return true;
            }
            return false;
        }

        // Checks if a point light volume can provide a cookie array slice.
        private bool IsSupportedTargetCookie(PointLightVolumeInstance targetLight) {
            if (LightVolumeManager == null || targetLight == null) return false;
            if (!targetLight.IsCustomTexture()) return false;
            return targetLight.IsPointLight() || targetLight.IsSpotLight();
        }

        // Restores the manager texture reference if it still points to this animator output.
        private void RestoreBaseCookieTexture() {
            if (SharedAnimator != null && SharedAnimator != this) return;
            if (LightVolumeManager == null || RuntimeCookieArray == null) return;
            if (LightVolumeManager.CustomTextures != RuntimeCookieArray) return;
            ApplyManagerCookieTexture(BaseCookieTexture);
        }

        // Stores the base cookie texture on the Udon manager so runtime fallback data stays available.
        private void ApplyManagerBaseCookieTexture() {
            if (LightVolumeManager == null || BaseCookieTexture == null) return;
            LightVolumeManager.CustomTexturesBase = BaseCookieTexture;
#if COMPILER_UDONSHARP
            LightVolumeManager.SetProgramVariable("CustomTexturesBase", BaseCookieTexture);
#endif
        }

        // Applies an active cookie texture to the manager through Udon variables in play mode.
        private void ApplyManagerCookieTexture(Texture texture) {
            if (LightVolumeManager == null) return;
            LightVolumeManager.CustomTextures = texture;
#if COMPILER_UDONSHARP
            LightVolumeManager.SetProgramVariable("CustomTextures", texture);
            LightVolumeManager.SendCustomEvent("UpdateVolumes");
#else
            LightVolumeManager.UpdateVolumes();
#endif
        }

        // Returns a valid source slice for 2D, cubemap and 2DArray source textures.
        private int GetEntrySourceSlice(int entryIndex, int faceOffset) {
            int sourceDepth = GetEntrySourceDepth(entryIndex);
            if (sourceDepth >= faceOffset + 1) return faceOffset;
            return 0;
        }

        // Returns the serialized source depth for one entry.
        private int GetEntrySourceDepth(int entryIndex) {
            int sourceDepth = SourceTextureDepth;
            if (SourceTextureDepths != null && entryIndex < SourceTextureDepths.Length) sourceDepth = SourceTextureDepths[entryIndex];
            Texture sourceTexture = GetEntrySourceTexture(entryIndex);
            if (sourceTexture != null && sourceTexture.dimension == TextureDimension.Cube && sourceDepth < 6) sourceDepth = 6;
#if !COMPILER_UDONSHARP
            int runtimeDepth = GetSourceTextureDepth(sourceTexture);
            if (runtimeDepth > sourceDepth) sourceDepth = runtimeDepth;
#endif
            return Mathf.Max(sourceDepth, 1);
        }

        // Returns how many animation entries are configured, including the legacy single-entry fallback.
        private int GetConfiguredEntryCount() {
            int count = 0;
            if (TargetPointLightVolumes != null && TargetPointLightVolumes.Length > count) count = TargetPointLightVolumes.Length;
            if (SourceTextures != null && SourceTextures.Length > count) count = SourceTextures.Length;
            if (count > 0) return count;
            if (TargetPointLightVolume != null || SourceTexture != null) return 1;
            return 0;
        }

        // Returns the target point light for an entry.
        private PointLightVolumeInstance GetEntryTarget(int entryIndex) {
            if (TargetPointLightVolumes != null && entryIndex < TargetPointLightVolumes.Length) return TargetPointLightVolumes[entryIndex];
            if (entryIndex == 0) return TargetPointLightVolume;
            return null;
        }

        // Returns the source texture for an entry.
        private Texture GetEntrySourceTexture(int entryIndex) {
            if (SourceTextures != null && entryIndex < SourceTextures.Length) return SourceTextures[entryIndex];
            if (entryIndex == 0) return SourceTexture;
            return null;
        }

        // Returns the first configured source texture.
        private Texture GetFirstSourceTexture() {
            int count = GetConfiguredEntryCount();
            for (int i = 0; i < count; i++) {
                Texture source = GetEntrySourceTexture(i);
                if (source != null) return source;
            }
            return null;
        }

        // Checks if a destination slice exists in the runtime cookie array.
        private bool IsValidTargetSlice(int slice) {
            return slice >= 0 && slice < Mathf.Max(CookieArrayDepth, 1);
        }

        // Returns a fallback width from known textures.
        private int GetFallbackTextureWidth() {
            if (BaseCookieTexture != null) return BaseCookieTexture.width;
            Texture source = GetFirstSourceTexture();
            if (source != null) return source.width;
            return 0;
        }

        // Returns a fallback height from known textures.
        private int GetFallbackTextureHeight() {
            if (BaseCookieTexture != null) return BaseCookieTexture.height;
            Texture source = GetFirstSourceTexture();
            if (source != null) return source.height;
            return 0;
        }

        // Returns a fallback slice count from known base and source textures.
        private int GetFallbackTextureDepth() {
            if (BaseCookieTexture != null) return GetKnownTextureDepth(BaseCookieTexture);
            return GetKnownTextureDepth(GetFirstSourceTexture());
        }

        // Returns a conservative depth for texture types available in both Udon and Unity editor.
        private int GetKnownTextureDepth(Texture texture) {
            if (texture == null) return 1;
            if (texture.dimension == TextureDimension.Cube) return 6;
#if !COMPILER_UDONSHARP
            return GetSourceTextureDepth(texture);
#else
            return 1;
#endif
        }

#if !COMPILER_UDONSHARP
        // Returns the depth or face count for editor-side texture types.
        private int GetSourceTextureDepth(Texture texture) {
            if (texture == null) return 1;
            if (texture.dimension == TextureDimension.Cube) return 6;
            if (texture is Texture2DArray textureArray) return Mathf.Max(textureArray.depth, 1);
            if (texture is RenderTexture renderTexture) return Mathf.Max(renderTexture.volumeDepth, 1);
            return 1;
        }
#endif

        // Copies a source slice or cubemap face into one destination slice.
        private void BlitSourceSlice(Texture source, RenderTexture destination, int sourceSlice, int destinationSlice) {
            if (source == null || destination == null) return;
            if (source.dimension == TextureDimension.Cube) {
                BlitCubemapFace(source, destination, sourceSlice, destinationSlice);
                return;
            }

            BlitTextureSlice(source, destination, sourceSlice, destinationSlice);
        }

        // Copies one cubemap face into one Texture2DArray slice using the shared face unwrap shader.
        private void BlitCubemapFace(Texture source, RenderTexture destination, int sourceFace, int destinationSlice) {
            if (!EnsureCubemapFaceMaterial()) return;
            InitializeCubemapShaderProperties();

            Material cubemapFaceMaterial = GetCubemapFaceMaterial();
            cubemapFaceMaterial.SetTexture(_cubemapMainTexID, source);
            cubemapFaceMaterial.SetInt(_cubemapFaceIndexID, Mathf.Clamp(sourceFace, 0, 5));

#if COMPILER_UDONSHARP
            PrepareUdonCubemapBlitTargetSlice(source, destination, destinationSlice);
            VRCGraphics.Blit(source, cubemapFaceMaterial, 0, destinationSlice);
#else
            RenderTexture previousRenderTexture = RenderTexture.active;
            Graphics.SetRenderTarget(destination, 0, CubemapFace.Unknown, destinationSlice);
            Graphics.Blit(source, cubemapFaceMaterial, 0);
            RenderTexture.active = previousRenderTexture;
#endif
        }

#if COMPILER_UDONSHARP
        // Prepares the active render target slice for VRCGraphics' material blit overload.
        private void PrepareUdonCubemapBlitTargetSlice(Texture source, RenderTexture destination, int destinationSlice) {
            Texture prepareSource = BaseCookieTexture != null ? BaseCookieTexture : source;
            VRCGraphics.Blit(prepareSource, destination, 0, destinationSlice);
        }
#endif

        // Finds or lazily creates the cubemap face material outside Udon.
        private bool EnsureCubemapFaceMaterial() {
            if (CubemapFaceMaterial != null) {
#if !COMPILER_UDONSHARP
                if (_cubemapFaceRuntimeMaterial == null) {
                    _cubemapFaceRuntimeMaterial = new Material(CubemapFaceMaterial);
                    _cubemapFaceRuntimeMaterial.hideFlags = HideFlags.HideAndDontSave;
                }
#endif
                return true;
            }
#if !COMPILER_UDONSHARP
            if (_cubemapFaceRuntimeMaterial != null) return true;
            Shader shader = Shader.Find("Hidden/CubeFace");
            if (shader == null) return false;
            _cubemapFaceRuntimeMaterial = new Material(shader);
            _cubemapFaceRuntimeMaterial.hideFlags = HideFlags.HideAndDontSave;
            return true;
#else
            return false;
#endif
        }

        // Returns the material instance that should be mutated for the current runtime.
        private Material GetCubemapFaceMaterial() {
#if COMPILER_UDONSHARP
            return CubemapFaceMaterial;
#else
            return _cubemapFaceRuntimeMaterial;
#endif
        }

#if !COMPILER_UDONSHARP
        // Destroys the editor/runtime material instance used by non-Udon execution.
        private void DestroyCubemapFaceRuntimeMaterial() {
            if (_cubemapFaceRuntimeMaterial == null) return;
            if (Application.isPlaying) Destroy(_cubemapFaceRuntimeMaterial);
            else DestroyImmediate(_cubemapFaceRuntimeMaterial);
            _cubemapFaceRuntimeMaterial = null;
        }
#endif

        // Caches shader property IDs used by cubemap face unwrapping.
        private void InitializeCubemapShaderProperties() {
            if (_cubemapShaderPropertiesInitialized) return;
#if COMPILER_UDONSHARP
            _cubemapMainTexID = VRCShader.PropertyToID("_MainTex");
            _cubemapFaceIndexID = VRCShader.PropertyToID("_FaceIndex");
#else
            _cubemapMainTexID = Shader.PropertyToID("_MainTex");
            _cubemapFaceIndexID = Shader.PropertyToID("_FaceIndex");
#endif
            _cubemapShaderPropertiesInitialized = true;
        }

        // Copies a texture slice using the fastest API available in the current runtime.
        private static void BlitTextureSlice(Texture source, RenderTexture destination, int sourceSlice, int destinationSlice) {
#if COMPILER_UDONSHARP
            VRCGraphics.Blit(source, destination, sourceSlice, destinationSlice);
#else
            Graphics.Blit(source, destination, sourceSlice, destinationSlice);
#endif
        }
    }
}
