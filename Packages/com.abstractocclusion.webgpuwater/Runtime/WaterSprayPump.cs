// WebGpuWater - water-driven spray emitter ("spray pump").
//
// Floats a probe point on the water surface and throws spray through the shared WaterSplashEmitter
// whenever the probe and the surface under it CLOSE ON EACH OTHER quickly. That single relative-motion
// signal covers both cases the effect is for:
//   - a wave rushing UP at a (near-)static point  -> spray bursting off a rock / pier,
//   - a hull driving DOWN through the surface      -> spray thrown in front of a boat.
//
// Contrast with WaterSplash, which triggers on a Rigidbody punching down through the waterline: that
// stays silent for a stationary object no matter how hard the water hits it. This measures the water's
// motion relative to the tracked point, so a fixed rock still sprays when a fast wave arrives.
//
// Step 1 of the "WOW pass": a single probe. The per-probe temporal state lives in one struct so the
// array pass can hold many probes without reshaping the trigger maths.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    public class WaterSprayPump : MonoBehaviour
    {
        // ---- serialized defaults (named so no literal is buried in the field initializers) ----
        const float DefaultSurfaceBand = 0.25f;
        const float DefaultMinImpactSpeed = 0.6f;
        const float DefaultMaxImpactSpeed = 4.0f;
        const float DefaultEmitCooldownSeconds = 0.06f;
        const float DefaultSprayRadius = 0.25f;

        // ---- internal guards ----
        // Below this frame time the finite-difference closing speed is numerically unstable: a single
        // hitched frame would read as an enormous impact and fire a false burst, so such frames are skipped.
        const float MinFrameDeltaSeconds = 1e-4f;
        // Floors the min..max span so a misconfigured maxImpactSpeed <= minImpactSpeed can't divide by zero.
        const float MinImpactSpeedSpan = 1e-3f;

        [Header("Probe")]
        [Tooltip("Local-space offset from this object's origin where the surface is sampled and spray is thrown.")]
        [SerializeField] Vector3 probePoint = Vector3.zero;

        [Tooltip("Only spray while the probe sits within this vertical distance (world units) of the surface, " +
                 "so a point held in mid-air or dragged deep underwater stays silent.")]
        [Min(0f)] [SerializeField] float surfaceBand = DefaultSurfaceBand;

        [Header("Trigger")]
        [Tooltip("Closing speed (world units/sec) between probe and surface below which nothing sprays.")]
        [Min(0f)] [SerializeField] float minImpactSpeed = DefaultMinImpactSpeed;

        [Tooltip("Closing speed that produces the strongest spray; faster impacts clamp to full strength.")]
        [Min(0f)] [SerializeField] float maxImpactSpeed = DefaultMaxImpactSpeed;

        [Tooltip("Minimum seconds between two bursts from this probe, so a sustained impact doesn't emit every frame.")]
        [Min(0f)] [SerializeField] float emitCooldownSeconds = DefaultEmitCooldownSeconds;

        [Header("Spray")]
        [Tooltip("World radius of each spray burst passed to the emitter.")]
        [Min(0f)] [SerializeField] float sprayRadius = DefaultSprayRadius;

        [Tooltip("Shared splash emitter. Auto-found in the scene if left empty.")]
        [SerializeField] WaterSplashEmitter emitter;

        [Tooltip("Sample the analytic surface only (ignore this object's own interactive ripples), so a " +
                 "self-emitting mover isn't re-triggered by its own wake.")]
        [SerializeField] bool ignoreOwnRipples = false;

        ProbeState _state;

        void Start()
        {
            if (emitter == null) emitter = FindFirstObjectByType<WaterSplashEmitter>();
            if (emitter == null)
                Debug.LogWarning($"{nameof(WaterSprayPump)} on '{name}' found no {nameof(WaterSplashEmitter)} " +
                                 "in the scene; it will detect impacts but emit nothing until one is assigned.", this);
        }

        // Drop stale history so a re-enable (or leaving and re-entering the water) can't diff a gap across
        // the missing frames and fire a phantom burst.
        void OnDisable() => _state = default;

        // LateUpdate: sample AFTER the sims have stepped this frame, so the surface reflects the current
        // waves - the same ordering WaterSplashEmitter's droplet drift relies on.
        void LateUpdate()
        {
            float deltaSeconds = Time.deltaTime;
            if (deltaSeconds < MinFrameDeltaSeconds) return;

            Vector3 world = transform.TransformPoint(probePoint);

            // Resolve the body under the probe each frame, so a probe crossing between two lakes samples
            // the right one. ignoreOwnRipples feeds the analytic-only surface for self-emitting movers.
            WaterVolume body = WaterVolume.BodyContaining(world);
            if (body == null || !body.SampleHeight(world, out WaterSample sample, 0f, ignoreOwnRipples))
            {
                _state.HasHistory = false; // outside the footprint / no reading: don't diff across the gap
                return;
            }

            float gap = world.y - sample.Height; // + above the surface, - below
            TryEmit(world, sample.Height, gap, deltaSeconds);

            _state.PreviousGap = gap;
            _state.HasHistory = true;
        }

        void TryEmit(Vector3 world, float surfaceHeight, float gap, float deltaSeconds)
        {
            if (!_state.HasHistory) return;              // need two frames to measure a closing speed
            if (Mathf.Abs(gap) > surfaceBand) return;    // not at the waterline
            if (Time.time < _state.NextEmitTime) return; // cooling down
            if (emitter == null) return;                 // warned once in Start; nothing to emit through

            // Positive when probe and surface are converging: the wave rose toward the probe, or the probe
            // drove toward the water. One number captures the rock-hit and the boat-plunge alike.
            float closingSpeed = (_state.PreviousGap - gap) / deltaSeconds;
            if (closingSpeed < minImpactSpeed) return;

            float span = Mathf.Max(MinImpactSpeedSpan, maxImpactSpeed - minImpactSpeed);
            float strength = Mathf.Clamp01((closingSpeed - minImpactSpeed) / span);

            Vector3 surfacePoint = new Vector3(world.x, surfaceHeight, world.z);
            emitter.EmitSplash(surfacePoint, strength, sprayRadius);
            _state.NextEmitTime = Time.time + emitCooldownSeconds;
        }

        // Per-probe temporal state. One instance today; the array pass keeps an array of these, one per point.
        struct ProbeState
        {
            public float PreviousGap;
            public float NextEmitTime;
            public bool HasHistory;
        }
    }
}
