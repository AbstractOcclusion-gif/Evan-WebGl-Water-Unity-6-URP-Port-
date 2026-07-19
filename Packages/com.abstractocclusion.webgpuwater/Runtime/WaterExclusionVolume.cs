// WebGpuWater - dry-region exclusion volume (analytic OBB, Phase 1).
// Marks an oriented box (transform pose + Size, scaled by lossyScale) in which the
// water surface must NOT render: a boat's hull interior, a submarine room, a house
// below sea level. Registers into a static list exactly like WaterInteractable;
// WaterUniformPublisher publishes the active volumes as global uniforms each frame
// and WaterSurface.shader discards fragments inside any of them (WaterExclusion.hlsl).
// Purely visual + camera-state: buoyancy, physics and the ripple sim are untouched -
// the hull still floats and still carves a wake.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterExclusionVolume : MonoBehaviour
    {
        // GPU pair: EXCLUSION_MAX_VOLUMES in Runtime/Shaders/WaterExclusion.hlsl.
        // WaterWaveConstantsValidator guards the pair, so a drift is a console error.
        internal const int MaxVolumes = 4;

        // Floor on a box edge so a zero Size (or a zero parent scale) can never produce a
        // singular world->box matrix; well under any visually meaningful volume.
        const float MinEdgeLength = 1e-4f;

        static readonly List<WaterExclusionVolume> _active = new List<WaterExclusionVolume>();

        /// <summary>All currently enabled exclusion volumes, for the uniform publisher.
        /// Read-only to callers; membership is managed by OnEnable/OnDisable.</summary>
        public static IReadOnlyList<WaterExclusionVolume> Active => _active;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState()
        {
            _active.Clear();
            _warnedOverLimit = false;
        }

        // The over-limit drop is warned ONCE (editor only, re-armed when the count drops back
        // under the cap) - a per-frame publisher log would flood the console, silence would
        // hide the truncation. Never a silent cap.
        static bool _warnedOverLimit;

        [Tooltip("Edge lengths of the dry box in local units (like BoxCollider Size); the " +
                 "transform's position, rotation and scale place it in the world. The water " +
                 "surface is never rendered inside this box.")]
        public Vector3 size = Vector3.one;

        void OnEnable()
        {
            if (!_active.Contains(this)) _active.Add(this);
        }

        void OnDisable()
        {
            _active.Remove(this);
        }

        /// <summary>World -> unit-box matrix for this volume: one matrix carries centre +
        /// rotation + size, so the shader's inside test is abs(local) &lt;= 0.5 per axis.
        /// Built from position/rotation/lossyScale (the BoxCollider approximation: shear
        /// from non-uniformly scaled rotated parents is ignored).</summary>
        internal Matrix4x4 WorldToBoxMatrix()
        {
            Vector3 edge = Vector3.Scale(size, transform.lossyScale);
            edge = new Vector3(Mathf.Max(Mathf.Abs(edge.x), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.y), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.z), MinEdgeLength));
            return Matrix4x4.TRS(transform.position, transform.rotation, edge).inverse;
        }

        /// <summary>Fill <paramref name="target"/> (length MaxVolumes exactly) with the
        /// world->box matrices of up to MaxVolumes active volumes and return the count used.
        /// Over the limit, the volumes NEAREST <paramref name="referencePoint"/> (the target
        /// camera) win and the drop is logged once - never a silent cap. Allocation-free:
        /// nearest-selection runs in place over the small active list.</summary>
        internal static int WriteMatrices(Matrix4x4[] target, Vector3 referencePoint)
        {
            if (target == null || target.Length != MaxVolumes)
                throw new System.ArgumentException(
                    $"WriteMatrices needs a persistent buffer of exactly {MaxVolumes} matrices " +
                    "(Unity locks a global array's size at its first set).", nameof(target));

            int activeCount = _active.Count;
            if (activeCount <= MaxVolumes)
            {
                _warnedOverLimit = false;
                for (int i = 0; i < activeCount; i++)
                    target[i] = _active[i].WorldToBoxMatrix();
                return activeCount;
            }

            WarnOverLimitOnce(activeCount);
            SelectNearest(target, referencePoint);
            return MaxVolumes;
        }

        // Selection-sort the MaxVolumes nearest volumes into the buffer without allocating:
        // the active list is tiny (a handful of rooms), so O(count * MaxVolumes) is nothing.
        static void SelectNearest(Matrix4x4[] target, Vector3 referencePoint)
        {
            for (int slot = 0; slot < MaxVolumes; slot++)
            {
                int best = -1;
                float bestSqr = float.MaxValue;
                for (int i = 0; i < _active.Count; i++)
                {
                    if (AlreadySelected(i, slot)) continue;
                    float sqr = (_active[i].transform.position - referencePoint).sqrMagnitude;
                    if (sqr >= bestSqr) continue;
                    bestSqr = sqr;
                    best = i;
                }
                _selected[slot] = best;
                target[slot] = _active[best].WorldToBoxMatrix();
            }
        }

        // Scratch indices for SelectNearest (static: the publisher runs on the main thread).
        static readonly int[] _selected = new int[MaxVolumes];

        static bool AlreadySelected(int index, int slotsFilled)
        {
            for (int s = 0; s < slotsFilled; s++)
                if (_selected[s] == index) return true;
            return false;
        }

        static void WarnOverLimitOnce(int activeCount)
        {
            if (_warnedOverLimit) return;
            _warnedOverLimit = true;
#if UNITY_EDITOR
            Debug.LogWarning($"WaterExclusionVolume: {activeCount} volumes are enabled but the " +
                             $"shader supports {MaxVolumes}; only the {MaxVolumes} nearest the " +
                             "camera are excluded this frame. Disable some volumes, or raise " +
                             "MaxVolumes together with EXCLUSION_MAX_VOLUMES (validator-paired).");
#endif
        }

#if UNITY_EDITOR
        // Editor-only wire box so the dry region is visible while authoring.
        static readonly Color GizmoColor = new Color(0f, 0.85f, 0.9f, 0.9f); // package cyan

        void OnDrawGizmos()
        {
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation,
                                          Vector3.Scale(size, transform.lossyScale));
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
