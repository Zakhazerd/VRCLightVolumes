using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.Callbacks;

namespace VRCLightVolumes {
    internal static class LightVolumePreprocessor {
        [PostProcessScene]
        // Removes editor-only Light Volumes data from the temporary scene copy used by Unity's build pipeline
        static void OnPostProcessScene() {
            if (!BuildPipeline.isBuildingPlayer) return; // We only want to cleanup on build
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            Cleanup<LightVolumeSetup>(roots); // We must destroy LightVolumeSetup first!
            Cleanup<LightVolume>(roots);
            Cleanup<PointLightVolume>(roots);
        }

        // Removes one authoring-only component type from the scene copy used by the build pipeline
        static void Cleanup<T>(GameObject[] roots) where T : Component {
            var temp = new List<T>();
            foreach (var go in roots) {
                if (go == null) continue;
                temp.Clear();
                go.GetComponentsInChildren(true, temp);
                foreach (var component in temp) {
                    if (component == null) continue;
                    Object.DestroyImmediate(component);
                }
            }
        }
    }
}
