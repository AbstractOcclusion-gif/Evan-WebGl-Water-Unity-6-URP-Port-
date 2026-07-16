// WebGpuWater - surf roller foam particles (the breaking wave's OWN foam).
//
// A dedicated sibling of WaterFoamParticles for the surf breaker fronts: particles are
// PHASE-LOCKED to the closed-form front field (WaterSurfWaves.hlsl) instead of being
// advected by the 2D flow sim. Each particle belongs to ONE front: the GPU re-solves that
// front's crest position every frame (Newton inversion of the shore-distance warp) and
// rides the particle on it with a churning tumble orbit, so the foam rolls WITH the wave
// and never washes away or stretches. A share of emissions on plunging fronts is thrown
// as ballistic lip spray instead. Inspired by KWS1's baked shoreline particles (one clock
// drives wave + particles), but fully procedural - no baked assets, no readback, WebGPU-safe.
//
// Emission happens inside a world-anchored alongshore window centred on the break line
// nearest the camera, solved on the CPU from the baked shore field's arrays
// (WaterSurfBreakLine, the solve shared with WaterSurfCurl - closed form, no readback).
//
// Attach next to (or under) an open-water WaterVolume whose surf layer is live
// (bed depth + SDF baked, Shore Waves enabled).
using UnityEngine;
using System.Runtime.InteropServices;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Water Surf Roller Particles")]
    public sealed class WaterSurfRollerParticles : MonoBehaviour
    {
        // Compute kernel names (must match WaterSurfRoller.compute).
        const string KernelClearCounters = "ClearCounters";
        const string KernelEmit = "Emit";
        const string KernelUpdate = "Update";

        // Thread-group sizes. MUST equal the [numthreads] in WaterSurfRoller.compute.
        const int EmitThreadGroupSize = 64;
        const int UpdateThreadGroupSize = 64;

        const int VerticesPerParticle = 6; // two triangles per procedural quad (SV_VertexID)
        const int CounterCount = 2;        // ring cursor + per-frame emit count

        // Alongshore length (m) of the world-anchored emission window centred on the camera's
        // break-line hit. Long enough that the visible stretch of breaking wave is dressed;
        // emission slots outside the camera's view cost nothing (they fail the front gates).
        const float EmitWindowLengthMeters = 120f;
        // Hard cap on emission slots per dispatch (bounds the Emit kernel regardless of the
        // density knob): 4096 threads = 64 groups of 64.
        const int MaxEmitSlots = 4096;

        // Break-line search parameters + the placement smoothing constant live in
        // WaterSurfBreakLine (the ONE march + bisect shared with WaterSurfCurl).
        // The emission window's ALONGSHORE coordinate snaps to this world lattice instead of
        // gliding with the camera: slots (and therefore the per-(front, slot) emission dedupe)
        // stay world-stable while the camera moves within a cell, so the particle band belongs
        // to the COAST, not to the camera (v1's smoothed follow made the whole band visibly
        // slide along the shore with every camera move). The across-shore coordinate stays
        // exactly on the solved break line - snapping it would push slots off the surf zone.
        const float WindowSnapMeters = 30f;

        // Knuth's multiplicative-hash constant (2^32 / golden ratio): decorrelates the
        // per-frame GPU random seed from the plain frame counter (same idiom as
        // WaterFoamParticles.FrameSeedHashPrime).
        const uint FrameSeedHashPrime = 2654435761u;

        // One particle = 20 floats = 80 bytes (a multiple of 16 for structured-buffer layout
        // portability). MUST match RollerParticle in WaterSurfRoller.compute and
        // SurfRollerParticles.shader.
        //   worldPos     world position (y is ABSOLUTE world height, unlike WaterFoamParticles)
        //   age/life     seconds; life <= 0 marks a dead slot; Update rewrites life when the
        //                front finishes breaking so the fade-out tracks the broken tail
        //   velocity     ballistic velocity (spray only; rollers keep it zero)
        //   frontIndex   the front this particle is phase-locked to (floor of the field phase)
        //   crestDist    unwarped shore distance (m) of that front's crest - the Newton state
        //   dAcross      across-crest offset at birth (m, + offshore); kept for tuning/debug
        //   birthOverCap break-criterion ratio at birth (lifecycle reference)
        //   size         world half-size of the quad
        //   seed         0..1 fixed hash: sprite variant, yaw spin, tumble phase, tail jitter
        //   kind         0 = roller (crest-locked tumble), 1 = lip spray (ballistic)
        //   strength     whitewash-matched opacity, rewritten by Update every frame
        //   brokenTimer  seconds since 'broken' completed at the particle (tail clock)
        [StructLayout(LayoutKind.Sequential)]
        struct RollerParticle
        {
            public Vector3 worldPos;
            public float age;
            public Vector3 velocity;
            public float life;
            public float frontIndex, crestDist, dAcross, birthOverCap;
            public float size, seed, kind, strength;
            public float brokenTimer;
            public Vector3 _pad;
        }

        // Compute/shader property ids.
        static readonly int ID_Particles = Shader.PropertyToID("Particles");
        static readonly int ID_ParticlesShader = Shader.PropertyToID("_Particles");
        static readonly int ID_Counters = Shader.PropertyToID("Counters");
        static readonly int ID_Capacity = Shader.PropertyToID("_Capacity");
        static readonly int ID_FrameSeed = Shader.PropertyToID("_FrameSeed");
        static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        static readonly int ID_BeatDeltaTime = Shader.PropertyToID("_BeatDeltaTime");
        static readonly int ID_WaterPlaneY = Shader.PropertyToID("_WaterPlaneY");
        static readonly int ID_EmitWindowCenter = Shader.PropertyToID("_EmitWindowCenter");
        static readonly int ID_EmitWindowAlong = Shader.PropertyToID("_EmitWindowAlong");
        static readonly int ID_EmitWindowLength = Shader.PropertyToID("_EmitWindowLength");
        static readonly int ID_EmitSlotCount = Shader.PropertyToID("_EmitSlotCount");
        static readonly int ID_ParticlesPerMeter = Shader.PropertyToID("_ParticlesPerMeter");
        static readonly int ID_EmitBurst = Shader.PropertyToID("_EmitBurst");
        static readonly int ID_SizeMin = Shader.PropertyToID("_SizeMin");
        static readonly int ID_SizeMax = Shader.PropertyToID("_SizeMax");
        static readonly int ID_SizeHeroPower = Shader.PropertyToID("_SizeHeroPower");
        static readonly int ID_TumbleSpeed = Shader.PropertyToID("_TumbleSpeed");
        static readonly int ID_SprayShare = Shader.PropertyToID("_SprayShare");
        static readonly int ID_LifeTail = Shader.PropertyToID("_LifeTail");
        static readonly int ID_MasterGain = Shader.PropertyToID("_MasterGain");

        [Header("Wiring")]
        [Tooltip("The water body whose surf fronts this system dresses. Defaults to a WaterVolume " +
                 "on this GameObject or its parents.")]
        [SerializeField] internal WaterVolume volume;
        [Tooltip("WaterSurfRoller.compute (emit/update kernels). Required.")]
        [SerializeField] internal ComputeShader particleCompute;
        [Tooltip("Material using the AbstractOcclusion/WebGpuWater/SurfRollerParticles shader. Required.")]
        [SerializeField] internal Material particleMaterial;

        [Header("Pool")]
        [Tooltip("Particle pool size; rounded up to a power of two and clamped by the body's " +
                 "quality-tier budget. Oldest particles are recycled when full.")]
        [Range(256, 8192)] [SerializeField] internal int capacity = 2048;

        [Header("Emission")]
        [Tooltip("Emission slots per metre of crest inside the emission window. Each slot fires " +
                 "once per passing front (a burst of particles - see below), so slots x burst is " +
                 "the roller's foam density.")]
        [Range(0.5f, 10f)] [SerializeField] internal float particlesPerMeter = 3f;
        [Tooltip("Particles emitted per slot each time a front's crest arrives, jittered along " +
                 "and across the crest - the roller reads as a churning band instead of a " +
                 "one-particle-thin row.")]
        [Range(1, 8)] [SerializeField] internal int burstPerArrival = 3;
        [Tooltip("Fraction of emissions on strongly PLUNGING fronts thrown as ballistic lip spray " +
                 "instead of crest-locked roller foam.")]
        [Range(0f, 1f)] [SerializeField] internal float spraySharePct = 0.25f;

        [Header("Look & life")]
        [Tooltip("Smallest particle world half-size (m).")]
        [Range(0.01f, 2f)] [SerializeField] internal float sizeMin = 0.12f;
        [Tooltip("Largest particle world half-size (m).")]
        [Range(0.01f, 2f)] [SerializeField] internal float sizeMax = 0.35f;
        [Tooltip("Size distribution bias (KWS): 1 = uniform sizes across the range; higher = most " +
                 "particles stay small with rare large 'hero' sprites.")]
        [Range(1f, 6f)] [SerializeField] internal float sizeHeroPower = 1f;
        [Tooltip("Orbital churn speed inside the roller (rad/s at full break). 0 = foam rides the " +
                 "crest without tumbling.")]
        [Range(0f, 8f)] [SerializeField] internal float tumbleSpeed = 2f;
        [Tooltip("How long roller foam lingers (s) after its front has fully collapsed into the " +
                 "whitewash bore, before fading out. Longer = the foam rides the bore further " +
                 "toward the beach. (A component added before this knob's default changed keeps " +
                 "its serialized value - raise the slider if the whitewash run-in looks bare.)")]
        [Range(0f, 8f)] [SerializeField] internal float lifeTailSeconds = 3.5f;
        [Tooltip("Master gain on particle opacity. 0 = system emits/updates but draws nothing.")]
        [Range(0f, 1f)] [SerializeField] internal float masterGain = 1f;

        [Header("Sprite atlas")]
        [Tooltip("Foam sprite atlas layout (columns, rows). (1,1) = a plain foam texture (no " +
                 "flipbook); (2,2) = a 4-frame sheet, etc. Same convention as Water Foam Particles.")]
        [SerializeField] internal Vector2Int flipbookGrid = new Vector2Int(2, 2);
        [Tooltip("Flipbook animation speed of the foam sprite over its life (frames/sec). 0 = each " +
                 "particle shows one fixed atlas cell.")]
        [Range(0f, 30f)] [SerializeField] internal float flipbookFps = 0f;

        [Header("Render")]
        [Tooltip("Offset added to the material's render queue (shader default: Transparent+11, one " +
                 "above the ambient foam quads). Applied to a private material instance, never the asset.")]
        [Range(-10, 50)] [SerializeField] internal int renderQueueOffset = 0;

        GraphicsBuffer _particles;
        GraphicsBuffer _counters;
        Material _materialInstance; // private copy so the queue offset never dirties the asset
        MaterialPropertyBlock _mpb;
        int _kClearCounters, _kEmit, _kUpdate;
        int _capacityPow2;
        // Smoothed emission-window placement (world xz centre + unit alongshore direction).
        Vector2 _followCenter;
        Vector2 _followAlong = new Vector2(1f, 0f);
        bool _followValid;
        // Previous dispatch's master surf beat, so the Emit kernel gets the wave clock's REAL
        // advance (_BeatDeltaTime). The clock scales with the body's timeScale and wraps every
        // ~SurfBeatWrapFronts periods, so Time.deltaTime is NOT its delta: reconstructing the
        // previous phase from raw dt double-fires (timeScale < 1) or misses (timeScale > 1)
        // crest arrivals. At the wrap the delta goes hugely negative, which is exactly what
        // trips the kernel's |delta| guard (the documented ~3-hourly hiccup).
        float _prevBeatTime;
        bool _prevBeatValid;

        void OnEnable()
        {
            if (volume == null) volume = GetComponentInParent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterSurfRollerParticles: no WaterVolume assigned or found in parents.", this);
                enabled = false;
                return;
            }
            if (!volume.openWater)
            {
                Debug.LogError("WaterSurfRollerParticles: the assigned body is not open water (Open " +
                               "Water off) - the surf breaker fronts only exist on the large-body " +
                               "wave path. Disabling.", this);
                enabled = false;
                return;
            }
            if (particleCompute == null)
            {
                Debug.LogError("WaterSurfRollerParticles: particleCompute (WaterSurfRoller.compute) " +
                               "not assigned.", this);
                enabled = false;
                return;
            }
            if (particleMaterial == null)
            {
                Debug.LogError("WaterSurfRollerParticles: particleMaterial not assigned (needs the " +
                               "AbstractOcclusion/WebGpuWater/SurfRollerParticles shader).", this);
                enabled = false;
                return;
            }

            // SurfRollerParticles.shader pulls the particle buffer in the VERTEX stage. WebGPU
            // compatibility mode allows zero vertex-stage storage buffers, so drawing there is a
            // validation error. Degrade to "no roller particles" instead of a broken build - the
            // surface whitewash still renders (same gate as WaterFoamParticles).
            if (SystemInfo.maxComputeBufferInputsVertex < 1)
            {
                Debug.LogWarning("WaterSurfRollerParticles: this device does not support structured " +
                                 "buffers in the vertex stage (WebGPU compatibility mode?); surf " +
                                 "roller particles disabled on this body.", this);
                enabled = false;
                return;
            }

            _kClearCounters = particleCompute.FindKernel(KernelClearCounters);
            _kEmit = particleCompute.FindKernel(KernelEmit);
            _kUpdate = particleCompute.FindKernel(KernelUpdate);

            // Shared pool recipe (tier cap + pow2 + dead-slot zeroing), same as WaterFoamParticles.
            _capacityPow2 = WaterParticlePool.Allocate<RollerParticle>(
                capacity, volume.FoamParticleBudget, UpdateThreadGroupSize, CounterCount,
                out _particles, out _counters);

            // Queue offset lives on a private instance so tweaking it can never dirty the shared
            // material asset (mirrors WaterVolume's per-body material-instance rule).
            _materialInstance = new Material(particleMaterial) { hideFlags = HideFlags.HideAndDontSave };
            _materialInstance.renderQueue = particleMaterial.renderQueue + renderQueueOffset;

            _mpb = new MaterialPropertyBlock();
            _followValid = false;
            _prevBeatValid = false;
        }

        void OnDisable()
        {
            _particles?.Dispose(); _particles = null;
            _counters?.Dispose(); _counters = null;
            if (_materialInstance != null) { Destroy(_materialInstance); _materialInstance = null; }
        }

        // Live retune: the queue offset used to apply only in OnEnable, so dragging the slider
        // did nothing until the component was toggled.
        void OnValidate()
        {
            if (_materialInstance != null && particleMaterial != null)
                _materialInstance.renderQueue = particleMaterial.renderQueue + renderQueueOffset;
        }

        // LateUpdate so the volume's Update has already stepped its wave clock and refreshed the
        // shore field state for this frame (same seam timing as WaterFoamParticles/WaterSurfCurl).
        void LateUpdate()
        {
            if (volume == null || !volume.isActiveAndEnabled) return;
            // Defensive: OnEnable can bail before allocating (compute/material assigned later in
            // the inspector, then the component re-enabled mid-setup) - never dispatch or draw
            // with a dead pool. Cheap, and turns a hard ArgumentNullException into a silent idle.
            if (_particles == null || _counters == null || particleCompute == null) return;

            // Gate on the surf layer being live: BuildShoreFoamState().Active is the same signal
            // the ripple-sim foam injection uses (bed depth + SDF baked, surf enabled, foam gain
            // above zero), and only an ACTIVE state pushes fresh _Surf* uniforms via BindTo - the
            // compute must never run the front math on stale uniforms.
            WaterSimulation.ShoreFoamState shoreFoam = volume.BuildShoreFoamState();
            if (!shoreFoam.Active) return;

            if (volume.IsSimulating && Time.deltaTime > 0f)
                DispatchSimulation(Time.deltaTime, shoreFoam);

            if (volume.IsVisibleToCamera)
                Draw();
        }

        void DispatchSimulation(float dt, in WaterSimulation.ShoreFoamState shoreFoam)
        {
            ComputeShader cs = particleCompute;

            // The shared binder pushes the Layer A field textures + the _Surf* front-field
            // uniforms onto each kernel that reads them, so the particles' front evaluation can
            // never drift from the surface render or the sim's whitewash injection.
            shoreFoam.BindTo(cs, _kEmit);
            shoreFoam.BindTo(cs, _kUpdate);

            cs.SetInt(ID_Capacity, _capacityPow2);
            cs.SetInt(ID_FrameSeed, unchecked((int)(Time.frameCount * FrameSeedHashPrime)));
            cs.SetFloat(ID_DeltaTime, dt);
            // The master beat's real advance since the previous dispatch (see _prevBeatTime).
            float beatDelta = _prevBeatValid ? shoreFoam.Time - _prevBeatTime : dt;
            _prevBeatTime = shoreFoam.Time;
            _prevBeatValid = true;
            cs.SetFloat(ID_BeatDeltaTime, beatDelta);
            // The still-water plane the front heights ride on. Roller heights are composed as
            // plane + front field only (the ambient swell is faded out under the fronts by
            // design - _SurfAmbientFade - so this stays visually glued near the break line).
            cs.SetFloat(ID_WaterPlaneY, volume.VolumeCenter.y);

            cs.SetFloat(ID_ParticlesPerMeter, particlesPerMeter);
            cs.SetFloat(ID_EmitBurst, burstPerArrival);
            cs.SetFloat(ID_SizeMin, sizeMin);
            cs.SetFloat(ID_SizeMax, Mathf.Max(sizeMin, sizeMax));
            cs.SetFloat(ID_SizeHeroPower, Mathf.Max(1f, sizeHeroPower));
            cs.SetFloat(ID_TumbleSpeed, tumbleSpeed);
            cs.SetFloat(ID_SprayShare, spraySharePct);
            cs.SetFloat(ID_LifeTail, lifeTailSeconds);
            cs.SetFloat(ID_MasterGain, masterGain);
            cs.SetFloat(ID_EmitWindowLength, EmitWindowLengthMeters);

            cs.SetBuffer(_kClearCounters, ID_Counters, _counters);
            cs.Dispatch(_kClearCounters, 1, 1, 1);

            // Emit only when the break line solved: existing particles keep self-solving their
            // front position in Update regardless, so a temporarily unsolvable camera (off-field,
            // no crossing) fades the system out instead of freezing it.
            if (TryUpdateEmissionWindow(dt))
            {
                int slotCount = Mathf.Min(
                    Mathf.CeilToInt(EmitWindowLengthMeters * particlesPerMeter), MaxEmitSlots);
                cs.SetInt(ID_EmitSlotCount, slotCount);
                cs.SetVector(ID_EmitWindowCenter, new Vector4(_followCenter.x, _followCenter.y, 0f, 0f));
                cs.SetVector(ID_EmitWindowAlong, new Vector4(_followAlong.x, _followAlong.y, 0f, 0f));
                cs.SetBuffer(_kEmit, ID_Particles, _particles);
                cs.SetBuffer(_kEmit, ID_Counters, _counters);
                cs.Dispatch(_kEmit, (slotCount + EmitThreadGroupSize - 1) / EmitThreadGroupSize, 1, 1);
            }

            cs.SetBuffer(_kUpdate, ID_Particles, _particles);
            cs.Dispatch(_kUpdate, _capacityPow2 / UpdateThreadGroupSize, 1, 1);
        }

        // Solve the break line near the camera (WaterSurfBreakLine, shared with WaterSurfCurl:
        // closed-form reads of the shore field's CPU arrays, no readback, crossing where the
        // mean set wave first satisfies overCap = 1) and glide the emission window onto it.
        bool TryUpdateEmissionWindow(float dt)
        {
            if (WaterSurfBreakLine.TrySolve(volume, out Vector2 targetCenter, out Vector2 targetAlong))
            {
                // Continuity flip against our own smoothed frame (the shared solve returns the
                // raw crest-parallel direction) so the slot row never spins 180 degrees.
                if (_followValid && Vector2.Dot(targetAlong, _followAlong) < 0f)
                    targetAlong = -targetAlong;
                // The DIRECTION still glides (a snapping frame would spin the slot row), but the
                // CENTRE is world-snapped along the coast (see WindowSnapMeters): decompose the
                // solved hit into along/across coordinates in the smoothed frame, quantize the
                // alongshore part, keep the across part exactly on the break line. Slots derived
                // from this centre are world-stable while the camera roams within a cell.
                float blend = _followValid
                    ? 1f - Mathf.Exp(-dt / WaterSurfBreakLine.FollowSmoothingSeconds)
                    : 1f;
                _followAlong = Vector2.Lerp(_followAlong, targetAlong, blend).normalized;
                Vector2 acrossDir = new Vector2(-_followAlong.y, _followAlong.x);
                float alongCoord = Vector2.Dot(targetCenter, _followAlong);
                float acrossCoord = Vector2.Dot(targetCenter, acrossDir);
                float snappedAlong = Mathf.Round(alongCoord / WindowSnapMeters) * WindowSnapMeters;
                _followCenter = _followAlong * snappedAlong + acrossDir * acrossCoord;
                _followValid = true;
            }
            return _followValid;
        }

        void Draw()
        {
            // The body's own uniforms (sun, fog, frames) ride in the same block; the roller
            // shader only reads the light/sun pair, but sharing the full block keeps this draw
            // consistent with every other per-body renderer.
            volume.WriteBodyProps(_mpb);
            _mpb.SetBuffer(ID_ParticlesShader, _particles);
            WaterParticlePool.WriteFlipbook(_mpb, flipbookGrid, flipbookFps);

            var rp = new RenderParams(_materialInstance != null ? _materialInstance : particleMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _mpb
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, _capacityPow2 * VerticesPerParticle);
        }
    }
}
