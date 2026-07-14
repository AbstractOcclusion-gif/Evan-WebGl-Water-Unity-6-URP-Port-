// WebGpuWater - shoreline shallow-water (SWE) sim zone driver (Layer C, C1).
//
// Owns a camera-following, texel-snapped world-frame grid near the waterline and the two
// ping-pong state RTs the Saint-Venant kernels integrate (WaterShoreSwe.compute). It consumes
// Layer A's world-frame seabed depth + shoreline SDF (WaterShoreDepthField) and a distributed
// swell pump, producing an emergent breaking/run-up height+velocity field. The state is published
// as _ShoreSweTex (+ its world frame) so the surface can visualize it (C1) and, later, add it (C2).
//
// Camera-follow reuses the proven WaterSimWindow idiom: project the camera onto the water plane,
// snap the zone centre to the SWE texel lattice, and Scroll the state by the integer texel delta so
// features stay world-anchored. The zone is world-axis-aligned (like the Layer A field it samples),
// so no rotation math is needed. WebGPU-safe: Src sampled / Dst write-only float4 state; half-float
// depth/SDF sampled linearly; a texture is always bound so the backend never sees an unbound sampler.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterShoreSwe
    {
        // Must equal [numthreads(...)] in WaterShoreSwe.compute.
        public const int ThreadGroupSize = 8;

        // Fixed substep + cap: the explicit integrator is CFL-bound, so time is consumed in small
        // fixed steps regardless of frame rate (Crest's accumulator model), capped so a hitch can't
        // trigger a runaway substep burst.
        const float FixedDt = 1f / 120f;
        const int MaxSubsteps = 4;

        // Physical constants that are not worth an inspector knob for C1.
        const float Gravity = 9.81f;
        const float Friction = 1f;      // Manning drag master (0 = off)
        const float Relax = 0.02f;      // OceanRelax: bleeds off-band displacement back to sea level
        const float FoamDecay = 0.95f;  // per-substep foam survival (Layer D scratch)

        const string KernelClear = "Clear";
        const string KernelStepVelocity = "StepVelocity";
        const string KernelStepHeight = "StepHeight";
        const string KernelScroll = "Scroll";

        // Compute-side property ids.
        static readonly int ID_Src = Shader.PropertyToID("Src");
        static readonly int ID_Dst = Shader.PropertyToID("Dst");
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_Delta = Shader.PropertyToID("_Delta");
        static readonly int ID_SweCenter = Shader.PropertyToID("_SweCenter");
        static readonly int ID_SweHalfSize = Shader.PropertyToID("_SweHalfSize");
        static readonly int ID_SweTexelWorld = Shader.PropertyToID("_SweTexelWorld");
        static readonly int ID_SweDt = Shader.PropertyToID("_SweDt");
        static readonly int ID_SweGravity = Shader.PropertyToID("_SweGravity");
        static readonly int ID_SweFriction = Shader.PropertyToID("_SweFriction");
        static readonly int ID_SweMaxVel = Shader.PropertyToID("_SweMaxVel");
        static readonly int ID_SweRelax = Shader.PropertyToID("_SweRelax");
        static readonly int ID_SweFoamDecay = Shader.PropertyToID("_SweFoamDecay");
        static readonly int ID_SweBand = Shader.PropertyToID("_SweBand");
        static readonly int ID_SwePumpGain = Shader.PropertyToID("_SwePumpGain");
        static readonly int ID_SwePushGain = Shader.PropertyToID("_SwePushGain");
        static readonly int ID_SweSwellHeight = Shader.PropertyToID("_SweSwellHeight");
        static readonly int ID_SweSwellWavelength = Shader.PropertyToID("_SweSwellWavelength");
        static readonly int ID_SweSwellDirXZ = Shader.PropertyToID("_SweSwellDirXZ");
        static readonly int ID_SweWaveTime = Shader.PropertyToID("_SweWaveTime");
        static readonly int ID_ScrollOffset = Shader.PropertyToID("_ScrollOffset");
        static readonly int ID_ShoreDepthTex = Shader.PropertyToID("_ShoreDepthTex");
        static readonly int ID_ShoreSDFTex = Shader.PropertyToID("_ShoreSDFTex");
        static readonly int ID_ShoreDepthCenter = Shader.PropertyToID("_ShoreDepthCenter");
        static readonly int ID_ShoreDepthSize = Shader.PropertyToID("_ShoreDepthSize");
        static readonly int ID_ShoreWaterLevel = Shader.PropertyToID("_ShoreWaterLevel");
        static readonly int ID_ShoreShoalDepth = Shader.PropertyToID("_ShoreShoalDepth");

        // Published (graphics) globals: the state field + its world frame + debug flags.
        static readonly int ID_SweTexG = Shader.PropertyToID("_ShoreSweTex");
        static readonly int ID_SweCenterG = Shader.PropertyToID("_ShoreSweCenter");
        static readonly int ID_SweHalfSizeG = Shader.PropertyToID("_ShoreSweHalfSize");
        static readonly int ID_SweValidG = Shader.PropertyToID("_ShoreSweValid");
        static readonly int ID_SweDebugG = Shader.PropertyToID("_ShoreSweDebug");

        // Debug viz is a global toggled from the WaterVolume context menu; static so it survives the
        // per-body republish each frame (mirrors WaterShoreDepthField's debug flags).
        static bool _debugEnabled;

        readonly WaterVolume _body;
        readonly ComputeShader _cs;
        readonly int _res, _groups;
        readonly float _halfSize;    // world half-extent of the (square) zone
        readonly float _texelWorld;  // world metres per texel
        readonly int _kClear, _kStepVelocity, _kStepHeight, _kScroll;

        RenderTexture _a, _b;        // ping-pong state (h, velX, velY, foam)

        Vector2 _center;             // world XZ centre of the zone (texel-snapped)
        int _cellX, _cellZ;          // zone centre as integer texel indices on the world lattice
        bool _centerInit;
        float _timeAccum;            // fixed-step accumulator
        bool _active;                // Layer A field baked this frame -> the zone runs and publishes valid

        internal WaterShoreSwe(ComputeShader cs, int resolution, WaterVolume body, float zoneMeters)
        {
            _cs = cs != null ? cs : throw new System.ArgumentNullException(nameof(cs));
            _body = body ?? throw new System.ArgumentNullException(nameof(body));

            _res = Mathf.Max(ThreadGroupSize, (resolution / ThreadGroupSize) * ThreadGroupSize);
            _groups = _res / ThreadGroupSize;
            _halfSize = Mathf.Max(1f, zoneMeters);
            _texelWorld = 2f * _halfSize / _res;

            _kClear = cs.FindKernel(KernelClear);
            _kStepVelocity = cs.FindKernel(KernelStepVelocity);
            _kStepHeight = cs.FindKernel(KernelStepHeight);
            _kScroll = cs.FindKernel(KernelScroll);

            _a = Create("ShoreSweStateA");
            _b = Create("ShoreSweStateB");
            _center = new Vector2(body.VolumeCenter.x, body.VolumeCenter.z);
            ClearState();
        }

        internal static void ToggleDebug() => _debugEnabled = !_debugEnabled;

        RenderTexture Create(string name)
        {
            var rt = new RenderTexture(_res, _res, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point, // float32 is not filterable on WebGPU; read via Load
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = name,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            return rt;
        }

        void ClearState()
        {
            _cs.SetFloat(ID_Size, _res);
            _cs.SetTexture(_kClear, ID_Dst, _a);
            _cs.Dispatch(_kClear, _groups, _groups, 1);
            _cs.SetTexture(_kClear, ID_Dst, _b);
            _cs.Dispatch(_kClear, _groups, _groups, 1);
        }

        /// <summary>Advance the zone: follow the camera, then run the fixed-step Saint-Venant solve
        /// consuming Layer A's depth + SDF and the (already-shoaled) primary-swell pump. No-op (and
        /// publishes an inactive field) until Layer A is baked. Render-only; readback-free.</summary>
        internal void Step(float dt, WaterShoreDepthField shore, float swellHeight, float swellWavelength,
                           float headingRad, float waveTime, float shoalDepth, float pumpGain, float pushGain)
        {
            _active = shore != null && shore.DepthBaked && shore.DepthTexture != null;
            if (!_active) { Publish(); return; }

            Track();

            SetScalars(shore, swellHeight, swellWavelength, headingRad, waveTime, shoalDepth, pumpGain, pushGain);

            _timeAccum += Mathf.Max(0f, dt);
            int steps = Mathf.Min(MaxSubsteps, Mathf.FloorToInt(_timeAccum / FixedDt));
            _timeAccum -= steps * FixedDt;

            Texture depthTex = shore.DepthTexture;
            Texture sdfTex = shore.SdfTexture != null ? shore.SdfTexture : (Texture)Texture2D.blackTexture;
            for (int i = 0; i < steps; i++)
            {
                Dispatch(_kStepVelocity, depthTex, sdfTex);
                Dispatch(_kStepHeight, depthTex, sdfTex);
            }

            Publish();
        }

        // Bind the ping-pong + Layer A textures onto a kernel, dispatch, and swap so _a is the latest.
        void Dispatch(int kernel, Texture depthTex, Texture sdfTex)
        {
            _cs.SetTexture(kernel, ID_Src, _a);
            _cs.SetTexture(kernel, ID_Dst, _b);
            _cs.SetTexture(kernel, ID_ShoreDepthTex, depthTex);
            _cs.SetTexture(kernel, ID_ShoreSDFTex, sdfTex);
            _cs.Dispatch(kernel, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
        }

        // Per-Step scalar/frame uniforms (compute-wide; textures are bound per-kernel in Dispatch).
        void SetScalars(WaterShoreDepthField shore, float swellHeight, float swellWavelength,
                        float headingRad, float waveTime, float shoalDepth, float pumpGain, float pushGain)
        {
            _cs.SetFloat(ID_Size, _res);
            _cs.SetVector(ID_Delta, new Vector4(1f / _res, 1f / _res, 0f, 0f));
            _cs.SetVector(ID_SweCenter, new Vector4(_center.x, _center.y, 0f, 0f));
            _cs.SetVector(ID_SweHalfSize, new Vector4(_halfSize, _halfSize, 0f, 0f));
            _cs.SetVector(ID_SweTexelWorld, new Vector4(_texelWorld, _texelWorld, 0f, 0f));

            _cs.SetFloat(ID_SweDt, FixedDt);
            _cs.SetFloat(ID_SweGravity, Gravity);
            _cs.SetFloat(ID_SweFriction, Friction);
            _cs.SetFloat(ID_SweMaxVel, 0.5f * _texelWorld / FixedDt); // CFL: <= half a texel per substep
            _cs.SetFloat(ID_SweRelax, Relax);
            _cs.SetFloat(ID_SweFoamDecay, FoamDecay);

            // Near-shore band: fully active in shallow water, off beyond the shoaling depth (tie the
            // band to the same depth scale Layer B shoals over so the two agree).
            float bandFar = Mathf.Max(2f, shoalDepth);
            _cs.SetVector(ID_SweBand, new Vector4(bandFar * 0.2f, bandFar, 0f, 0f));
            _cs.SetFloat(ID_SwePumpGain, pumpGain);
            _cs.SetFloat(ID_SwePushGain, pushGain);

            _cs.SetFloat(ID_SweSwellHeight, swellHeight);
            _cs.SetFloat(ID_SweSwellWavelength, swellWavelength);
            _cs.SetVector(ID_SweSwellDirXZ, new Vector4(Mathf.Cos(headingRad), Mathf.Sin(headingRad), 0f, 0f));
            _cs.SetFloat(ID_SweWaveTime, waveTime);

            _cs.SetVector(ID_ShoreDepthCenter, new Vector4(shore.FieldCenter.x, shore.FieldCenter.y, 0f, 0f));
            _cs.SetVector(ID_ShoreDepthSize, new Vector4(shore.FieldHalfSize.x, shore.FieldHalfSize.y, 0f, 0f));
            _cs.SetFloat(ID_ShoreWaterLevel, shore.FieldWaterLevel);
            _cs.SetFloat(ID_ShoreShoalDepth, shoalDepth);
        }

        // Follow the camera/focus on the water plane, snap the zone centre to the texel lattice, and
        // scroll the state by the integer texel delta so features stay world-anchored.
        void Track()
        {
            Transform focus = _body.simWindowFocus;
            Camera cam = _body.targetCamera;
            if (focus == null && cam == null) return;
            Transform follow = focus != null ? focus : cam.transform;

            Vector3 up = _body.VolumeUp;
            Vector3 followPos = follow.position;
            Vector3 onPlane = followPos - Vector3.Dot(followPos - _body.VolumeCenter, up) * up;

            // World-axis-aligned lattice anchored to the world origin (like the Layer A field frame).
            int cellX = Mathf.RoundToInt(onPlane.x / _texelWorld);
            int cellZ = Mathf.RoundToInt(onPlane.z / _texelWorld);

            if (!_centerInit)
            {
                _cellX = cellX; _cellZ = cellZ;
                _centerInit = true;
            }
            else
            {
                int dx = cellX - _cellX;
                int dz = cellZ - _cellZ;
                if (dx != 0 || dz != 0)
                {
                    Scroll(-dx, -dz); // Dst[p] = Src[p - offset]: -delta keeps world features fixed
                    _cellX = cellX; _cellZ = cellZ;
                }
            }
            _center = new Vector2(_cellX * _texelWorld, _cellZ * _texelWorld);
        }

        void Scroll(int offsetX, int offsetZ)
        {
            if (offsetX == 0 && offsetZ == 0) return;
            _cs.SetFloat(ID_Size, _res);
            _cs.SetInts(ID_ScrollOffset, offsetX, offsetZ);
            _cs.SetTexture(_kScroll, ID_Src, _a);
            _cs.SetTexture(_kScroll, ID_Dst, _b);
            _cs.Dispatch(_kScroll, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
        }

        // Publish the state + its world frame + debug flags. Always binds a texture (black fallback +
        // valid = 0 when inactive) so the surface never samples an unbound texture on WebGPU.
        internal void Publish()
        {
            Shader.SetGlobalTexture(ID_SweTexG, _active ? (Texture)_a : Texture2D.blackTexture);
            Shader.SetGlobalVector(ID_SweCenterG, new Vector4(_center.x, _center.y, 0f, 0f));
            Shader.SetGlobalVector(ID_SweHalfSizeG, new Vector4(_halfSize, _halfSize, 0f, 0f));
            Shader.SetGlobalFloat(ID_SweValidG, _active ? 1f : 0f);
            Shader.SetGlobalFloat(ID_SweDebugG, _debugEnabled ? 1f : 0f);
        }

        internal void Dispose()
        {
            ReleaseAndDestroy(ref _a);
            ReleaseAndDestroy(ref _b);
        }

        static void ReleaseAndDestroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Object.Destroy(rt); else Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
