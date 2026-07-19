// WebGL Water - WaterVolume inspector: Jerlov physical water-colour preset.
// A water-type dropdown + "Apply" button that writes the validated per-channel absorption into
// Fog Extinction (at density 1) and the single-scattering-albedo body colour into the Scatter / Fog
// colour. Mirrors the body-type "Apply defaults" pattern: explicit, button-driven, and fully
// undoable (values are set through SerializedProperties, committed by OnInspectorGUI). Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Scattering intensity is a multiplier on the body colour; a stored 0 would render the body
        // black however good the preset colour is. When applying a preset, lift a zeroed intensity to
        // this sensible default so the physical colour is actually visible.
        const float JerlovMinScatterIntensity = 1f;

        void DrawJerlovWaterTypeSelector()
        {
            DrawFields("jerlovWaterType");
            var type = (JerlovWaterType)Prop("jerlovWaterType").enumValueIndex;
            if (GUILayout.Button("Apply " + JerlovWaterTypes.Get(type).DisplayName + " water colour"))
                ApplyJerlovWaterType(type);
        }

        // Writes the preset into the existing appearance fields. Reversible via Undo; enables Water Fog
        // so the transmission tint (the part that removes the old constant cyan) is visible immediately.
        void ApplyJerlovWaterType(JerlovWaterType type)
        {
            JerlovPreset preset = JerlovWaterTypes.Get(type);

            Prop("waterFogSettings.fogExtinction").colorValue = preset.Extinction;
            Prop("waterFogSettings.fogDensity").floatValue = JerlovWaterTypes.PhysicalDensity;
            Prop("waterFogSettings.fogColor").colorValue = preset.BodyColor;
            Prop("waterFogSettings.waterFog").boolValue = true;

            Prop("volumeScatterSettings.scatterColor").colorValue = preset.BodyColor;
            SerializedProperty scatterIntensity = Prop("volumeScatterSettings.scatterIntensity");
            if (scatterIntensity.floatValue <= 0f)
                scatterIntensity.floatValue = JerlovMinScatterIntensity;
        }
    }
}
#endif
