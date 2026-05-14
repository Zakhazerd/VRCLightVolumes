using UnityEditor;
using UnityEngine;

namespace VRCLightVolumes {
    public class LightProbePlacerWindow : EditorWindow {

        private LightVolume _lightVolume;

        private bool _adaptiveResolution = true;
        private float _voxelsPerUnit = 2;
        private Vector3Int _resolution = new Vector3Int(16, 16, 16);

        // Light probes world positions
        private Vector3[] _probesPositions = new Vector3[0];
        private bool _isWindowActive = false;

        // Preview
        private LightVolumePreviewRenderer _previewRenderer;

        public static LightProbePlacerWindow Show(LightVolume volume) {
            LightProbePlacerWindow window = ScriptableObject.CreateInstance<LightProbePlacerWindow>();
            window._lightVolume = volume;
            window._resolution = volume.Resolution / 4;
            window._voxelsPerUnit = volume.VoxelsPerUnit / 4;
            window._adaptiveResolution = volume.AdaptiveResolution;
            window.titleContent = new GUIContent("Generate Light Probes");
            window.position = new Rect(Screen.width / 2, Screen.height / 2, 220f, 150f);
            window.minSize = new Vector2(220f, 150f);
            window.Show();
            return window;
        }

        private void OnEnable() {

            const float width = 220f;
            const float height = 150f;

            Vector2 center = new Vector2(
                Screen.currentResolution.width / 2f - width / 2f,
                Screen.currentResolution.height / 2f - height / 2f
            );

            position = new Rect(center, new Vector2(width, height));

            SceneView.duringSceneGui += OnSceneGUI;
            _isWindowActive = true;

        }

        private void OnDisable() {

            SceneView.duringSceneGui -= OnSceneGUI;
            ReleasePreviewRenderer();
            _isWindowActive = false;

        }

        private void OnSceneGUI(SceneView sceneView) {

            if (!_isWindowActive) return;
            if (Event.current.type != EventType.Repaint) return;
            if (_lightVolume == null) return;
            if (_previewRenderer == null) _previewRenderer = new LightVolumePreviewRenderer();
            _previewRenderer.DrawProbeGrid(_lightVolume, _resolution, sceneView.camera);

        }

        // Releases preview renderer resources.
        void ReleasePreviewRenderer() {
            if (_previewRenderer == null) return;
            _previewRenderer.Dispose();
            _previewRenderer = null;
        }

        private void OnGUI() {

            if (_lightVolume == null) {
                Close();
                return;
            }

            const float padding = 10f;

            Rect paddedRect = new Rect(padding, padding, position.width - padding * 2, position.height - padding * 2);

            GUILayout.BeginArea(paddedRect);

            EditorGUILayout.LabelField(_lightVolume.gameObject.name, EditorStyles.boldLabel);

            _adaptiveResolution = EditorGUILayout.Toggle("Adaptive Resolution", _adaptiveResolution);
            if (_adaptiveResolution) {
                _voxelsPerUnit = EditorGUILayout.FloatField("Voxels Per Unit", _voxelsPerUnit);
            }

            _resolution = EditorGUILayout.Vector3IntField("Resolution", _resolution);
            Recalculate();

            GUILayout.Space(10);
            if (GUILayout.Button("Create Light Probe Group")) {
                CreateLightProbeGroup();
                Close();
            }

            GUILayout.EndArea();
            SceneView.RepaintAll();
        }

        // Creates a LightProbeGroup using the current preview resolution.
        private void CreateLightProbeGroup() {
            Recalculate();
            RecalculateProbesPositions();
            GameObject go = new GameObject("Light Probes - " + _lightVolume.gameObject.name);
            go.transform.parent = _lightVolume.transform;
            LightProbeGroup probeGroup = go.AddComponent<LightProbeGroup>();
            probeGroup.probePositions = _probesPositions;
            EditorGUIUtility.PingObject(go);
            Selection.activeObject = go;
        }

        // Recalculates preview resolution from current window settings.
        private void Recalculate() {
            if (_adaptiveResolution) RecalculateAdaptiveResolution();
        }

        // Recalculates resolution based on Adaptive Resolution.
        private void RecalculateAdaptiveResolution() {
            Vector3 scale = _lightVolume.GetScale();
            scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            Vector3 count = scale * _voxelsPerUnit;
            int x = Mathf.Max((int)Mathf.Round(count.x), 1);
            int y = Mathf.Max((int)Mathf.Round(count.y), 1);
            int z = Mathf.Max((int)Mathf.Round(count.z), 1);
            _resolution = new Vector3Int(x, y, z);
        }

        // Recalculates probes world positions.
        private void RecalculateProbesPositions() {
            _probesPositions = new Vector3[_resolution.x * _resolution.y * _resolution.z];
            Vector3 offset = new Vector3(0.5f, 0.5f, 0.5f);
            var pos = _lightVolume.GetPosition();
            var rot = _lightVolume.GetRotation();
            var scl = _lightVolume.GetScale();
            int id = 0;
            Vector3 localPos;
            for (int z = 0; z < _resolution.z; z++) {
                for (int y = 0; y < _resolution.y; y++) {
                    for (int x = 0; x < _resolution.x; x++) {
                        localPos = new Vector3((float)(x + 0.5f) / _resolution.x, (float)(y + 0.5f) / _resolution.y, (float)(z + 0.5f) / _resolution.z) - offset;
                        _probesPositions[id] = LVUtils.TransformPoint(localPos, pos, rot, scl);
                        id++;
                    }
                }
            }
        }

    }
}
