// WebGL Water - GPU foam particles (Unity 6 / URP port)
//
// Per-body foam/spray particle system, fully GPU-resident (KWS-inspired): a compute
// pass spawns particles where the body's foam sim is strong and drifts them with the
// surface flow; FoamParticles.shader draws the pool as procedural quads pulled from
// the buffer by SV_VertexID. No CPU readback, no Shuriken, no geometry shaders and
// no append buffers - every piece works on the WebGPU backend.
//
// Attach next to a WaterVolume (one system per body; buffers and draw follow that
// body's sim window, property block and cull/budget schedule).
using UnityEngine;
using System.Runtime.InteropServices;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("WebGL Water/Water Foam Particles")]
    public class WaterFoamParticles : MonoBehaviour
    {
        // Compute kernel names (must match WaterFoamParticles.compute).
        const string KernelBeginFrame = "BeginFrame";
        const string KernelSpawn = "Spawn";
        const string KernelUpdate = "Update";

        // Thread-group sizes. MUST equal the [numthreads] in WaterFoamParticles.compute.
        const int SpawnThreadGroupSize = 8;
        const int UpdateThreadGroupSize = 64;

        const int VerticesPerParticle = 6;
        const int CounterCount = 2; // ring cursor + per-frame spawn count

        // Knuth's multiplicative-hash constant (2^32 / golden ratio): decorrelates the
        // per-frame GPU random seed from the plain frame counter.
        const uint FrameSeedHashPrime = 2654435761u;

        // One particle = 12 floats. MUST match FoamParticle in the compute + shader.
        [StructLayout(LayoutKind.Sequential)]
        struct FoamParticle
        {
            public Vector3 worldPos;
            public Vector3 velocity;
            public float age, life, size, seed, kind, strength;
        }
        static readonly int ParticleStride = Marshal.SizeOf<FoamParticle>();

        // Compute/shader property ids.
        static readonly int ID_Particles = Shader.PropertyToID("Particles");
        static readonly int ID_ParticlesShader = Shader.PropertyToID("_Particles");
        static readonly int ID_Counters = Shader.PropertyToID("Counters");
        static readonly int ID_Sim = Shader.PropertyToID("Sim");
        static readonly int ID_FoamTex = Shader.PropertyToID("FoamTex");
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_Capacity = Shader.PropertyToID("_Capacity");
        static readonly int ID_FrameSeed = Shader.PropertyToID("_FrameSeed");
        static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        static readonly int ID_SpawnThreshold = Shader.PropertyToID("_SpawnThreshold");
        static readonly int ID_SpawnRate = Shader.PropertyToID("_SpawnRate");
        static readonly int ID_MaxSpawnPerFrame = Shader.PropertyToID("_MaxSpawnPerFrame");
        static readonly int ID_SprayChance = Shader.PropertyToID("_SprayChance");
        static readonly int ID_SprayLaunchSpeed = Shader.PropertyToID("_SprayLaunchSpeed");
        static readonly int ID_LifeMin = Shader.PropertyToID("_LifeMin");
        static readonly int ID_LifeMax = Shader.PropertyToID("_LifeMax");
        static readonly int ID_SizeMin = Shader.PropertyToID("_SizeMin");
        static readonly int ID_SizeMax = Shader.PropertyToID("_SizeMax");
        static readonly int ID_TexelWorldArea = Shader.PropertyToID("_TexelWorldArea");
        static readonly int ID_Gravity = Shader.PropertyToID("_Gravity");
        static readonly int ID_FlowDrift = Shader.PropertyToID("_FlowDrift");
        static readonly int ID_WindDrift = Shader.PropertyToID("_WindDrift");
        static readonly int ID_Drag = Shader.PropertyToID("_Drag");

        [Header("Wiring")]
        [Tooltip("The water body this system spawns from. Defaults to the WaterVolume on this GameObject.")]
        public WaterVolume volume;
        [Tooltip("WaterFoamParticles.compute (spawn/update kernels). Required.")]
        public ComputeShader particleCompute;
        [Tooltip("Material using the WebGLWater/FoamParticles shader. Required; the Water " +
                 "Wizard (AbstractOcclusion > WebGpuWater > Water Wizard) saves a tweakable " +
                 "material asset and assigns it here.")]
        public Material particleMaterial;

        [Header("Pool")]
        [Tooltip("Particle pool size; rounded up to a power of two. Oldest particles are recycled when full.")]
        [Range(256, 65536)] public int capacity = 4096;

        [Header("Spawning")]
        [Tooltip("Foam level (0..1) below which no particles spawn.")]
        [Range(0f, 1f)] public float spawnThreshold = 0.25f;
        [Tooltip("Expected spawns per second per square world-unit of fully-foamed water.")]
        [Range(0f, 200f)] public float spawnRate = 30f;
        [Tooltip("Hard cap on spawns per frame (spreads bursts over a few frames).")]
        [Range(16, 4096)] public int maxSpawnPerFrame = 256;
        [Tooltip("Fraction of spawns thrown as ballistic spray instead of floating foam.")]
        [Range(0f, 1f)] public float sprayChance = 0.15f;
        [Tooltip("Initial upward speed of spray droplets (world units/sec).")]
        [Range(0f, 5f)] public float sprayLaunchSpeed = 0.6f;

        [Header("Look & life")]
        [Tooltip("Particle lifetime range (seconds).")]
        public Vector2 lifeRange = new Vector2(1.5f, 4f);
        [Tooltip("Particle world half-size range.")]
        public Vector2 sizeRange = new Vector2(0.02f, 0.06f);

        [Header("Motion")]
        [Tooltip("Gravity on spray droplets (world units/sec^2).")]
        [Range(0f, 20f)] public float gravity = 4f;
        [Tooltip("Drift speed along the surface flow, per unit of surface slope (world units/sec).")]
        [Range(0f, 2f)] public float flowDrift = 0.25f;
        [Tooltip("Constant downwind drift of floating foam (world units/sec).")]
        [Range(0f, 0.5f)] public float windDriftSpeed = 0.02f;
        [Tooltip("How quickly foam velocity relaxes to the driven flow (1/sec).")]
        [Range(0f, 10f)] public float drag = 2f;

        GraphicsBuffer _particles;
        GraphicsBuffer _counters;
        int _kBeginFrame, _kSpawn, _kUpdate;
        int _capacityPow2;
        MaterialPropertyBlock _mpb;

        void OnEnable()
        {
            if (volume == null) volume = GetComponent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterFoamParticles: no WaterVolume assigned or found on this GameObject.", this);
                enabled = false;
                return;
            }
            if (particleCompute == null)
            {
                Debug.LogError("WaterFoamParticles: particleCompute (WaterFoamParticles.compute) not assigned.", this);
                enabled = false;
                return;
            }
            if (particleMaterial == null)
            {
                // No silent runtime material: it would be invisible in the project and
                // impossible to tweak. The Water Wizard creates and wires the asset.
                Debug.LogError("WaterFoamParticles: particleMaterial not assigned. Use " +
                               "'AbstractOcclusion > WebGpuWater > Water Wizard' to generate " +
                               "and wire a material asset.", this);
                enabled = false;
                return;
            }

            // FoamParticles.shader pulls the particle buffer in the VERTEX stage. WebGPU
            // compatibility mode (older Android GPUs / constrained browsers) allows zero
            // vertex-stage storage buffers, so drawing there is a validation error. Degrade
            // to "no foam particles" instead of a broken build; surface foam still renders.
            if (SystemInfo.maxComputeBufferInputsVertex < 1)
            {
                Debug.LogWarning("WaterFoamParticles: this device does not support structured " +
                                 "buffers in the vertex stage (WebGPU compatibility mode?); " +
                                 "foam particles disabled on this body.", this);
                enabled = false;
                return;
            }

            _kBeginFrame = particleCompute.FindKernel(KernelBeginFrame);
            _kSpawn = particleCompute.FindKernel(KernelSpawn);
            _kUpdate = particleCompute.FindKernel(KernelUpdate);

            // Tier cap first: the whole pool is drawn every frame (dead slots emit degenerate
            // quads), so weak devices pay for capacity whether particles are alive or not.
            // Relies on WaterVolume's earlier execution order (-50) having applied its tier.
            int budget = Mathf.Min(capacity, volume.FoamParticleBudget);
            _capacityPow2 = Mathf.NextPowerOfTwo(Mathf.Max(UpdateThreadGroupSize, budget));
            _particles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacityPow2, ParticleStride);
            _particles.SetData(new FoamParticle[_capacityPow2]); // life = 0 -> every slot dead
            _counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, CounterCount, sizeof(uint));
            _counters.SetData(new uint[CounterCount]);

            _mpb = new MaterialPropertyBlock();
        }

        void OnDisable()
        {
            _particles?.Dispose(); _particles = null;
            _counters?.Dispose(); _counters = null;
        }

        // LateUpdate so the volume's Update has already stepped the sim and refreshed its
        // window/schedule for this frame.
        void LateUpdate()
        {
            if (volume == null || !volume.isActiveAndEnabled) return;
            if (volume.SimStateTexture == null || volume.FoamMaskTexture == null) return;
            if (!volume.foam) return; // foam sim off -> nothing to spawn from

            if (volume.IsSimulating && Time.deltaTime > 0f)
                DispatchSimulation(Time.deltaTime);

            if (volume.IsVisibleToCamera)
                Draw();
        }

        void DispatchSimulation(float dt)
        {
            ComputeShader cs = particleCompute;
            volume.WriteSimFrameUniforms(cs);

            cs.SetFloat(ID_Size, volume.SimResolution);
            cs.SetInt(ID_Capacity, _capacityPow2);
            cs.SetInt(ID_FrameSeed, unchecked((int)(Time.frameCount * FrameSeedHashPrime)));
            cs.SetFloat(ID_DeltaTime, dt);

            cs.SetFloat(ID_SpawnThreshold, spawnThreshold);
            cs.SetFloat(ID_SpawnRate, spawnRate);
            cs.SetInt(ID_MaxSpawnPerFrame, maxSpawnPerFrame);
            cs.SetFloat(ID_SprayChance, sprayChance);
            cs.SetFloat(ID_SprayLaunchSpeed, sprayLaunchSpeed);
            cs.SetFloat(ID_LifeMin, lifeRange.x);
            cs.SetFloat(ID_LifeMax, Mathf.Max(lifeRange.x, lifeRange.y));
            cs.SetFloat(ID_SizeMin, sizeRange.x);
            cs.SetFloat(ID_SizeMax, Mathf.Max(sizeRange.x, sizeRange.y));
            cs.SetFloat(ID_TexelWorldArea, volume.SimTexelWorldArea);

            cs.SetFloat(ID_Gravity, gravity);
            cs.SetFloat(ID_FlowDrift, flowDrift);
            cs.SetVector(ID_WindDrift, WindDriftWorld());
            cs.SetFloat(ID_Drag, drag);

            cs.SetBuffer(_kBeginFrame, ID_Counters, _counters);
            cs.Dispatch(_kBeginFrame, 1, 1, 1);

            cs.SetBuffer(_kSpawn, ID_Particles, _particles);
            cs.SetBuffer(_kSpawn, ID_Counters, _counters);
            cs.SetTexture(_kSpawn, ID_Sim, volume.SimStateTexture);
            cs.SetTexture(_kSpawn, ID_FoamTex, volume.FoamMaskTexture);
            int spawnGroups = volume.SimResolution / SpawnThreadGroupSize;
            cs.Dispatch(_kSpawn, spawnGroups, spawnGroups, 1);

            // Only the resources the Update kernel actually reads: binding an unused
            // slot is a hard error on some backends.
            cs.SetBuffer(_kUpdate, ID_Particles, _particles);
            cs.SetTexture(_kUpdate, ID_Sim, volume.SimStateTexture);
            cs.Dispatch(_kUpdate, _capacityPow2 / UpdateThreadGroupSize, 1, 1);
        }

        // Constant downwind drift in world space: the wave bank's heading convention is
        // 0 degrees = travelling toward +X in the body's local frame.
        Vector2 WindDriftWorld()
        {
            float radians = volume.windFromDegrees * Mathf.Deg2Rad;
            Vector3 local = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians));
            Vector3 world = volume.transform.rotation * local * windDriftSpeed;
            return new Vector2(world.x, world.z);
        }

        void Draw()
        {
            // The body's own uniforms (sim texture, volume frame, waves, sun) drive the
            // vertex shader; the particle buffer rides along in the same block.
            volume.WriteBodyProps(_mpb);
            _mpb.SetBuffer(ID_ParticlesShader, _particles);

            var rp = new RenderParams(particleMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _mpb
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, _capacityPow2 * VerticesPerParticle);
        }
    }
}
