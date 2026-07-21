// WebGpuWater - honest inspector for WaterFoamParticles.
//
// Two jobs:
//  1. Fields a WaterFoamProfile drives are shown DISABLED with a pointer to the profile.
//     Before this they silently did nothing (the profile re-applies every LateUpdate),
//     which read as "the foam sliders are broken".
//  2. Play-mode readout of the live spawn budget, so pool churn (the ring recycling live
//     foam early = flicker) is visible instead of mysterious.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterFoamParticles))]
    public sealed class WaterFoamParticlesEditor : UnityEditor.Editor
    {
        const string ScriptProperty = "m_Script";
        const double ReadbackIntervalSeconds = 0.25;
        // Counters buffer layout - MUST match the COUNTER_* layout in WaterFoamParticles.compute.
        const int CounterFrameSpawns = 1;
        const int CounterBurstSpawns = 2;
        const int CounterCount = 3;

        // Component fields overwritten every frame while the matching profile section drives.
        static readonly string[] AmbientDrivenProperties =
        {
            "spawnThreshold", "spawnRate", "maxSpawnPerFrame", "sprayChance",
            "sprayLaunchSpeed", "lifeRange", "sizeRange", "spawnMaxDistance"
        };
        static readonly string[] LookDrivenProperties =
        {
            "sizeHeroPower", "flipbookGrid", "flipbookFps"
        };

        readonly uint[] _counters = new uint[CounterCount];
        double _lastRequestTime;
        bool _requestInFlight;
        bool _hasCounterData;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var foam = (WaterFoamParticles)target;
            WaterFoamProfile profile = foam.profile;
            bool ambientDriven = profile != null && profile.ambient.drive;
            bool lookDriven = profile != null && profile.look.drive;

            if (ambientDriven || lookDriven)
            {
                EditorGUILayout.HelpBox(
                    "The assigned Water Foam Profile drives the greyed-out fields below " +
                    "(re-applied every frame) and rides its tint/opacity/atlas over the " +
                    "materials. Tune the PROFILE, not this component or the materials.",
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
                bool driven =
                    (ambientDriven && System.Array.IndexOf(AmbientDrivenProperties, property.name) >= 0) ||
                    (lookDriven && System.Array.IndexOf(LookDrivenProperties, property.name) >= 0);
                using (new EditorGUI.DisabledScope(driven))
                    EditorGUILayout.PropertyField(property, true);
            }
            serializedObject.ApplyModifiedProperties();

            DrawLiveReadout(foam);
        }

        // Async (never blocking) readback of the 3-uint counters buffer, throttled.
        void DrawLiveReadout(WaterFoamParticles foam)
        {
            if (!Application.isPlaying || foam.CountersBuffer == null) return;

            if (!_requestInFlight &&
                EditorApplication.timeSinceStartup - _lastRequestTime > ReadbackIntervalSeconds)
            {
                _requestInFlight = true;
                _lastRequestTime = EditorApplication.timeSinceStartup;
                AsyncGPUReadback.Request(foam.CountersBuffer, request =>
                {
                    _requestInFlight = false;
                    if (request.hasError) return;
                    var data = request.GetData<uint>();
                    for (int i = 0; i < CounterCount && i < data.Length; i++)
                        _counters[i] = data[i];
                    _hasCounterData = true;
                    Repaint();
                });
            }
            if (!_hasCounterData) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live (play mode)", EditorStyles.boldLabel);
            uint spawns = _counters[CounterFrameSpawns];
            uint bursts = _counters[CounterBurstSpawns];
            int budget = foam.SpawnBudgetPerFrame;
            EditorGUILayout.LabelField($"Pool: {foam.CapacityPow2} slots (ring)");
            EditorGUILayout.LabelField($"Spawns this frame: {spawns} / {budget} budget (+{bursts} burst)");
            if (spawns >= (uint)budget)
                EditorGUILayout.HelpBox(
                    "Spawn budget SATURATED: the ring pool is recycling live foam early, which " +
                    "shows as flicker / truncated lifetimes. Raise Capacity, lower Spawn Rate, " +
                    "or raise Spawn Threshold.", MessageType.Warning);
        }
    }
}
