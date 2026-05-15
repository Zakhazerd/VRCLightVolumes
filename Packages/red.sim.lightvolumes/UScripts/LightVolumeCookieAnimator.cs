using UnityEngine;
using UnityEngine.Rendering;

#if UDONSHARP
using UdonSharp;
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
        [Tooltip("Point Light Volume Instance whose cookie texture should be animated.")]
        public PointLightVolumeInstance TargetPointLightVolume;
        [Tooltip("Texture, Render Texture or Custom Render Texture copied into the target cookie slice every frame.")]
        public Texture SourceTexture;
        [Tooltip("First source slice used when Source Texture is a Texture2DArray or a 2DArray RenderTexture.")]
        [Min(0)] public int SourceSlice = 0;
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

        private bool _isInitialized = false;
        private bool _baseTextureCopied = false;

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
            BlitTargetCookie();
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
            if (Animate) BlitTargetCookie();
        }

        // Returns true when the target light uses a cookie texture this animator can replace.
        public bool HasValidTargetCookie() {
            if (LightVolumeManager == null || TargetPointLightVolume == null) return false;
            if (!TargetPointLightVolume.IsCustomTexture()) return false;
            return TargetPointLightVolume.IsPointLight() || TargetPointLightVolume.IsSpotLight();
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

        // Blits the animated source into the target light cookie slice.
        private void BlitTargetCookie() {
            if (RuntimeCookieArray == null || SourceTexture == null || !HasValidTargetCookie()) return;

            int customTextureId = -(int)TargetPointLightVolume.CustomID - 1;
            if (customTextureId < 0) return;

            if (TargetPointLightVolume.IsSpotLight()) {
                int targetSlice = LightVolumeManager.CubemapsCount * 5 + customTextureId;
                if (IsValidTargetSlice(targetSlice)) BlitTextureSlice(SourceTexture, RuntimeCookieArray, GetSourceSlice(0), targetSlice);
                return;
            }

            if (customTextureId >= LightVolumeManager.CubemapsCount) return;
            int firstTargetSlice = customTextureId * 6;
            for (int i = 0; i < 6; i++) {
                int targetSlice = firstTargetSlice + i;
                if (IsValidTargetSlice(targetSlice)) BlitTextureSlice(SourceTexture, RuntimeCookieArray, GetSourceSlice(i), targetSlice);
            }
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
            return;
#endif
            LightVolumeManager.UpdateVolumes();
        }

        // Returns a valid source slice for 2D and 2DArray source textures.
        private int GetSourceSlice(int faceOffset) {
            int sourceDepth = Mathf.Max(SourceTextureDepth, 1);
            int slice = SourceSlice;
            if (sourceDepth >= SourceSlice + faceOffset + 1) slice = SourceSlice + faceOffset;
            if (slice >= sourceDepth) return 0;
            return slice;
        }

        // Checks if a destination slice exists in the runtime cookie array.
        private bool IsValidTargetSlice(int slice) {
            return slice >= 0 && slice < Mathf.Max(CookieArrayDepth, 1);
        }

        // Returns a fallback width from known textures.
        private int GetFallbackTextureWidth() {
            if (BaseCookieTexture != null) return BaseCookieTexture.width;
            if (SourceTexture != null) return SourceTexture.width;
            return 0;
        }

        // Returns a fallback height from known textures.
        private int GetFallbackTextureHeight() {
            if (BaseCookieTexture != null) return BaseCookieTexture.height;
            if (SourceTexture != null) return SourceTexture.height;
            return 0;
        }

        // Returns a fallback slice count from known base textures.
        private int GetFallbackTextureDepth() {
            if (BaseCookieTexture == null) return 1;
            if (BaseCookieTexture.dimension == TextureDimension.Cube) return 6;
#if !COMPILER_UDONSHARP
            if (BaseCookieTexture is Texture2DArray textureArray) return Mathf.Max(textureArray.depth, 1);
            if (BaseCookieTexture is RenderTexture renderTexture) return Mathf.Max(renderTexture.volumeDepth, 1);
#endif
            return 1;
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
