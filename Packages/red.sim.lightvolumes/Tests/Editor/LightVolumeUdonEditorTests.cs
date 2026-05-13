using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCLightVolumes.Tests {
    public class LightVolumeUdonEditorTests {
        private const float Epsilon = 0.0001f;

        private static readonly int _lightVolumeInvLocalEdgeSmoothID = Shader.PropertyToID("_UdonLightVolumeInvLocalEdgeSmooth");
        private static readonly int _lightVolumeColorID = Shader.PropertyToID("_UdonLightVolumeColor");
        private static readonly int _lightVolumeCountID = Shader.PropertyToID("_UdonLightVolumeCount");
        private static readonly int _lightVolumeAdditiveCountID = Shader.PropertyToID("_UdonLightVolumeAdditiveCount");
        private static readonly int _lightVolumeAdditiveMaxOverdrawID = Shader.PropertyToID("_UdonLightVolumeAdditiveMaxOverdraw");
        private static readonly int _lightVolumeEnabledID = Shader.PropertyToID("_UdonLightVolumeEnabled");
        private static readonly int _lightVolumeProbesBlendID = Shader.PropertyToID("_UdonLightVolumeProbesBlend");
        private static readonly int _lightVolumeSharpBoundsID = Shader.PropertyToID("_UdonLightVolumeSharpBounds");
        private static readonly int _lightVolumeRotationQuaternionID = Shader.PropertyToID("_UdonLightVolumeRotationQuaternion");
        private static readonly int _lightVolumeInvWorldMatrixID = Shader.PropertyToID("_UdonLightVolumeInvWorldMatrix");
        private static readonly int _lightVolumeUvwScaleID = Shader.PropertyToID("_UdonLightVolumeUvwScale");
        private static readonly int _lightVolumeOcclusionUvwID = Shader.PropertyToID("_UdonLightVolumeOcclusionUvw");
        private static readonly int _lightVolumeOcclusionCountID = Shader.PropertyToID("_UdonLightVolumeOcclusionCount");
        private static readonly int _pointLightPositionID = Shader.PropertyToID("_UdonPointLightVolumePosition");
        private static readonly int _pointLightColorID = Shader.PropertyToID("_UdonPointLightVolumeColor");
        private static readonly int _pointLightDirectionID = Shader.PropertyToID("_UdonPointLightVolumeDirection");
        private static readonly int _pointLightCustomIdID = Shader.PropertyToID("_UdonPointLightVolumeCustomID");
        private static readonly int _pointLightCountID = Shader.PropertyToID("_UdonPointLightVolumeCount");
        private static readonly int _pointLightCubeCountID = Shader.PropertyToID("_UdonPointLightVolumeCubeCount");
        private static readonly int _pointLightDepthShadowDataID = Shader.PropertyToID("_UdonPointLightVolumeDepthShadowData");
        private static readonly int _pointLightDepthShadowReprojectionDataID = Shader.PropertyToID("_UdonPointLightVolumeDepthShadowReprojectionData");
        private static readonly int _pointLightDepthShadowCountID = Shader.PropertyToID("_UdonPointLightVolumeDepthShadowCount");
        private static readonly int _pointLightDepthShadowResolutionID = Shader.PropertyToID("_UdonPointLightVolumeDepthShadowResolution");
        private static readonly int _lightBrightnessCutoffID = Shader.PropertyToID("_UdonLightBrightnessCutoff");
        private static readonly BindingFlags _lifecycleMethodFlags = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        // Resets process-wide shader globals before every test case.
        [SetUp]
        public void SetUp() {
            ResetShaderGlobals();
        }

        // Destroys all temporary scene and texture objects created by a test case.
        [TearDown]
        public void TearDown() {
            ResetShaderGlobals();
            for (int i = _createdObjects.Count - 1; i >= 0; i--) {
                DestroyTestObject(_createdObjects[i]);
            }
            _createdObjects.Clear();
        }

        // Verifies that corrupted serialized registry arrays are repaired instead of throwing.
        [Test]
        public void NullRegistriesDisableShaderWithoutThrowing() {
            LightVolumeManager manager = CreateManager("Null Registries Manager", true);
            manager.LightVolumeInstances = null;
            manager.PointLightVolumeInstances = null;

            Assert.DoesNotThrow(() => manager.UpdateVolumes());

            Assert.That(manager.LightVolumeInstances, Is.Not.Null);
            Assert.That(manager.PointLightVolumeInstances, Is.Not.Null);
            Assert.That(manager.LightVolumeInstances.Length, Is.EqualTo(0));
            Assert.That(manager.PointLightVolumeInstances.Length, Is.EqualTo(0));
            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);
            AssertGlobalFloat(_pointLightDepthShadowCountID, 0);
        }

        // Verifies that empty light-volume and point-light families do not block each other.
        [Test]
        public void EmptyVolumeFamiliesWriteIndependentCounts() {
            LightVolumeManager emptyManager = CreateManager("Empty Families Manager", true);

            emptyManager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);

            LightVolumeManager pointOnlyManager = CreateManager("Point Only Manager", false);
            PointLightVolumeInstance point = CreatePointLight(pointOnlyManager, "Point Only Light", true);
            point.transform.position = new Vector3(1, 2, 3);
            point.PositionData = new Vector4(0, 0, 0, 2);
            point.SetPointLight();
            pointOnlyManager.LightVolumeInstances = new LightVolumeInstance[0];
            pointOnlyManager.PointLightVolumeInstances = new[] { point };

            pointOnlyManager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 1);
            AssertVectorClose(new Vector4(1, 2, 3, 2), Shader.GetGlobalVectorArray(_pointLightPositionID)[0]);

            LightVolumeManager volumeOnlyManager = CreateManager("Volume Only Manager", true);
            LightVolumeInstance volume = CreateLightVolume(volumeOnlyManager, "Volume Only Light Volume", true);
            volumeOnlyManager.LightVolumeInstances = new[] { volume };
            volumeOnlyManager.PointLightVolumeInstances = new PointLightVolumeInstance[0];

            volumeOnlyManager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 1);
            AssertGlobalFloat(_pointLightCountID, 0);
            AssertVectorClose(ExpectedLightVolumeColor(volume), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);
        }

        // Exercises real GameObject enable and disable callbacks for unregister and re-register behavior.
        [Test]
        public void LifecycleCallbacksRegisterUnregisterAndReinitializeVolumes() {
            LightVolumeManager manager = CreateManager("Lifecycle Manager", true);
            LightVolumeInstance volume = CreateLightVolume(manager, "Lifecycle Volume", true);

            manager.UpdateVolumes();
            Assert.That(volume.IsInitialized, Is.True);
            Assert.That(ContainsLightVolume(manager.LightVolumeInstances, volume), Is.True);
            Assert.That(CountLightVolumeReferences(manager.LightVolumeInstances, volume), Is.EqualTo(1));
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 1);

            volume.gameObject.SetActive(false);
            InvokeLifecycleMethod(volume, "OnDisable");

            manager.UpdateVolumes();
            Assert.That(volume.IsInitialized, Is.False);
            Assert.That(ContainsLightVolume(manager.LightVolumeInstances, volume), Is.False);
            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);

            volume.gameObject.SetActive(true);
            InvokeLifecycleMethod(volume, "OnEnable");

            manager.UpdateVolumes();
            Assert.That(volume.IsInitialized, Is.True);
            Assert.That(ContainsLightVolume(manager.LightVolumeInstances, volume), Is.True);
            Assert.That(CountLightVolumeReferences(manager.LightVolumeInstances, volume), Is.EqualTo(1));
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 1);

            manager.gameObject.SetActive(false);
            InvokeLifecycleMethod(manager, "OnDisable");

            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);
        }

        // Verifies runtime-style initialization and deinitialization for both regular and point light volumes.
        [Test]
        public void RuntimeLifecycleInitializesAndDeinitializesBothVolumeTypes() {
            LightVolumeManager manager = CreateManager("Runtime Lifecycle Manager", true);
            LightVolumeInstance volume = CreateUnregisteredLightVolume(manager, "Runtime Light Volume");
            PointLightVolumeInstance point = CreateUnregisteredPointLight(manager, "Runtime Point Light Volume");

            InvokeLifecycleMethod(volume, "OnEnable");
            InvokeLifecycleMethod(point, "OnEnable");
            manager.UpdateVolumes();

            Assert.That(volume.IsInitialized, Is.True);
            Assert.That(point.IsInitialized, Is.True);
            Assert.That(ContainsLightVolume(manager.LightVolumeInstances, volume), Is.True);
            Assert.That(ContainsPointLightVolume(manager.PointLightVolumeInstances, point), Is.True);
            Assert.That(CountLightVolumeReferences(manager.LightVolumeInstances, volume), Is.EqualTo(1));
            Assert.That(CountPointLightVolumeReferences(manager.PointLightVolumeInstances, point), Is.EqualTo(1));
            AssertGlobalFloat(_lightVolumeCountID, 1);
            AssertGlobalFloat(_pointLightCountID, 1);

            InvokeLifecycleMethod(volume, "OnDisable");
            InvokeLifecycleMethod(point, "OnDisable");
            manager.UpdateVolumes();

            Assert.That(volume.IsInitialized, Is.False);
            Assert.That(point.IsInitialized, Is.False);
            Assert.That(ContainsLightVolume(manager.LightVolumeInstances, volume), Is.False);
            Assert.That(ContainsPointLightVolume(manager.PointLightVolumeInstances, point), Is.False);
            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);
        }

        // Verifies first-update cleanup of duplicate and inactive serialized registry entries.
        [Test]
        public void SanitizedRegistriesRemoveInactiveDuplicatesAndKeepValidEntries() {
            LightVolumeManager manager = CreateManager("Sanitize Manager", true, false);
            LightVolumeInstance lightA = CreateLightVolume(manager, "Light A", true);
            LightVolumeInstance lightB = CreateLightVolume(manager, "Light B", true);
            LightVolumeInstance inactiveLight = CreateLightVolume(manager, "Inactive Light", false);
            PointLightVolumeInstance pointA = CreatePointLight(manager, "Point A", true);
            PointLightVolumeInstance pointB = CreatePointLight(manager, "Point B", true);
            PointLightVolumeInstance inactivePoint = CreatePointLight(manager, "Inactive Point", false);

            inactiveLight.IsInitialized = true;
            inactivePoint.IsInitialized = true;
            manager.LightVolumeInstances = new[] { lightA, lightA, inactiveLight, null, lightB };
            manager.PointLightVolumeInstances = new[] { pointA, inactivePoint, pointA, null, pointB };

            manager.gameObject.SetActive(true);
            manager.UpdateVolumes();

            Assert.That(manager.LightVolumeInstances[0], Is.SameAs(lightA));
            Assert.That(manager.LightVolumeInstances[1], Is.Null);
            Assert.That(manager.LightVolumeInstances[2], Is.Null);
            Assert.That(inactiveLight.IsInitialized, Is.False);
            Assert.That(manager.PointLightVolumeInstances[0], Is.SameAs(pointA));
            Assert.That(manager.PointLightVolumeInstances[1], Is.Null);
            Assert.That(manager.PointLightVolumeInstances[2], Is.Null);
            Assert.That(inactivePoint.IsInitialized, Is.False);
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 2);
            AssertGlobalFloat(_pointLightCountID, 2);
        }

        // Verifies inactive, black, and zero-intensity entries are removed from the final shader-visible arrays.
        [Test]
        public void DisabledAndZeroBrightnessInstancesAreExcludedFromFinalGlobals() {
            LightVolumeManager manager = CreateManager("Filtering Manager", true);
            LightVolumeInstance validVolume = CreateLightVolume(manager, "Valid Volume", true);
            LightVolumeInstance inactiveVolume = CreateLightVolume(manager, "Inactive Volume", false);
            LightVolumeInstance blackVolume = CreateLightVolume(manager, "Black Volume", true);
            LightVolumeInstance zeroVolume = CreateLightVolume(manager, "Zero Volume", true);
            ConfigureLightVolume(validVolume, new Color(0.2f, 0.4f, 0.8f, 1), 2, false, false, 0.25f);
            ConfigureLightVolume(blackVolume, Color.black, 1, false, false, 0.5f);
            ConfigureLightVolume(zeroVolume, Color.white, 0, false, false, 0.75f);

            PointLightVolumeInstance validPoint = CreatePointLight(manager, "Valid Point", true);
            PointLightVolumeInstance inactivePoint = CreatePointLight(manager, "Inactive Point", false);
            PointLightVolumeInstance blackPoint = CreatePointLight(manager, "Black Point", true);
            PointLightVolumeInstance zeroPoint = CreatePointLight(manager, "Zero Point", true);
            validPoint.Color = new Color(1, 0.5f, 0.25f, 1);
            validPoint.Intensity = 3;
            validPoint.PositionData = new Vector4(0, 0, 0, 4);
            validPoint.SetPointLight();
            blackPoint.Color = Color.black;
            zeroPoint.Intensity = 0;
            inactiveVolume.IsInitialized = true;
            inactivePoint.IsInitialized = true;
            manager.LightVolumeInstances = new[] { blackVolume, inactiveVolume, validVolume, zeroVolume };
            manager.PointLightVolumeInstances = new[] { zeroPoint, validPoint, inactivePoint, blackPoint };

            manager.UpdateVolumes();

            Assert.That(manager.LightVolumeInstances[1], Is.Null);
            Assert.That(manager.PointLightVolumeInstances[2], Is.Null);
            Assert.That(inactiveVolume.IsInitialized, Is.False);
            Assert.That(inactivePoint.IsInitialized, Is.False);
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 1);
            AssertGlobalFloat(_pointLightCountID, 1);
            AssertVectorClose(ExpectedLightVolumeColor(validVolume), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);
            AssertVectorClose(ExpectedPointLightColor(validPoint), Shader.GetGlobalVectorArray(_pointLightColorID)[0]);

            validVolume.Intensity = 0;
            validPoint.Intensity = 0;

            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_lightVolumeCountID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);
        }

        // Checks shader globals for volume order, color/intensity changes, movement, occlusion, and additive counters.
        [Test]
        public void LightVolumeGlobalsFollowOrderMovementAndParameterChanges() {
            LightVolumeManager manager = CreateManager("Volume Globals Manager", true);
            manager.LightProbesBlending = false;
            manager.SharpBounds = false;
            manager.AdditiveMaxOverdraw = 2;

            LightVolumeInstance first = CreateLightVolume(manager, "First Volume", true);
            LightVolumeInstance second = CreateLightVolume(manager, "Second Volume", true);
            ConfigureLightVolume(first, new Color(0.25f, 0.5f, 0.75f, 1), 1.5f, false, false, 0.1f);
            ConfigureLightVolume(second, new Color(1, 0.2f, 0.05f, 1), 0.75f, true, true, 0.5f);
            first.transform.position = new Vector3(-1, 0.5f, 2);
            first.transform.localScale = new Vector3(1, 2, 3);
            second.transform.position = new Vector3(2, 3, 4);
            second.transform.rotation = Quaternion.Euler(0, 45, 0);
            second.transform.localScale = new Vector3(2, 3, 4);
            second.InvBakedRotation = Quaternion.Euler(0, 15, 0);
            manager.LightVolumeInstances = new[] { second, first };

            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 2);
            AssertGlobalFloat(_lightVolumeAdditiveCountID, 1);
            AssertGlobalFloat(_lightVolumeOcclusionCountID, 1);
            AssertGlobalFloat(_lightVolumeProbesBlendID, 0);
            AssertGlobalFloat(_lightVolumeSharpBoundsID, 0);
            AssertGlobalFloat(_lightVolumeAdditiveMaxOverdrawID, 2);
            AssertVectorClose(ExpectedLightVolumeColor(second), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);
            AssertVectorClose(ExpectedLightVolumeColor(first), Shader.GetGlobalVectorArray(_lightVolumeColorID)[1]);
            AssertVectorClose(second.BoundsUvwMin0, Shader.GetGlobalVectorArray(_lightVolumeUvwScaleID)[0]);
            AssertVectorClose(second.BoundsUvwMin1, Shader.GetGlobalVectorArray(_lightVolumeUvwScaleID)[1]);
            AssertVectorClose(second.BoundsUvwMin2, Shader.GetGlobalVectorArray(_lightVolumeUvwScaleID)[2]);
            AssertVectorClose(second.BoundsUvwMinOcclusion, Shader.GetGlobalVectorArray(_lightVolumeOcclusionUvwID)[0]);
            AssertVectorClose(second.RelativeRotation, Shader.GetGlobalVectorArray(_lightVolumeRotationQuaternionID)[0]);
            AssertMatrixClose(Matrix4x4.TRS(second.transform.position, second.transform.rotation, second.transform.lossyScale).inverse, Shader.GetGlobalMatrixArray(_lightVolumeInvWorldMatrixID)[0]);

            second.Intensity = 0;
            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeCountID, 1);
            AssertGlobalFloat(_lightVolumeAdditiveCountID, 0);
            AssertGlobalFloat(_lightVolumeOcclusionCountID, 0);
            AssertVectorClose(ExpectedLightVolumeColor(first), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);

            second.Intensity = 0.75f;
            first.SetSmoothBlending(0.5f);
            first.Color = Color.green;
            first.Intensity = 2;
            first.IsAdditive = true;
            first.BakeOcclusion = true;
            first.transform.position = new Vector3(3, 4, 5);
            manager.LightVolumeInstances = new[] { first, second };

            manager.UpdateVolumes();

            Vector3 expectedSmooth = first.transform.lossyScale / 0.5f;
            AssertGlobalFloat(_lightVolumeCountID, 2);
            AssertGlobalFloat(_lightVolumeAdditiveCountID, 2);
            AssertGlobalFloat(_lightVolumeOcclusionCountID, 2);
            AssertVectorClose(ExpectedLightVolumeColor(first), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);
            AssertVectorClose(new Vector4(expectedSmooth.x, expectedSmooth.y, expectedSmooth.z, 0), Shader.GetGlobalVectorArray(_lightVolumeInvLocalEdgeSmoothID)[0]);
            AssertVectorClose(first.BoundsUvwMinOcclusion, Shader.GetGlobalVectorArray(_lightVolumeOcclusionUvwID)[0]);
            AssertMatrixClose(Matrix4x4.TRS(first.transform.position, first.transform.rotation, first.transform.lossyScale).inverse, Shader.GetGlobalMatrixArray(_lightVolumeInvWorldMatrixID)[0]);
        }

        // Verifies all active regular light volumes can write occlusion data at the same time.
        [Test]
        public void AllLightVolumesWithOcclusionWriteOcclusionGlobals() {
            LightVolumeManager manager = CreateManager("All Occlusion Manager", true);
            const int volumeCount = 8;
            LightVolumeInstance[] volumes = new LightVolumeInstance[volumeCount];

            for (int i = 0; i < volumeCount; i++) {
                LightVolumeInstance volume = CreateLightVolume(manager, "Occlusion Volume " + i, true);
                ConfigureLightVolume(volume, Color.white, 1, false, true, i * 0.1f);
                volumes[i] = volume;
            }
            manager.LightVolumeInstances = volumes;

            manager.UpdateVolumes();

            Vector4[] occlusionData = Shader.GetGlobalVectorArray(_lightVolumeOcclusionUvwID);
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, volumeCount);
            AssertGlobalFloat(_lightVolumeOcclusionCountID, volumeCount);
            for (int i = 0; i < volumeCount; i++) {
                AssertVectorClose(volumes[i].BoundsUvwMinOcclusion, occlusionData[i]);
            }
        }

        // Verifies dynamic regular and point light transforms are pushed into shader globals after movement.
        [Test]
        public void DynamicInstancesWriteMovedTransformsToShaderGlobals() {
            LightVolumeManager manager = CreateManager("Dynamic Transform Manager", true);
            LightVolumeInstance volume = CreateLightVolume(manager, "Dynamic Volume", true);
            PointLightVolumeInstance point = CreatePointLight(manager, "Dynamic Point", true);

            volume.IsDynamic = true;
            volume.transform.position = new Vector3(10, 20, 30);
            volume.transform.rotation = Quaternion.Euler(15, 25, 35);
            volume.transform.localScale = new Vector3(2, 3, 4);
            point.IsDynamic = true;
            point.transform.position = new Vector3(-2, -3, -4);
            point.transform.rotation = Quaternion.Euler(0, 90, 0);
            point.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            point.PositionData = new Vector4(0, 0, 0, 3);
            point.SetCustomTexture(0);
            manager.LightVolumeInstances = new[] { volume };
            manager.PointLightVolumeInstances = new[] { point };

            manager.UpdateVolumes();

            Quaternion expectedPointRotation = Quaternion.Inverse(point.transform.rotation);
            AssertMatrixClose(Matrix4x4.TRS(volume.transform.position, volume.transform.rotation, volume.transform.lossyScale).inverse, Shader.GetGlobalMatrixArray(_lightVolumeInvWorldMatrixID)[0]);
            AssertVectorClose(volume.RelativeRotation, Shader.GetGlobalVectorArray(_lightVolumeRotationQuaternionID)[0]);
            AssertVectorClose(new Vector4(-2, -3, -4, point.PositionData.w * point.SquaredScale), Shader.GetGlobalVectorArray(_pointLightPositionID)[0]);
            AssertVectorClose(new Vector4(expectedPointRotation.x, expectedPointRotation.y, expectedPointRotation.z, expectedPointRotation.w), Shader.GetGlobalVectorArray(_pointLightDirectionID)[0]);
        }

        // Verifies cached transform data changes only when the instance update methods run.
        [Test]
        public void StaticInstancesKeepCachedTransformDataUntilManualUpdateMethodsRun() {
            LightVolumeInstance volume = CreateLightVolume(null, "Static Cached Volume", true);
            PointLightVolumeInstance point = CreatePointLight(null, "Static Cached Point", true);

            volume.IsDynamic = false;
            volume.transform.position = Vector3.zero;
            volume.UpdateTransform();
            Matrix4x4 cachedVolumeMatrix = volume.InvWorldMatrix;
            point.IsDynamic = false;
            point.transform.position = Vector3.zero;
            point.PositionData = new Vector4(0, 0, 0, 2);
            point.UpdateTransform();
            Vector4 cachedPointPosition = point.PositionData;

            volume.transform.position = new Vector3(5, 6, 7);
            point.transform.position = new Vector3(8, 9, 10);

            AssertMatrixClose(cachedVolumeMatrix, volume.InvWorldMatrix);
            AssertVectorClose(cachedPointPosition, point.PositionData);

            volume.UpdateTransform();
            point.UpdateTransform();

            AssertMatrixClose(Matrix4x4.TRS(volume.transform.position, volume.transform.rotation, volume.transform.lossyScale).inverse, volume.InvWorldMatrix);
            AssertVectorClose(new Vector4(8, 9, 10, 2), point.PositionData);
        }

        // Checks point light shader globals through point, LUT, cookie spot, area, and depth shadow modes.
        [Test]
        public void PointLightGlobalsFollowModeDepthShadowAndCutoffChanges() {
            LightVolumeManager manager = CreateManager("Point Globals Manager", false);
            manager.CubemapsCount = 4;
            manager.DepthShadowCubemapsCount = 3;
            manager.DepthShadowResolution = 256;
            manager.CustomTextures = CreateTexture2D("Custom Texture Array");
            manager.DepthShadowTextures = CreateTexture2D("Depth Shadow Texture Array");
            manager.LightsBrightnessCutoff = 0.2f;

            PointLightVolumeInstance point = CreatePointLight(manager, "Point Light", true);
            point.transform.position = new Vector3(2, 3, 4);
            point.transform.rotation = Quaternion.Euler(10, 20, 30);
            point.transform.localScale = Vector3.one;
            point.Color = new Color(1, 0.5f, 0.25f, 1);
            point.Intensity = 2;
            point.PositionData = new Vector4(0, 0, 0, 4);
            point.Angle = 30 * Mathf.Deg2Rad;
            point.AngleData = Mathf.Cos(point.Angle);
            point.SetPointLight();

            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_pointLightCountID, 1);
            AssertGlobalFloat(_pointLightCubeCountID, 4);
            AssertGlobalFloat(_pointLightDepthShadowResolutionID, 256);
            AssertGlobalFloat(_lightBrightnessCutoffID, 0.2f);
            AssertVectorClose(new Vector4(2, 3, 4, 4), Shader.GetGlobalVectorArray(_pointLightPositionID)[0]);
            AssertVectorClose(ExpectedPointLightColor(point), Shader.GetGlobalVectorArray(_pointLightColorID)[0]);
            AssertPointCustomData(point, 0, -1, 0);

            point.SetColor(Color.black);
            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 0);
            AssertGlobalFloat(_pointLightCountID, 0);

            point.SetColor(new Color(1, 0.5f, 0.25f, 1));
            point.DepthShadowID = 1;
            point.DepthShadowFollowLight = false;
            point.DepthShadowSoftShadows = false;
            point.DepthShadowBias = -1;
            point.DepthShadowNormalBias = 0.2f;
            point.DepthShadowBiasSmoothness = -0.25f;
            point.DepthShadowBakePosition = new Vector3(5, 6, 7);

            manager.UpdateVolumes();

            AssertGlobalFloat(_pointLightDepthShadowCountID, 3);
            AssertPointCustomData(point, 0, -1, 2);
            AssertVectorClose(new Vector4(0, 0.2f, 0, 0), Shader.GetGlobalVectorArray(_pointLightDepthShadowDataID)[0]);
            AssertVectorClose(new Vector4(5, 6, 7, 1), Shader.GetGlobalVectorArray(_pointLightDepthShadowReprojectionDataID)[0]);

            point.DepthShadowFollowLight = true;
            point.DepthShadowBakeRotation = Quaternion.Euler(0, 90, 0);

            manager.UpdateVolumes();

            Quaternion expectedFollowRotation = point.DepthShadowBakeRotation * Quaternion.Inverse(point.transform.rotation);
            AssertPointCustomData(point, 0, -1, -2);
            AssertVectorClose(new Vector4(expectedFollowRotation.x, expectedFollowRotation.y, expectedFollowRotation.z, expectedFollowRotation.w), Shader.GetGlobalVectorArray(_pointLightDepthShadowReprojectionDataID)[0]);

            point.SetLut(2);
            point.SetLightSourceSize(5);

            manager.UpdateVolumes();

            Assert.That(point.IsLut(), Is.True);
            AssertPointCustomData(point, 3, -1, -2);
            AssertVectorClose(new Vector4(2, 3, 4, point.PositionData.w / point.SquaredScale), Shader.GetGlobalVectorArray(_pointLightPositionID)[0]);

            point.SetCustomTexture(1);
            point.SetSpotLight(60, 0.25f);

            manager.UpdateVolumes();

            Quaternion expectedCookieRotation = Quaternion.Inverse(point.transform.rotation);
            Assert.That(point.IsSpotLight(), Is.True);
            AssertPointCustomData(point, -2, -1, -2);
            AssertVectorClose(ExpectedPointLightColor(point), Shader.GetGlobalVectorArray(_pointLightColorID)[0]);
            AssertVectorClose(new Vector4(expectedCookieRotation.x, expectedCookieRotation.y, expectedCookieRotation.z, expectedCookieRotation.w), Shader.GetGlobalVectorArray(_pointLightDirectionID)[0]);

            point.SetParametric();
            point.transform.localScale = new Vector3(2, 3, 1);
            point.SetAreaLight();

            manager.UpdateVolumes();

            Quaternion expectedAreaRotation = point.transform.rotation;
            Assert.That(point.IsAreaLight(), Is.True);
            AssertVectorClose(new Vector4(2, 3, 4, point.PositionData.w), Shader.GetGlobalVectorArray(_pointLightPositionID)[0]);
            AssertVectorClose(ExpectedPointLightColor(point), Shader.GetGlobalVectorArray(_pointLightColorID)[0]);
            AssertVectorClose(new Vector4(expectedAreaRotation.x, expectedAreaRotation.y, expectedAreaRotation.z, expectedAreaRotation.w), Shader.GetGlobalVectorArray(_pointLightDirectionID)[0]);

            manager.LightsBrightnessCutoff = 0.5f;

            manager.UpdateVolumes();

            AssertGlobalFloat(_lightBrightnessCutoffID, 0.5f);
            Assert.That(point.IsRangeDirty, Is.False);
            Assert.That(Shader.GetGlobalVectorArray(_pointLightCustomIdID)[0].z, Is.EqualTo(point.SquaredRange).Within(Epsilon));
        }

        // Verifies all active point lights can use cubemap projection data at the same time.
        [Test]
        public void AllPointLightsWithCubemapProjectionWriteProjectionGlobals() {
            LightVolumeManager manager = CreateManager("All Cubemap Projection Manager", false);
            const int pointCount = 8;
            manager.CubemapsCount = pointCount;
            manager.CustomTextures = CreateTexture2D("All Cubemap Projection Texture Array");
            PointLightVolumeInstance[] points = new PointLightVolumeInstance[pointCount];

            for (int i = 0; i < pointCount; i++) {
                PointLightVolumeInstance point = CreatePointLight(manager, "Cubemap Point " + i, true);
                point.transform.position = new Vector3(i, i + 1, i + 2);
                point.transform.rotation = Quaternion.Euler(i * 5, i * 7, i * 11);
                point.PositionData = new Vector4(0, 0, 0, i + 1);
                point.SetCustomTexture(i);
                points[i] = point;
            }
            manager.PointLightVolumeInstances = points;

            manager.UpdateVolumes();

            Vector4[] positions = Shader.GetGlobalVectorArray(_pointLightPositionID);
            Vector4[] directions = Shader.GetGlobalVectorArray(_pointLightDirectionID);
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_pointLightCountID, pointCount);
            AssertGlobalFloat(_pointLightCubeCountID, pointCount);
            for (int i = 0; i < pointCount; i++) {
                Quaternion expectedRotation = Quaternion.Inverse(points[i].transform.rotation);
                AssertVectorClose(new Vector4(i, i + 1, i + 2, points[i].PositionData.w * points[i].SquaredScale), positions[i]);
                AssertVectorClose(new Vector4(expectedRotation.x, expectedRotation.y, expectedRotation.z, expectedRotation.w), directions[i]);
                AssertPointCustomData(i, points[i], -i - 1, -1, 0);
            }
        }

        // Verifies all active point lights can write depth shadow data together.
        [Test]
        public void AllPointLightsWithDepthShadowsWriteDepthShadowGlobals() {
            LightVolumeManager manager = CreateManager("All Depth Shadow Manager", false);
            const int pointCount = 6;
            manager.DepthShadowCubemapsCount = pointCount;
            manager.DepthShadowTextures = CreateTexture2D("All Depth Shadow Texture Array");
            PointLightVolumeInstance[] points = new PointLightVolumeInstance[pointCount];

            for (int i = 0; i < pointCount; i++) {
                PointLightVolumeInstance point = CreatePointLight(manager, "Depth Shadow Point " + i, true);
                point.DepthShadowID = i;
                point.DepthShadowBias = 0.01f * (i + 1);
                point.DepthShadowNormalBias = 0.02f * (i + 1);
                point.DepthShadowBiasSmoothness = 0.03f * (i + 1);
                point.DepthShadowSoftShadows = i % 2 == 0;
                point.DepthShadowFollowLight = i % 2 == 1;
                point.DepthShadowBakePosition = new Vector3(i + 3, i + 4, i + 5);
                point.DepthShadowBakeRotation = Quaternion.Euler(i * 10, i * 15, i * 20);
                points[i] = point;
            }
            manager.PointLightVolumeInstances = points;

            manager.UpdateVolumes();

            Vector4[] depthData = Shader.GetGlobalVectorArray(_pointLightDepthShadowDataID);
            Vector4[] reprojectionData = Shader.GetGlobalVectorArray(_pointLightDepthShadowReprojectionDataID);
            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_pointLightCountID, pointCount);
            AssertGlobalFloat(_pointLightDepthShadowCountID, pointCount);
            for (int i = 0; i < pointCount; i++) {
                float expectedDepthState = points[i].DepthShadowFollowLight ? -i - 1 : i + 1;
                AssertPointCustomData(i, points[i], 0, -1, expectedDepthState);
                AssertVectorClose(new Vector4(points[i].DepthShadowBias, points[i].DepthShadowNormalBias, points[i].DepthShadowBiasSmoothness, points[i].DepthShadowSoftShadows ? 1 : 0), depthData[i]);
                if (points[i].DepthShadowFollowLight) {
                    Quaternion expectedRotation = points[i].DepthShadowBakeRotation * Quaternion.Inverse(points[i].transform.rotation);
                    AssertVectorClose(new Vector4(expectedRotation.x, expectedRotation.y, expectedRotation.z, expectedRotation.w), reprojectionData[i]);
                } else {
                    AssertVectorClose(new Vector4(points[i].DepthShadowBakePosition.x, points[i].DepthShadowBakePosition.y, points[i].DepthShadowBakePosition.z, 1), reprojectionData[i]);
                }
            }
        }

        // Ensures shader array caps are enforced for oversized runtime registries.
        [Test]
        public void ShaderCountsClampToSupportedUdonArraySizes() {
            LightVolumeManager manager = CreateManager("Caps Manager", true);
            LightVolumeInstance[] volumes = new LightVolumeInstance[35];
            PointLightVolumeInstance[] points = new PointLightVolumeInstance[130];

            for (int i = 0; i < volumes.Length; i++) {
                LightVolumeInstance volume = CreateLightVolume(manager, "Clamped Volume " + i, true);
                ConfigureLightVolume(volume, Color.white, 1, false, false, i * 0.01f);
                volumes[i] = volume;
            }
            for (int i = 0; i < points.Length; i++) {
                PointLightVolumeInstance point = CreatePointLight(manager, "Clamped Point " + i, true);
                point.PositionData = new Vector4(0, 0, 0, 1);
                point.SetPointLight();
                points[i] = point;
            }

            manager.LightVolumeInstances = volumes;
            manager.PointLightVolumeInstances = points;

            manager.UpdateVolumes();

            AssertGlobalFloat(_lightVolumeEnabledID, 1);
            AssertGlobalFloat(_lightVolumeCountID, 32);
            AssertGlobalFloat(_pointLightCountID, 128);
            AssertVectorClose(ExpectedLightVolumeColor(volumes[0]), Shader.GetGlobalVectorArray(_lightVolumeColorID)[0]);
            AssertVectorClose(ExpectedLightVolumeColor(volumes[31]), Shader.GetGlobalVectorArray(_lightVolumeColorID)[31]);
            AssertVectorClose(ExpectedPointLightColor(points[0]), Shader.GetGlobalVectorArray(_pointLightColorID)[0]);
            AssertVectorClose(ExpectedPointLightColor(points[127]), Shader.GetGlobalVectorArray(_pointLightColorID)[127]);
        }

        // Creates a manager with deterministic defaults.
        private LightVolumeManager CreateManager(string name, bool withAtlas) {
            return CreateManager(name, withAtlas, true);
        }

        private LightVolumeManager CreateManager(string name, bool withAtlas, bool active) {
            GameObject gameObject = CreateGameObject(name, false);
            LightVolumeManager manager = gameObject.AddComponent<LightVolumeManager>();
            manager.LightVolumeAtlas = withAtlas ? CreateAtlas() : null;
            manager.LightVolumeInstances = new LightVolumeInstance[0];
            manager.PointLightVolumeInstances = new PointLightVolumeInstance[0];
            manager.LightProbesBlending = true;
            manager.SharpBounds = true;
            manager.AutoUpdateVolumes = false;
            manager.AdditiveMaxOverdraw = 4;
            manager.LightsBrightnessCutoff = 0.35f;
            gameObject.SetActive(active);
            return manager;
        }

        // Creates a scene light volume instance and optionally lets Unity call OnEnable.
        private LightVolumeInstance CreateLightVolume(LightVolumeManager manager, string name, bool active) {
            GameObject gameObject = CreateGameObject(name, false);
            LightVolumeInstance volume = gameObject.AddComponent<LightVolumeInstance>();
            volume.LightVolumeManager = manager;
            volume.IsDynamic = true;
            ConfigureLightVolume(volume, Color.white, 1, false, false, 0);
            gameObject.SetActive(active);
            if (active && manager != null) manager.InitializeLightVolume(volume);
            return volume;
        }

        // Creates a scene point light volume instance and optionally lets Unity call OnEnable.
        private PointLightVolumeInstance CreatePointLight(LightVolumeManager manager, string name, bool active) {
            GameObject gameObject = CreateGameObject(name, false);
            PointLightVolumeInstance point = gameObject.AddComponent<PointLightVolumeInstance>();
            point.LightVolumeManager = manager;
            point.Color = Color.white;
            point.Intensity = 1;
            point.IsDynamic = true;
            point.PositionData = new Vector4(0, 0, 0, 1);
            point.DirectionData = new Vector4(0, 0, 1, 1);
            point.Angle = 30 * Mathf.Deg2Rad;
            point.AngleData = Mathf.Cos(point.Angle);
            point.ShadowmaskIndex = -1;
            gameObject.SetActive(active);
            if (active && manager != null) manager.InitializePointLightVolume(point);
            return point;
        }

        // Creates an active light volume that has a manager reference but is not registered yet.
        private LightVolumeInstance CreateUnregisteredLightVolume(LightVolumeManager manager, string name) {
            GameObject gameObject = CreateGameObject(name, true);
            LightVolumeInstance volume = gameObject.AddComponent<LightVolumeInstance>();
            volume.LightVolumeManager = manager;
            volume.IsDynamic = true;
            ConfigureLightVolume(volume, Color.white, 1, false, false, 0);
            return volume;
        }

        // Creates an active point light volume that has a manager reference but is not registered yet.
        private PointLightVolumeInstance CreateUnregisteredPointLight(LightVolumeManager manager, string name) {
            GameObject gameObject = CreateGameObject(name, true);
            PointLightVolumeInstance point = gameObject.AddComponent<PointLightVolumeInstance>();
            point.LightVolumeManager = manager;
            point.Color = Color.white;
            point.Intensity = 1;
            point.IsDynamic = true;
            point.PositionData = new Vector4(0, 0, 0, 1);
            point.DirectionData = new Vector4(0, 0, 1, 1);
            point.Angle = 30 * Mathf.Deg2Rad;
            point.AngleData = Mathf.Cos(point.Angle);
            point.ShadowmaskIndex = -1;
            return point;
        }

        // Creates a temporary GameObject tracked by teardown.
        private GameObject CreateGameObject(string name, bool active) {
            GameObject gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            gameObject.SetActive(active);
            return gameObject;
        }

        // Creates a temporary 3D atlas texture tracked by teardown.
        private Texture3D CreateAtlas() {
            Texture3D texture = new Texture3D(1, 1, 1, TextureFormat.RGBA32, false);
            texture.name = "Runtime Test Light Volume Atlas";
            _createdObjects.Add(texture);
            return texture;
        }

        // Creates a temporary 2D texture for texture global assignment checks.
        private Texture2D CreateTexture2D(string name) {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.name = name;
            _createdObjects.Add(texture);
            return texture;
        }

        // Assigns deterministic volume data used by shader global assertions.
        private static void ConfigureLightVolume(LightVolumeInstance volume, Color color, float intensity, bool isAdditive, bool bakeOcclusion, float offset) {
            volume.Color = color;
            volume.Intensity = intensity;
            volume.IsAdditive = isAdditive;
            volume.BakeOcclusion = bakeOcclusion;
            volume.InvBakedRotation = Quaternion.identity;
            volume.BoundsUvwMin0 = new Vector4(offset + 0.01f, offset + 0.02f, offset + 0.03f, offset + 0.04f);
            volume.BoundsUvwMin1 = new Vector4(offset + 0.05f, offset + 0.06f, offset + 0.07f, offset + 0.08f);
            volume.BoundsUvwMin2 = new Vector4(offset + 0.09f, offset + 0.1f, offset + 0.11f, offset + 0.12f);
            volume.BoundsUvwMinOcclusion = new Vector4(offset + 0.13f, offset + 0.14f, offset + 0.15f, offset + 0.16f);
            volume.BoundsUvwMax0 = new Vector4(offset + 0.17f, offset + 0.18f, offset + 0.19f, offset + 0.2f);
            volume.BoundsUvwMax1 = new Vector4(offset + 0.21f, offset + 0.22f, offset + 0.23f, offset + 0.24f);
            volume.BoundsUvwMax2 = new Vector4(offset + 0.25f, offset + 0.26f, offset + 0.27f, offset + 0.28f);
            volume.InvLocalEdgeSmoothing = new Vector4(offset + 0.29f, offset + 0.3f, offset + 0.31f, offset + 0.32f);
        }

        // Converts a light volume color exactly like LightVolumeManager does.
        private static Vector4 ExpectedLightVolumeColor(LightVolumeInstance instance) {
            Color color = instance.Color.linear * instance.Intensity;
            return new Vector4(color.r, color.g, color.b, instance.IsRotated ? 1 : 0);
        }

        // Converts a point light color exactly like LightVolumeManager does.
        private static Vector4 ExpectedPointLightColor(PointLightVolumeInstance instance) {
            Color color = instance.Color.linear * instance.Intensity;
            return new Vector4(color.r, color.g, color.b, instance.AngleData);
        }

        // Asserts the packed point custom data vector written to the shader.
        private static void AssertPointCustomData(PointLightVolumeInstance point, float customId, float shadowmaskIndex, float depthShadowState) {
            AssertPointCustomData(0, point, customId, shadowmaskIndex, depthShadowState);
        }

        // Asserts the packed point custom data vector at a specific shader array index.
        private static void AssertPointCustomData(int index, PointLightVolumeInstance point, float customId, float shadowmaskIndex, float depthShadowState) {
            Vector4 data = Shader.GetGlobalVectorArray(_pointLightCustomIdID)[index];
            Assert.That(data.x, Is.EqualTo(customId).Within(Epsilon));
            Assert.That(data.y, Is.EqualTo(shadowmaskIndex).Within(Epsilon));
            Assert.That(data.z, Is.EqualTo(point.SquaredRange).Within(Epsilon));
            Assert.That(data.w, Is.EqualTo(depthShadowState).Within(Epsilon));
        }

        // Asserts a global float with the shared tolerance.
        private static void AssertGlobalFloat(int propertyId, float expected) {
            Assert.That(Shader.GetGlobalFloat(propertyId), Is.EqualTo(expected).Within(Epsilon));
        }

        // Asserts a Vector4 with the shared tolerance.
        private static void AssertVectorClose(Vector4 expected, Vector4 actual) {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(Epsilon));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(Epsilon));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(Epsilon));
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(Epsilon));
        }

        // Asserts a Matrix4x4 with the shared tolerance.
        private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual) {
            for (int i = 0; i < 16; i++) {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(Epsilon), "Matrix index " + i);
            }
        }

        // Finds a light volume reference without relying on LINQ.
        private static bool ContainsLightVolume(LightVolumeInstance[] instances, LightVolumeInstance target) {
            if (instances == null) return false;
            for (int i = 0; i < instances.Length; i++) {
                if (instances[i] == target) return true;
            }
            return false;
        }

        // Finds a point light volume reference without relying on LINQ.
        private static bool ContainsPointLightVolume(PointLightVolumeInstance[] instances, PointLightVolumeInstance target) {
            if (instances == null) return false;
            for (int i = 0; i < instances.Length; i++) {
                if (instances[i] == target) return true;
            }
            return false;
        }

        // Counts light volume references without relying on LINQ.
        private static int CountLightVolumeReferences(LightVolumeInstance[] instances, LightVolumeInstance target) {
            if (instances == null) return 0;
            int count = 0;
            for (int i = 0; i < instances.Length; i++) {
                if (instances[i] == target) count++;
            }
            return count;
        }

        // Counts point light volume references without relying on LINQ.
        private static int CountPointLightVolumeReferences(PointLightVolumeInstance[] instances, PointLightVolumeInstance target) {
            if (instances == null) return 0;
            int count = 0;
            for (int i = 0; i < instances.Length; i++) {
                if (instances[i] == target) count++;
            }
            return count;
        }

        // Invokes private Unity lifecycle methods because EditMode tests do not run normal MonoBehaviour lifecycle for these scripts.
        private static void InvokeLifecycleMethod(MonoBehaviour behaviour, string methodName) {
            MethodInfo method = behaviour.GetType().GetMethod(methodName, _lifecycleMethodFlags);
            Assert.That(method, Is.Not.Null, methodName + " method was not found on " + behaviour.GetType().Name);
            method.Invoke(behaviour, null);
        }

        // Resets scalar shader globals that can affect later tests.
        private static void ResetShaderGlobals() {
            Shader.SetGlobalFloat(_lightVolumeEnabledID, 0);
            Shader.SetGlobalFloat(_lightVolumeCountID, 0);
            Shader.SetGlobalFloat(_lightVolumeAdditiveCountID, 0);
            Shader.SetGlobalFloat(_lightVolumeOcclusionCountID, 0);
            Shader.SetGlobalFloat(_pointLightCountID, 0);
            Shader.SetGlobalFloat(_pointLightCubeCountID, 0);
            Shader.SetGlobalFloat(_pointLightDepthShadowCountID, 0);
            Shader.SetGlobalFloat(_lightBrightnessCutoffID, 0);
        }

        // Destroys a temporary Unity object immediately when the editor runtime allows it.
        private static void DestroyTestObject(UnityEngine.Object target) {
            if (target == null) return;
            if (Application.isEditor) UnityEngine.Object.DestroyImmediate(target);
            else UnityEngine.Object.Destroy(target);
        }
    }
}
