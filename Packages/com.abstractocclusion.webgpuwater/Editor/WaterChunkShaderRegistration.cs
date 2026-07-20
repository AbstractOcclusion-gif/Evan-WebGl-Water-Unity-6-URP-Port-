// WebGpuWater - ensure the chunk wall shader ships in player builds.
// WaterChunkVolume resolves WaterChunkWall by name at runtime (Shader.Find), which only reaches
// shaders the build already includes. A packaged shader used solely from code is NOT pulled into a
// build automatically, so without this the chunk would render in the editor but vanish in a player.
// One idempotent add to GraphicsSettings' Always Included Shaders (only when missing - no VCS churn),
// so the component's shader slot never has to be filled by hand.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [InitializeOnLoad]
    internal static class WaterChunkShaderRegistration
    {
        const string AlwaysIncludedProperty = "m_AlwaysIncludedShaders";

        // delayCall: mutating a settings asset during the InitializeOnLoad callback itself is flaky;
        // deferring one tick runs it on a settled editor.
        static WaterChunkShaderRegistration() => EditorApplication.delayCall += EnsureIncluded;

        static void EnsureIncluded()
        {
            Shader shader = Shader.Find(WaterShaderNames.WaterChunkWall);
            if (shader == null) return; // not imported yet; the next domain reload retries

            var settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty shaders = settings.FindProperty(AlwaysIncludedProperty);
            if (shaders == null) return;

            for (int i = 0; i < shaders.arraySize; i++)
                if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) return; // already listed

            int index = shaders.arraySize;
            shaders.InsertArrayElementAtIndex(index);
            shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            settings.ApplyModifiedProperties();
            Debug.Log($"[WebGpuWater] Added '{WaterShaderNames.WaterChunkWall}' to Always Included Shaders " +
                      "so it ships in player builds (the chunk resolves it by name at runtime).");
        }
    }
}
