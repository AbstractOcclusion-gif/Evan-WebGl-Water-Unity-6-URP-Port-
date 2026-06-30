// WebGL Water - marker for objects that interact with the water (Unity 6 / URP).
// Add this to any Renderer that should displace the surface. It self-registers in
// a static list that WaterObstacle iterates each step, so detection is automatic:
// no manual wiring, no per-frame FindObjectsOfType.
using System.Collections.Generic;
using UnityEngine;

namespace WebGLWater
{
    [RequireComponent(typeof(Renderer))]
    public class WaterInteractable : MonoBehaviour
    {
        /// <summary>All currently enabled interactables, for the obstacle pass.</summary>
        public static readonly List<WaterInteractable> Active = new List<WaterInteractable>();

        [Tooltip("Per-object multiplier on how strongly it displaces the water.")]
        public float displaceScale = 1f;

        [Tooltip("Low-pass smoothing of the displaced footprint (0..1). Lower = smoother / " +
                 "more lag, which filters the residual bob so it doesn't feed jitter back " +
                 "into the sim. 1 = no smoothing.")]
        [Range(0.05f, 1f)] public float displaceSmoothing = 0.3f;

        public Renderer Renderer { get; private set; }

        // Smoothed amount actually reported to the obstacle pass (see SubmergedAmount).
        float _emittedAmount;

        void Awake()  { Renderer = GetComponent<Renderer>(); }
        void OnEnable()
        {
            if (Renderer == null) Renderer = GetComponent<Renderer>();
            if (!Active.Contains(this)) Active.Add(this);
        }
        void OnDisable() { Active.Remove(this); }

        /// <summary>How far this object is submerged below the surface, in world
        /// units, approximated from its world-space bounds. Drives the footprint
        /// amount written into the obstacle map.</summary>
        public float SubmergedAmount(float waterY)
        {
            if (Renderer == null) return 0f;
            Bounds b = Renderer.bounds;
            float depth = Mathf.Clamp(waterY - b.min.y, 0f, b.size.y);
            float raw = depth * displaceScale;

            // The obstacle pass displaces by the frame-to-frame CHANGE in this amount,
            // so a floating object's residual micro-bob would inject a ripple every frame
            // and self-excite (worse at higher displace scale). Low-pass the reported
            // amount: it tracks real motion (dropping in, riding a wave) smoothly while
            // attenuating the high-frequency bob, with none of a deadband's step pulses.
            _emittedAmount = Mathf.Lerp(_emittedAmount, raw, displaceSmoothing);
            return Mathf.Max(0f, _emittedAmount);
        }
    }
}
