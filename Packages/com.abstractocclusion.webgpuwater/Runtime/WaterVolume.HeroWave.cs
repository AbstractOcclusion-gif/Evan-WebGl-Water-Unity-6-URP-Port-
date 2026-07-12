// WebGpuWater - hero-wave state seam on the body.
// The WaterHeroWave component computes the wave's lifecycle each frame and publishes the shader
// state HERE; WaterUniformPublisher then writes it into every per-body property block alongside the
// rest of the body uniforms (surface, patch, clipmap, and the hero strip all see the SAME values,
// so the base offset can never crack against the lip sheet). The default (zeroed) state is inert:
// _HeroWaveActive = 0 skips the entire hero path in the shader.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Shader-ready hero-wave uniforms. Field layout mirrors WaterHeroWave.hlsl.</summary>
    internal struct HeroWaveShaderState
    {
        public bool Active;
        public Vector4 Frame;  // xy = crest-line centre (world xz), zw = along-crest unit dir (world xz)
        public Vector4 Shape;  // amplitude (envelope baked in), face length, back length, crest half-length
        public Vector4 Curl;   // peel position, peel blend, max roll (rad, envelope baked in), curl start fraction
        public Vector4 Curl2;  // pivot ahead fraction, pivot height fraction, lean distance, shoulder start fraction
        public Vector4 Motion; // undulation amplitude, undulation wavelength, undulation phase,
                               // peel direction sign (+1 = breaks from the -u end, -1 = from +u)
        public float FoamStrength; // whitewater master gain - consumed by the ripple-sim foam
                                   // injection only (never written to the render property blocks)

        static readonly int ID_HeroWaveActive = Shader.PropertyToID("_HeroWaveActive");
        static readonly int ID_HeroWaveFrame = Shader.PropertyToID("_HeroWaveFrame");
        static readonly int ID_HeroWaveShape = Shader.PropertyToID("_HeroWaveShape");
        static readonly int ID_HeroWaveCurl = Shader.PropertyToID("_HeroWaveCurl");
        static readonly int ID_HeroWaveCurl2 = Shader.PropertyToID("_HeroWaveCurl2");
        static readonly int ID_HeroWaveMotion = Shader.PropertyToID("_HeroWaveMotion");

        /// <summary>Push the WaterHeroWave.hlsl uniforms onto a compute shader - the ONE binder
        /// every GPU consumer (ripple-sim foam, foam particles) shares, so field packing can never
        /// drift between consumers. The inactive default binds Active = 0, gating everything.</summary>
        internal void BindTo(ComputeShader cs)
        {
            cs.SetFloat(ID_HeroWaveActive, Active ? 1f : 0f);
            cs.SetVector(ID_HeroWaveFrame, Frame);
            cs.SetVector(ID_HeroWaveShape, Shape);
            cs.SetVector(ID_HeroWaveCurl, Curl);
            cs.SetVector(ID_HeroWaveCurl2, Curl2);
            cs.SetVector(ID_HeroWaveMotion, Motion);
        }
    }

    public partial class WaterVolume
    {
        HeroWaveShaderState _heroWave; // zeroed = inert

        internal HeroWaveShaderState HeroWaveState => _heroWave;

        /// <summary>Publish this frame's hero-wave shader state. Called by WaterHeroWave (execution
        /// order before this body's Update), so every block written this frame carries it.</summary>
        internal void PublishHeroWave(in HeroWaveShaderState state) => _heroWave = state;

        /// <summary>Return the body to the inert (no hero wave) state.</summary>
        internal void ClearHeroWave() => _heroWave = default;

        /// <summary>Push this frame's hero-wave whitewater source to the ripple sim, together with
        /// the sim-uv -> world-xz affine (the sim grid is world-agnostic). Called just before
        /// StepFoam; the inert default state costs the kernel nothing.</summary>
        internal void PushHeroWaveFoam(WaterSimulation sim)
        {
            if (sim == null) return;
            // The sim domain is the scrolling window on windowed bodies, the whole footprint
            // otherwise - the SAME frames the render side uses (WorldToSim / pool mapping).
            Vector3 domainCenter = IsWindowed ? SimWindowCenter : VolumeCenter;
            Vector3 domainExtent = IsWindowed ? SimHalfExtent : VolumeExtentSafe;
            Quaternion rotation = VolumeRotation;
            Vector3 uvOrigin = domainCenter + rotation * new Vector3(-domainExtent.x, 0f, -domainExtent.z);
            Vector3 uvAxisX = rotation * new Vector3(2f * domainExtent.x, 0f, 0f);
            Vector3 uvAxisZ = rotation * new Vector3(0f, 0f, 2f * domainExtent.z);
            sim.SetHeroWaveFoam(_heroWave,
                                new Vector4(uvOrigin.x, uvOrigin.z, 0f, 0f),
                                new Vector4(uvAxisX.x, uvAxisX.z, uvAxisZ.x, uvAxisZ.z));
        }
    }
}
