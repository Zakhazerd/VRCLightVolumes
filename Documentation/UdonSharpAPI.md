[VRC Light Volumes](../README.md) | [How to Use](../Documentation/HowToUse.md) | [Best Practices](../Documentation/BestPractices.md) | **Udon Sharp API** | [For Shader Developers](../Documentation/ForShaderDevelopers.md) | [Compatible Shaders](../Documentation/CompatibleShaders.md)

# Udon Sharp API

| Menu |
| --- |
|**Udon Sharp API**<br />• [Light Volume Manager](#LightVolumeManager)<br />• [Light Volume Instance](#LightVolumeInstance)<br />• [Point Light Volume Instance](#PointLightVolumeInstance)|

## LightVolumeManager
Stores the light volumes 3D atlas and references to all of the Light Volume Instances. Controls and updates all the Light Volumes in your scene

### Public Fields
| Public Field | Description |
| --- | --- |
|`Texture LightVolumeAtlas` | Combined Texture containing all Light Volumes' baked SH data. |
|`Texture3D LightVolumeAtlasBase` | Combined Texture3D containing all Light Volumes' baked SH data. This field is not used at runtime, see LightVolumeAtlas instead. It specifies the base for the post process chain, if given. |
|`CustomRenderTexture[] AtlasPostProcessors` | Custom Render Textures that will be applied top to bottom to the Light Volume Atlas at runtime. External scripts can register themselves here using `RegisterPostProcessorCRT()` method in `LightVolumeSetup`. You probably don't want to mess with this field manually. |
|`bool LightProbesBlending` | When enabled, areas outside Light Volumes fall back to light probes. Otherwise, the Light Volume with the smallest weight is used as fallback. It also improves performance. |
|`bool SharpBounds` | Disables smooth blending with areas outside Light Volumes. Use it if your entire scene's play area is covered by Light Volumes. It also improves performance. |
|`bool AutoUpdateVolumes` | Automatically updates a volume's position, rotation, and scale in Play mode using an Udon script. Use only if you have movable volumes in your scene. |
|`int AdditiveMaxOverdraw` | Limits the maximum number of additive volumes that can affect a single pixel. If you have many dynamic additive volumes that may overlap, it's good practice to limit overdraw to maintain performance. |
|`bool ForceSceneLighting` | Disables min/max brightness limits for modern avatar shaders such as lilToon or Poiyomi. Enable only if you're sure your scene lighting is properly configured. |
| `float LightsBrightnessCutoff` | The minimum brightness at a point due to lighting from a Point Light Volume, before the light is culled. Larger values will result in better performance, but light attenuation will be less physically correct. You should never set it to zero or below, otherwise lights will never be culled at all. |
|`LightVolumeInstance[] LightVolumeInstances` | All Light Volume instances sorted in decreasing order by weight. You can enable or disable volumes game objects at runtime. Manually disabling unnecessary volumes improves performance. |
|`Texture CustomTextures` | Texture array that can be used for as Cubemaps, LUT or Cookies, stored in a single Texture Array. Faces of the used cubemaps always stores first.|
|`int CubemapsCount` | Cubemaps count that stored in CustomTextures texture array. Cubemaps faces starts from the beginning, 6 elements per each cubemap.|
|`bool IsRangeDirty` | Flag that defines if range of all of the point lights should be recalculated in the next frame. Recalculates automatically when `AutoUpdateVolumes` is enabled. Resets to false after being recalculated.|

### Public Methods
| Public Method | Description |
| --- | --- |
|`void UpdateVolumes()` | Method that updates all the volumes global shader parameters. Useful if you want manually update volumes instead of having **AutoUpdateVolumes** enabled.|


## LightVolumeInstance
Stores all the volume configuration including 3D UVs, world transform, color, etc.

### Public Fields
| Public Field | Description |
| --- | --- |
|`Color Color` | Multiplies volumes color by this value. Changing the color is useful for animating Additive volumes. You can even control the R, G, B channels separately this way.|
|`float Intensity` | Color multiplies by this value. Basically, controls the brightness.|
|`bool IsDynamic` | Defines whether this volume can be moved in runtime. Disabling this option slightly improves performance. You can even change it in runtime.|
|`bool IsAdditive` | Additive volumes apply their light on top of others as an overlay. Useful for movable lights like flashlights, projectors, disco balls, etc. They can also project light onto static lightmapped objects if the surface shader supports it.|
|`Quaternion InvBakedRotation` | Inverse rotation of the pose the volume was baked in. Automatically recalculated for dynamic volumes with auto-update, or manually via the **UpdateRotation()** method.|
|`Vector4 BoundsUvwMin0` | Min bounds of Texture0 in 3D atlas space.|
|`Vector4 BoundsUvwMin1` | Min bounds of Texture1 in 3D atlas space.|
|`Vector4 BoundsUvwMin2` | Min bounds of Texture2 in 3D atlas space.|
|`Vector4 BoundsUvwMax0` | Max bounds of Texture0 in 3D atlas space.|
|`Vector4 BoundsUvwMax1` | Max bounds of Texture1 in 3D atlas space.|
|`Vector4 BoundsUvwMax2` | Max bounds of Texture2 in 3D atlas space.|
|`Vector4 InvLocalEdgeSmoothing` | Inversed edge smoothing in 3D atlas space. Recalculates via **SetSmoothBlending(float radius)** method.|
|`Matrix4x4 InvWorldMatrix` | Inversed TRS matrix of this volume that transforms it into the 1x1x1 cube. Recalculates via the **UpdateRotation()** method.|
|`Vector3 RelativeRotationRow0` | Current volume's rotation matrix row 0 relative to the rotation it was baked with. Mandatory for dynamic volumes. Recalculates via the UpdateRotation() method.|
|`Vector3 RelativeRotationRow1` | Current volume's rotation matrix row 1 relative to the rotation it was baked with. Mandatory for dynamic volumes. Recalculates via the UpdateRotation() method.|
|`bool IsRotated` | True if there is any relative rotation. No relative rotation improves performance. Recalculated via the **UpdateRotation()** method.|
|`bool BakeOcclusion` | True if the volume has baked shadow mask.|
|`bool IsInitialized` | True if this Light Volume added to the **Light Volumes** array in **LightVolumeManager**. Should be always true for the Light Volumes placed in editor. Helps to initialize Light Volumes spawned in runtime.|
|`LightVolumeManager LightVolumeManager` | Reference to the Light Volume Manager. Needed for runtime initialization.|

### Public Methods
| Public Method | Description |
| --- | --- |
|`void SetSmoothBlending(float radius)` | Calculates **InvLocalEdgeSmoothing** value. Execute it if you want to control edge smoothing in runtime. You can even control every direction independent if it's needed.|
|`void UpdateTransform()` | Recalculates **InvWorldMatrix**, **RelativeRotationRow0** and **RelativeRotationRow1**. Executes automatically from **LightVolumeManager.UpdateVolumes()** or while **LightVolumeManager.AutoUpdateVolumes** enabled. Usually don't need to be executed manually.|

## PointLightVolumeInstance

Stores all the point light volume configuration including Color, Position, Direction, Custom Texture ID, etc.

### Public Fields
| Public Field | Description |
| --- | --- |
|`Color Color` | Point light volume color.|
|`float Intensity` | Color multiplies by this value. Basically, controls the brightness.|
|`bool IsDynamic` | Defines whether this point light volume can be moved in runtime. Disabling this option slightly improves performance.|
|`Vector4 PositionData` | **For point light:** XYZ = Position, W = Squared light source size. **For spot light:** XYZ = Position, W = Squared light source size, or negated inverse squared range when in LUT mode. **For area light:** XYZ = Position, W = Width.|
|`Vector4 DirectionData` | **For point light:** XYZW = Rotation quaternion. **For spot light:** XYZ = Direction, W = Cone falloff. **For area light:** XYZW = Rotation quaternion.|
|`float CustomID` | **If parametric:** Stores 0. **If uses custom LUT**: Stores LUT ID with positive sign. **If uses custom texture:** Stores texture ID with negative sign.|
|`float Angle` | Half-angle of the spotlight cone, in radians.|
|`float AngleData` | **For point light:** Cos of angle (for LUT). **For spot light:** Cos of outer angle if no custom texture, tan of outer angle otherwise. **For area light:** 2 + Height.|
|`sbyte ShadowmaskIndex` | Index of the shadowmask channel used by this light. -1 means no shadowmask.|
|`bool IsInitialized` | True if this Point Light Volume added to the **Point Light Volumes** array in **LightVolumeManager**. Should be always true for the Point Light Volumes placed in editor. Helps to initialize Point Light Volumes spawned in runtime.|
|`float SquaredRange` | Squared range after which light will be culled. Should be recalculated by executing **UpdateRange()** method.|
|`float SquaredScale` | Average squared lossy scale of the light. **Light Source Size** gets multiplied by it at the end. Updates with **UpdateTransform()** method.|
|`LightVolumeManager LightVolumeManager` | Reference to the Light Volume Manager. Needed for runtime initialization.|
|`bool IsRangeDirty` | Flag that defines if range of this point light should be recalculated in the next frame. Recalculates automatically when **AutoUpdateVolumes** is enabled. Resets to false after being recalculated.|

### Public Methods
| Public Method | Description |
| --- | --- |
|`bool IsSpotLight()` | Checks if it's a spotlight.|
|`bool IsPointLight()` | Checks if it's a point light.|
|`bool IsAreaLight()` | Checks if it's an area light.|
|`bool IsCustomTexture()` | Checks if uses custom texture.|
|`bool IsLut()` | Checks if uses LUT.|
|`bool IsParametric()` | Checks if uses Parametric mode.|
|`void SetLightSourceSize(float size)` | Sets Light source size, or a range data for LUT mode.|
|`void SetLut(int id)` | Sets LUT ID.|
|`void SetCustomTexture(int id)` | Sets Cubemap or a Cookie ID.|
|`void SetParametric()` | Sets light into parametric mode.|
|`void SetPointLight()` | Sets light into the point light type.|
|`void SetSpotLight(float angleDeg, float falloff)` | Sets light into the spot light type with both angle and falloff because angle required to determine falloff anyway.|
|`void SetSpotLight(float angleDeg)` | Sets light into the spot light type with angle specified.|
|`void SetAreaLight()` | Sets light into the area light type.|
|`void SetColor(Color color)` | Sets light source color and marks **IsRangeDirty** as true to auto recalculate range.|
|`void SetIntensity(float intensity)` | Sets light source intensity and marks **IsRangeDirty** as true to auto recalculate range.|
|`void UpdateTransform()` | Manually updates data required for shader.|
|`void UpdateRange()` | Recalculates squared culling range for the light. Usually it's enough to mark this light's **IsRangeDirty** as true instead, and this method will be executed automatically if **AutoUpdateVolumes** is enabled.|
