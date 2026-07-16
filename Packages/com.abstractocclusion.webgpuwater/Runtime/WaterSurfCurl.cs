// WebGpuWater - procedural plunging lip sheet for the surf breaker fronts (CURL layer).
//
// Spawns a dense strip ribbon (the WaterHeroWave lip-sheet pattern via WaterLipSheetRig) whose
// vertices evaluate the surf front field + an attractor-curl rotation (WaterSurfCurl.hlsl): where
// a front classifies as plunging and is at its break moment, the cresting face grows an actual
// overturning barrel - the KWS1 rolling-wave spectacle, generated from the front math instead of
// hand-placed flipbook patches. Two modes (CurlMode):
//
//  - StaticTestRibbon (CURL-1): place/rotate the transform on an open-water body - the crest line
//    spans LEFT<->RIGHT and fronts travel along FORWARD toward a SYNTHETIC plane beach (depth +
//    slope knobs below), so the whole lifecycle can be judged over a flat ocean with nothing
//    baked. The front knobs (wavelength, period, amplitude, lean, sets, crest segments) come from
//    the body's own Shore Waves settings - the _Surf* globals are published every frame even
//    while the surf layer is inactive, so the ribbon and the real coastline share one tuning.
//
//  - FollowBreakLine (CURL-2): the ribbon follows the camera along the coastline's BREAK LINE,
//    solved every frame from the shore field's CPU arrays (closed-form - no readback), samples
//    the REAL Layer A field per vertex and adds ONLY the curl rotation on top of the base surface
//    (delta mode: the water already renders the fronts, so the sheet's foot lands on it exactly).
//
// RENDER-ONLY: buoyancy keeps the heightfield-safe base (LargeWaveField mirrors the un-curled
// front); the sheet never moves physics.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Water Surf Curl")]
    public sealed class WaterSurfCurl : MonoBehaviour
    {
        /// <summary>How the ribbon is placed and what field drives it.</summary>
        public enum CurlMode
        {
            /// <summary>CURL-1 look test: static placement from this transform, a synthetic plane
            /// beach (Center Depth / Beach Slope), the ribbon renders the whole front + curl.
            /// Works on a bare flat ocean with nothing baked.</summary>
            StaticTestRibbon,
            /// <summary>CURL-2 live: the ribbon follows the camera along the BREAK LINE solved
            /// from the baked shore field (CPU arrays - no readback), samples the real Layer A
            /// field per vertex, and adds ONLY the curl rotation on top of the base surface
            /// (delta mode - the water already renders the fronts). Needs the body's surf layer
            /// live (bed depth + SDF baked, surf enabled).</summary>
            FollowBreakLine,
        }

        const string StripObjectName = "WaterSurfCurlSheet";
        const string StripUnderObjectName = "WaterSurfCurlSheetUnder";
        const string StripMeshName = "WaterSurfCurlStrip";
        // View-space metres the sheet is pulled toward the camera where its foot meets the base
        // surface (same mechanism as the hero sheet; independent value).
        const float SheetDepthBiasMeters = 0.02f;
        // Break-line search parameters + smoothing constant live in WaterSurfBreakLine
        // (the ONE march + bisect shared with WaterSurfRollerParticles). Moving the ribbon
        // never moves the waves: the field is world-anchored; placement only decides where
        // sheet GEOMETRY exists.
        // Roll-speed mapping (KEEP IN SYNC with SURF_CURL_ROLL_START and the cresting->broken
        // overCap window in WaterSurfWaves.hlsl). The roll must ALWAYS complete inside the wave's
        // visibility window or the knob changes the achieved ANGLE instead of the timing (the
        // original mapping let slow speeds run past the fade - "roll speed acts on roll size"):
        // the latest completion sits just past peak visibility (cresting tops out at ~1.05), so
        // even the laziest roll reaches full curl while the tube is still clearly visible.
        const float RollStartOverCap = 0.75f;
        const float RollCompleteLatestOverCap = 1.3f;

        [Tooltip("The open-water body whose surf-front settings drive the ribbon. Defaults to a " +
                 "WaterVolume on this object or its parents.")]
        [SerializeField] WaterVolume volume;

        [Tooltip("Static Test Ribbon = CURL-1 look test on a synthetic beach (place with this " +
                 "transform; works on a bare ocean). Follow Break Line = CURL-2 live: the ribbon " +
                 "tracks the camera along the real coastline's break line and adds the plunging " +
                 "lip onto the rendered fronts (needs bed depth + SDF baked and surf enabled).")]
        [SerializeField] CurlMode mode = CurlMode.StaticTestRibbon;

        [Header("Ribbon (static test placement - transform position/yaw)")]
        [Tooltip("Half-length (m) of the ribbon along the crest line (transform left<->right).")]
        [Min(5f)] [SerializeField] float alongHalfLength = 40f;
        [Tooltip("Half-width (m) of the ribbon across the fronts (transform forward = shoreward). " +
                 "Cover at least one front spacing so a whole wave fits - but keep it as narrow as " +
                 "the look test allows: the rolled lip is only ~2-4 m across, and vertex spacing " +
                 "(2 x half-width / across segments) must stay under ~10 cm through the curl or " +
                 "the spiral shows as jittering facets that fold over each other.")]
        [Min(5f)] [SerializeField] float acrossHalfWidth = 15f;
        [Tooltip("Fraction of the along half-length where the lip starts fading toward the ribbon " +
                 "ends (no hard vertical cut where the mesh stops).")]
        [Range(0f, 0.95f)] [SerializeField] float shoulderStartFraction = 0.7f;

        [Header("Synthetic beach (CURL-1 stand-in for the baked shore field)")]
        [Tooltip("Still-water column depth (m) under the ribbon CENTRE. Fronts arrive from the " +
                 "offshore edge, shoal on the synthetic slope and break where the physics says.")]
        [Range(0.1f, 20f)] [SerializeField] float centerDepth = 1.5f;
        [Tooltip("Synthetic beach slope tan(beta) toward the transform's forward. Also feeds the " +
                 "Iribarren classification: ~0.08+ puts the fronts in the plunging regime.")]
        [Range(0.005f, 0.5f)] [SerializeField] float beachSlope = 0.1f;

        [Header("Curl (the overturning lip)")]
        [Tooltip("Maximum roll of the lip in degrees at full curl weight. ~90 pitches the crest " +
                 "forward; ~180-220 throws a full plunging barrel.")]
        [Range(0f, 270f)] [SerializeField] float curlMaxRollDegrees = 200f;
        [Tooltip("Profile fraction where the curl begins: only the part of the front above this " +
                 "fraction of the crest rolls. Lower = thicker lip.")]
        [Range(0.05f, 0.95f)] [SerializeField] float curlStartFraction = 0.5f;
        [Tooltip("Curl pivot distance ahead (shoreward) of the crest, as a fraction of the front's " +
                 "face length. Further ahead = the lip orbits wider and throws further before " +
                 "plunging (the pivot geometry sets the tube size; ~0.35 keeps the arc apex near " +
                 "1.4x the wave height at surf scale).")]
        [Range(0f, 1.5f)] [SerializeField] float pivotAheadFraction = 0.35f;
        [Tooltip("Curl pivot height as a fraction of the local front height. Higher = a rounder, " +
                 "more open tube.")]
        [Range(0f, 1f)] [SerializeField] float pivotHeightFraction = 0.7f;
        [Tooltip("WHEN the overturn completes inside the wave's break window: 1 = the roll " +
                 "finishes just past peak cresting (laziest), higher = snappier (fully barreled " +
                 "earlier, the tube holds longer). The roll always completes, so this changes " +
                 "timing, never the roll size. The break window's total DURATION is physics - " +
                 "wavelength / period on the volume and the beach slope set it.")]
        [Range(1f, 4f)] [SerializeField] float rollSpeed = 1f;
        [Tooltip("Lip BASE thickness, as a multiple of the front's face length: how far down the " +
                 "face the rolling sheet extends. Thicker = a heavier, meatier lip.")]
        [Range(0.3f, 2f)] [SerializeField] float lipBaseThickness = 1f;
        [Tooltip("Lip TIP thickness, as a share of the base thickness on the crest's back side: " +
                 "how much of the back rolls over as the tip of the curl. Thin = a knife-edge " +
                 "throwing lip; thick = a fat pitching slab.")]
        [Range(0.1f, 1f)] [SerializeField] float lipTipThickness = 0.4f;
        [Tooltip("Master gain on the lip weight. 0 = sheet off (the ribbon still shows the plain " +
                 "front in Render Full Front mode).")]
        [Range(0f, 1f)] [SerializeField] float masterGain = 1f;
        [Tooltip("Override the Iribarren plunge gate (test knob): 0 = use the front's own " +
                 "slope-derived plunge weight; above 0 = force this weight so the curl can be " +
                 "judged regardless of the synthetic slope.")]
        [Range(0f, 1f)] [SerializeField] float plungeOverride = 0f;

        [Header("Mode")]
        [Tooltip("CURL-1 look test: the ribbon renders the WHOLE front + curl (flat ocean, no shore " +
                 "field needed). Off = delta mode (CURL-2): the base surface carries the front and " +
                 "the sheet adds only the curl rotation.")]
        [SerializeField] bool renderFullFront = true;

        [Header("Lip sheet mesh")]
        [Tooltip("Strip mesh segments along the crest. Higher = smoother lip silhouette.")]
        [Range(16, 512)] [SerializeField] int stripAlongSegments = 192;
        [Tooltip("Strip mesh segments across the fronts (through the curl). The lip is only a few " +
                 "metres across, so this drives everything: below ~20 vertices through the curl the " +
                 "spiral reads as jittering, self-crossing facets. Keep across spacing under ~10 cm.")]
        [Range(16, 512)] [SerializeField] int stripAcrossSegments = 384;

        Mesh _stripMesh;
        MeshRenderer _stripRenderer;
        MeshRenderer _stripUnderRenderer;
        MaterialPropertyBlock _stripBlock;
        MaterialPropertyBlock _stripUnderBlock;
        // Smoothed follow-mode placement (world xz centre + unit along direction).
        Vector2 _followCenter;
        Vector2 _followAlong = new Vector2(1f, 0f);
        bool _followValid;

        static readonly int ID_IsClipmap = Shader.PropertyToID("_IsClipmap");
        static readonly int ID_IsSurfCurl = Shader.PropertyToID("_IsSurfCurl");
        static readonly int ID_PatchDepthBias = Shader.PropertyToID("_PatchDepthBias");
        static readonly int ID_SurfCurlFrame = Shader.PropertyToID("_SurfCurlFrame");
        static readonly int ID_SurfCurlShape = Shader.PropertyToID("_SurfCurlShape");
        static readonly int ID_SurfCurlParams = Shader.PropertyToID("_SurfCurlParams");
        static readonly int ID_SurfCurlExtent = Shader.PropertyToID("_SurfCurlExtent");
        static readonly int ID_SurfCurlField = Shader.PropertyToID("_SurfCurlField");

        void OnEnable()
        {
            if (volume == null) volume = GetComponentInParent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterSurfCurl: no WaterVolume assigned or found in parents - disabling.", this);
                enabled = false;
                return;
            }
            if (!volume.openWater)
            {
                Debug.LogError("WaterSurfCurl: the assigned body is not open water (Open Water off) - " +
                               "the lip sheet needs the large-body wave path. Disabling.", this);
                enabled = false;
            }
        }

        void OnDisable() => DestroyStrip();

        // LateUpdate, after the volume refreshed its own per-frame state, so the strip's property
        // block copy carries this frame's body uniforms (same seam timing as the hero sheet).
        void LateUpdate()
        {
            EnsureStrip();
            UpdateStrip();
        }

        void EnsureStrip()
        {
            // Live density tuning: if the segment counts changed since the mesh was built, tear the
            // strip down and let the block below rebuild it - density is THE anti-jitter knob for
            // the curl, so it must be adjustable without toggling the component.
            int expectedVertexCount = (stripAlongSegments + 1) * (stripAcrossSegments + 1);
            if (_stripMesh != null && _stripMesh.vertexCount != expectedVertexCount)
                DestroyStrip();

            if (_stripRenderer == null
                && volume.surfaceAbove != null && volume.surfaceAbove.sharedMaterial != null)
            {
                if (_stripMesh == null)
                    _stripMesh = WaterLipSheetRig.BuildStripGrid(StripMeshName,
                                                                 stripAlongSegments, stripAcrossSegments);
                _stripRenderer = WaterLipSheetRig.CreateStripRenderer(volume, _stripMesh,
                    StripObjectName, volume.surfaceAbove.sharedMaterial);
            }
            if (_stripUnderRenderer == null && _stripMesh != null
                && volume.surfaceUnder != null && volume.surfaceUnder.sharedMaterial != null)
            {
                _stripUnderRenderer = WaterLipSheetRig.CreateStripRenderer(volume, _stripMesh,
                    StripUnderObjectName, volume.surfaceUnder.sharedMaterial);
            }
        }

        void UpdateStrip()
        {
            if (_stripRenderer == null) return;

            bool live = mode == CurlMode.FollowBreakLine;
            Vector2 center;
            Vector2 along;
            if (live)
            {
                if (WaterSurfBreakLine.TrySolve(volume, out Vector2 targetCenter, out Vector2 targetAlong))
                {
                    // Continuity flip against our own smoothed frame (the shared solve returns
                    // the raw crest-parallel direction) so the ribbon never spins 180 degrees.
                    if (_followValid && Vector2.Dot(targetAlong, _followAlong) < 0f)
                        targetAlong = -targetAlong;
                    // Glide (never snap) to the new placement; the first solve lands directly.
                    float blend = _followValid
                        ? 1f - Mathf.Exp(-Time.deltaTime / WaterSurfBreakLine.FollowSmoothingSeconds)
                        : 1f;
                    _followCenter = Vector2.Lerp(_followCenter, targetCenter, blend);
                    _followAlong = Vector2.Lerp(_followAlong, targetAlong, blend).normalized;
                    _followValid = true;
                }
                if (!_followValid)
                {
                    // No solvable break line yet (surf layer off, camera off-field, no crossing):
                    // hide the sheets rather than run 74k curl vertices for zero weight.
                    SetStripsVisible(false);
                    return;
                }
                center = _followCenter;
                along = _followAlong;
            }
            else
            {
                center = new Vector2(transform.position.x, transform.position.z);
                along = AlongDirXZ();
            }

            SetStripsVisible(true);
            PositionStrip(_stripRenderer, ref _stripBlock, center, along, live);
            PositionStrip(_stripUnderRenderer, ref _stripUnderBlock, center, along, live);
        }

        void SetStripsVisible(bool visible)
        {
            if (_stripRenderer != null && _stripRenderer.enabled != visible)
                _stripRenderer.enabled = visible;
            if (_stripUnderRenderer != null && _stripUnderRenderer.enabled != visible)
                _stripUnderRenderer.enabled = visible;
        }

        // Park one strip renderer over the ribbon and feed it the body uniforms plus the sheet's
        // own flags + curl state. The strip rides the clipmap vertex mapping (_IsClipmap: verts in
        // world metres via ObjectToWorld).
        void PositionStrip(Renderer strip, ref MaterialPropertyBlock block,
                           Vector2 center, Vector2 along, bool live)
        {
            if (strip == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            volume.WriteBodyProps(block);
            block.SetFloat(ID_IsClipmap, 1f);
            block.SetFloat(ID_IsSurfCurl, 1f);
            block.SetFloat(ID_PatchDepthBias, SheetDepthBiasMeters);
            block.SetVector(ID_SurfCurlFrame, new Vector4(center.x, center.y, along.x, along.y));
            block.SetVector(ID_SurfCurlShape, new Vector4(
                curlMaxRollDegrees * Mathf.Deg2Rad, curlStartFraction,
                pivotAheadFraction, pivotHeightFraction));
            // Live mode is ALWAYS delta (the base surface renders the fronts; the sheet adds only
            // the curl rotation) and samples the real shore field; the test knobs only apply to
            // the static synthetic-beach ribbon.
            float rollEndOverCap = RollStartOverCap
                                 + (RollCompleteLatestOverCap - RollStartOverCap)
                                   / Mathf.Max(rollSpeed, 1f);
            block.SetVector(ID_SurfCurlParams, new Vector4(
                masterGain, live ? 0f : plungeOverride,
                (!live && renderFullFront) ? 1f : 0f, rollEndOverCap));
            block.SetVector(ID_SurfCurlExtent, new Vector4(
                alongHalfLength, acrossHalfWidth, shoulderStartFraction, lipBaseThickness));
            block.SetVector(ID_SurfCurlField, new Vector4(
                centerDepth, beachSlope, live ? 0f : 1f, lipTipThickness));
            strip.SetPropertyBlock(block);

            // along = (cos yaw, -sin yaw) => yaw = atan2(-along.y, along.x). In static mode this
            // reproduces the authoring transform's yaw exactly.
            float yawDegrees = Mathf.Atan2(-along.y, along.x) * Mathf.Rad2Deg;
            Transform stripTransform = strip.transform;
            stripTransform.SetPositionAndRotation(
                new Vector3(center.x, volume.VolumeCenter.y, center.y),
                Quaternion.Euler(0f, yawDegrees, 0f));
            stripTransform.localScale = new Vector3(alongHalfLength, 1f, acrossHalfWidth);
        }

        // Crest-line direction (world xz) from the transform's yaw only, so a tilted authoring
        // transform can never shear the ribbon frame. KEEP IN SYNC with WaterSurfCurl.hlsl
        // (SurfCurlLocalCoords): along = right, travel = (-along.y, along.x) = forward = shoreward.
        Vector2 AlongDirXZ()
        {
            float yawRad = transform.eulerAngles.y * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(yawRad), -Mathf.Sin(yawRad));
        }

        void DestroyStrip()
        {
            WaterLipSheetRig.DestroyRuntimeObject(_stripRenderer != null ? _stripRenderer.gameObject : null);
            WaterLipSheetRig.DestroyRuntimeObject(_stripUnderRenderer != null ? _stripUnderRenderer.gameObject : null);
            WaterLipSheetRig.DestroyRuntimeObject(_stripMesh);
            _stripRenderer = null;
            _stripUnderRenderer = null;
            _stripMesh = null;
            _stripBlock = null;
            _stripUnderBlock = null;
        }
    }
}
