// WebGpuWater - WaterFoamProfile inspector with simple mode on top.
//
// Simple mode ON: the three dials (Foam Amount / Spray Amount / Look Preset) are the
// interface; the advanced sections are kept in sync live (SyncSimpleMode) and drawn
// read-only so what you see is exactly what the components receive. Simple mode OFF:
// the classic per-section editing, untouched.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterFoamProfile))]
    public sealed class WaterFoamProfileEditor : UnityEditor.Editor
    {
        const string ScriptProperty = "m_Script";
        const string SimpleProperty = "simple";

        public override void OnInspectorGUI()
        {
            var profile = (WaterFoamProfile)target;
            // Live-map the dials onto the sections BEFORE the serialized view is refreshed,
            // so the greyed-out sections below always show the values actually applied.
            bool simpleEnabled = profile.simple != null && profile.simple.enabled;
            if (simpleEnabled) profile.SyncSimpleMode();

            serializedObject.Update();
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.name == ScriptProperty) continue;
                bool isSimpleBlock = property.name == SimpleProperty;
                if (!isSimpleBlock && simpleEnabled && property.depth == 0)
                {
                    // Advanced sections while simple mode drives: visible (transparency)
                    // but read-only (single source of truth = the dials).
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(property, true);
                    continue;
                }
                EditorGUILayout.PropertyField(property, true);
            }

            if (simpleEnabled)
                EditorGUILayout.HelpBox(
                    "Simple mode drives everything below from the three dials (all 'drive' " +
                    "toggles forced on). Turn Simple off to hand-tune the sections; pick the " +
                    "Custom preset to keep your authored Veil/Look values while the dials " +
                    "still control amounts.", MessageType.Info);

            if (serializedObject.ApplyModifiedProperties() && simpleEnabled)
            {
                // A dial changed: re-map immediately and persist the mapped section values,
                // so the asset on disk always equals what renders.
                profile.SyncSimpleMode();
                EditorUtility.SetDirty(profile);
            }
        }
    }
}
