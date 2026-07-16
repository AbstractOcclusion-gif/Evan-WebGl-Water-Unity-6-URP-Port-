// WebGL Water - WaterVolume custom inspector (orchestration).
// Draws the cyan header, every feature section in a readable top-down order, and the footer.
// The scene-view gizmos/handles live in WaterVolumeEditor.cs; the per-section drawing lives in
// the WaterVolumeEditor.Setup/Dynamics/Ocean/Appearance partials. Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Foldout state. Only the placement/look/body blocks a user reaches for first start open;
        // the rest stay collapsed so the inspector opens compact.
        bool _showWiring = false;
        bool _showLook = true;
        bool _showPlacement = true;
        bool _showBody = true;
        bool _showPerformance = false;
        bool _showReflections = false;
        bool _showSimulation = false;
        bool _showRipple = false;
        bool _showWindWaves = false;
        bool _showWindow = false;
        bool _showOceanOpenWater = false;
        bool _showOceanClipmap = false;
        bool _showOceanGodRays = false;
        bool _showOceanFoam = false;
        bool _showObjectInteraction = false;
        bool _showWaterFog = false;
        bool _showScatter = false;
        bool _showDepth = false;
        bool _showBedDepth = false;
        bool _showFoam = false;
        bool _showCrestGlow = true;
        bool _showSurfAdvanced = false;
        bool _showCamera = false;
        bool _showSplash = false;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            WaterEditorUI.DrawHeader(InspectorTitle, BodySubtitle());

            // Body type selector + one-click defaults for the chosen archetype (advisory).
            WaterEditorUI.BodyTypeSelector(Prop("bodyType"));
            if (GUILayout.Button("Apply " + CurrentType + " defaults"))
                ApplyBodyTypeDefaults(CurrentType);

            // Placement + wiring
            DrawPlacementSection();
            DrawLookSection();
            DrawBodySection();
            DrawWiringSection();
            DrawPerformanceSection();

            // Surface look + light transport
            DrawReflectionsSection();
            DrawWaterFogSection();
            DrawVolumeScatterSection();
            DrawDepthAttenuationSection();
            DrawBedDepthSection();

            // Motion
            DrawSimulationSection();
            DrawRippleSection();
            DrawWindWavesSection();
            DrawObjectInteractionSection();
            DrawFoamSection();

            // Large water / ocean
            DrawWindowSection();
            DrawOceanOpenWaterSection();
            DrawOceanClipmapSection();
            DrawOceanGodRaysSection();
            DrawOceanFoamSection();

            // Camera + splash
            DrawCameraSection();
            DrawSplashSection();

            WaterEditorUI.DrawFooter();

            serializedObject.ApplyModifiedProperties();
        }

        // Shorthand for a serialized property by path; nested Settings blocks use dotted paths
        // (e.g. "ocean.openWater"). Kept single-sourced so no section invents a raw string twice.
        SerializedProperty Prop(string path) => serializedObject.FindProperty(path);

        // Draws every named property field of a nested block, honouring its [Range]/[Min]/[Tooltip]
        // attributes automatically (PropertyField reads them), so this editor holds no range literals.
        void DrawFields(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
                EditorGUILayout.PropertyField(Prop(paths[i]), true);
        }

        // ---- body-type applicability (advisory) --------------------------------------------
        // The bodyType enum drives which sections are relevant; sections grey their body when a
        // feature doesn't apply to the chosen archetype. Advisory only - it never changes runtime
        // behaviour by itself (the functional flags still gate the actual paths).
        WaterVolume.WaterBodyType CurrentType => (WaterVolume.WaterBodyType)Prop("bodyType").enumValueIndex;
        bool IsOcean => CurrentType == WaterVolume.WaterBodyType.Ocean;
        bool LakeOrOcean => CurrentType != WaterVolume.WaterBodyType.Pond;
        bool Bounded => CurrentType != WaterVolume.WaterBodyType.Ocean; // pond + lake have real walls / finite volume

        // Draw the given fields greyed unless the applicability condition holds (fine-grained, in-section).
        void DrawFieldsIf(bool enabled, params string[] paths)
        {
            EditorGUI.BeginDisabledGroup(!enabled);
            DrawFields(paths);
            EditorGUI.EndDisabledGroup();
        }

        // "Apply {type} defaults": set the functional flags that make the chosen archetype behave as
        // expected. Explicit (button-driven) so selecting a type never silently clobbers tuning.
        void ApplyBodyTypeDefaults(WaterVolume.WaterBodyType type)
        {
            bool openWater = type != WaterVolume.WaterBodyType.Pond;
            Prop("ocean.openWater").boolValue = openWater;
            Prop("ocean.unboundedOcean").boolValue = type == WaterVolume.WaterBodyType.Ocean;
            Prop("enableLargeBodyWindow").boolValue = openWater;
        }

        string BodySubtitle()
        {
            var volume = (WaterVolume)target;
            return volume.IsPrimary ? SubtitlePrimary : SubtitleSecondary;
        }

        const string InspectorTitle = "WATER VOLUME";
        const string SubtitlePrimary = "Primary body  —  drives global water state";
        const string SubtitleSecondary = "Secondary body";
    }
}
#endif
