// WebGpuWater - honest inspector for WaterSplashEmitter: fields the assigned
// WaterFoamProfile's Splash section drives are shown DISABLED with a pointer to the
// profile (they are overwritten on every emit; editing them silently did nothing).
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterSplashEmitter))]
    public sealed class WaterSplashEmitterEditor : UnityEditor.Editor
    {
        const string ScriptProperty = "m_Script";

        // Overwritten by WaterFoamProfile.ApplyTo(emitter) when splash.drive is on.
        static readonly string[] SplashDrivenProperties =
        {
            "maxParticlesPerBurst", "upwardBias", "outwardSpread", "dropletSize",
            "lifetime", "crownMinStrength", "crownBaseSize", "crownLifetime"
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var splashEmitter = (WaterSplashEmitter)target;
            WaterFoamProfile profile = splashEmitter.profile;
            bool splashDriven = profile != null && profile.splash.drive;

            if (splashDriven)
            {
                EditorGUILayout.HelpBox(
                    "The assigned Water Foam Profile drives the greyed-out burst/crown fields " +
                    "below (applied on every emit). Tune the PROFILE's Splash section instead.",
                    MessageType.Info);
                if (GUILayout.Button("Open Foam Profile"))
                    Selection.activeObject = profile;
            }

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.name == ScriptProperty) continue;
                bool driven = splashDriven &&
                              System.Array.IndexOf(SplashDrivenProperties, property.name) >= 0;
                using (new EditorGUI.DisabledScope(driven))
                    EditorGUILayout.PropertyField(property, true);
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
