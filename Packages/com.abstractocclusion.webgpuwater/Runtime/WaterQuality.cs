// WebGL Water - quality tiers (Unity 6 / URP port)
// Scales the GPU-cost knobs (sim grid resolution, caustic resolution, god-ray steps)
// so the same water fits both a PC and the tighter WebGPU/mobile budget. Assign one
// asset to every WaterVolume; each body reads it at startup. With no asset a body uses
// Tier.Default (the original 256/1024/24 look), so existing scenes are unaffected.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [CreateAssetMenu(fileName = "WaterQuality", menuName = "WebGL Water/Water Quality")]
    public class WaterQuality : ScriptableObject
    {
        public enum Selection { Auto, ForceLow, ForceMedium, ForceHigh }

        // Grid resolution must be a positive multiple of the sim's thread-group size; derive
        // from the single source of truth so the two can't drift.
        const int ThreadGroupSize = WaterSimulation.ThreadGroupSize;
        const int MinCausticResolution = 64;
        const int MidGraphicsMemoryMB = 2048; // below this, Auto steps down from High to Medium

        // The refine loop cost is per-pixel dependent texture fetches, so cap it hard.
        const int MaxRefineSteps = 8;

        // Default (no-asset) tier knobs - the original look. Named once so the Default tier
        // and the High-tier inspector defaults below can't drift apart.
        const int DefaultSimResolution = 256;
        const int DefaultCausticResolution = 1024;
        const int DefaultGodRaySteps = 24;
        const int DefaultMaxWaveCount = WaterWaveBank.MaxWaves;
        const int DefaultRefineSteps = 5; // matches the surface shader's original fixed loop

        /// <summary>An immutable snapshot of the cost knobs a tier scales, handed to a body.
        /// Values are sanitised on construction so a mistyped inspector field still runs.</summary>
        public readonly struct Tier
        {
            public readonly int SimResolution;     // heightfield RT size (multiple of ThreadGroupSize)
            public readonly int CausticResolution; // caustic RT size
            public readonly int GodRaySteps;       // raymarch samples for the god-ray shader
            public readonly bool GodRays;          // god-ray pass on/off
            public readonly bool RichReflections;  // SSR/Planar allowed; when off, bodies cap to SkyOnly
            public readonly int MaxWaveCount;      // cap on summed wind-wave sinusoids (vertex+pixel+CPU cost)
            public readonly int RefineSteps;       // surface peaked-refine loop steps (dependent fetches per pixel)

            public Tier(int simResolution, int causticResolution, int godRaySteps, bool godRays,
                        bool richReflections, int maxWaveCount, int refineSteps)
            {
                SimResolution = SanitizeResolution(simResolution);
                CausticResolution = Mathf.Max(MinCausticResolution, causticResolution);
                GodRaySteps = Mathf.Max(0, godRaySteps);
                GodRays = godRays;
                RichReflections = richReflections;
                MaxWaveCount = Mathf.Clamp(maxWaveCount, 1, WaterWaveBank.MaxWaves);
                RefineSteps = Mathf.Clamp(refineSteps, 0, MaxRefineSteps);
            }

            // Round to the nearest valid grid size rather than fail, keeping a floor of one group.
            static int SanitizeResolution(int resolution)
            {
                int rounded = Mathf.RoundToInt(resolution / (float)ThreadGroupSize) * ThreadGroupSize;
                return Mathf.Max(ThreadGroupSize, rounded);
            }
        }

        /// <summary>Fallback tier when no quality asset is assigned - the original look.</summary>
        public static Tier Default => new Tier(DefaultSimResolution, DefaultCausticResolution,
                                               DefaultGodRaySteps, true, true,
                                               DefaultMaxWaveCount, DefaultRefineSteps);

        [Tooltip("Auto picks a tier from a capability probe (WebGPU/mobile -> Low). The Force* " +
                 "options pin a specific tier, e.g. to preview Low in a desktop editor.")]
        public Selection selection = Selection.Auto;

        [Header("Tier: High (desktop)")]
        [Min(ThreadGroupSize)] public int highSimResolution = DefaultSimResolution;
        [Min(MinCausticResolution)] public int highCausticResolution = DefaultCausticResolution;
        [Range(8, 64)] public int highGodRaySteps = DefaultGodRaySteps;
        public bool highGodRays = true;
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        public bool highRichReflections = true;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] public int highMaxWaveCount = DefaultMaxWaveCount;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] public int highRefineSteps = DefaultRefineSteps;

        [Header("Tier: Medium")]
        [Min(ThreadGroupSize)] public int mediumSimResolution = 128;
        [Min(MinCausticResolution)] public int mediumCausticResolution = 512;
        [Range(8, 64)] public int mediumGodRaySteps = 16;
        public bool mediumGodRays = true;
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        public bool mediumRichReflections = true;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] public int mediumMaxWaveCount = 12;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] public int mediumRefineSteps = 3;

        [Header("Tier: Low (WebGPU / mobile)")]
        [Min(ThreadGroupSize)] public int lowSimResolution = 128;
        [Min(MinCausticResolution)] public int lowCausticResolution = 256;
        // God rays kept ON at reduced steps: cheap enough for the WebGPU/mobile budget, and the
        // scene reads wrong without them (they were the main thing lost on the constrained build).
        [Range(0, 64)] public int lowGodRaySteps = 12;
        public bool lowGodRays = true;
        // SSR (needs Depth+Opaque) and Planar (a full extra scene render) are the priciest paths;
        // off by default on the constrained budget so Low falls back to the cheap procedural sky.
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        public bool lowRichReflections = false;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] public int lowMaxWaveCount = 8;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] public int lowRefineSteps = 2;

        /// <summary>The active tier: the forced one, or the capability-probed one under Auto.</summary>
        public Tier Resolve()
        {
            switch (selection)
            {
                case Selection.ForceLow: return Low;
                case Selection.ForceMedium: return Medium;
                case Selection.ForceHigh: return High;
                default: return Probe();
            }
        }

        Tier High => new Tier(highSimResolution, highCausticResolution, highGodRaySteps, highGodRays,
                              highRichReflections, highMaxWaveCount, highRefineSteps);
        Tier Medium => new Tier(mediumSimResolution, mediumCausticResolution, mediumGodRaySteps, mediumGodRays,
                                mediumRichReflections, mediumMaxWaveCount, mediumRefineSteps);
        Tier Low => new Tier(lowSimResolution, lowCausticResolution, lowGodRaySteps, lowGodRays,
                             lowRichReflections, lowMaxWaveCount, lowRefineSteps);

        // Pick a tier from the running hardware. The web player is how Unity ships WebGPU
        // builds, and async readback (buoyancy) is often unavailable there - both force Low.
        Tier Probe()
        {
            bool constrained = Application.platform == RuntimePlatform.WebGLPlayer
                               || Application.isMobilePlatform
                               || !SystemInfo.supportsAsyncGPUReadback;
            if (constrained) return Low;
            if (SystemInfo.graphicsMemorySize < MidGraphicsMemoryMB) return Medium;
            return High;
        }
    }
}
