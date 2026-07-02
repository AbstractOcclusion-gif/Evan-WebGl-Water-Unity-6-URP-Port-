// WebGL Water - obstacle footprint renderer (Unity 6 / URP port)
// Draws every WaterInteractable top-down into a ping-pong pair of RenderTextures
// (R = submerged amount per column). The compute sim reads (prev - curr) to push
// the surface, generalising the original analytic sphere displacement to any mesh.
using UnityEngine;
using UnityEngine.Rendering;

namespace WebGLWater
{
    public class WaterObstacle
    {
        public RenderTexture Prev => _prev;
        public RenderTexture Curr => _curr;

        const float MinExtent = 1e-4f;        // floor so a zero extent can't collapse the frustum
        const float EyeHeightInExtents = 2f;  // ortho camera sits this many depth-extents above the surface
        const float FarPlaneInExtents = 4f;   // far plane spans this many depth-extents below the eye
        const float OrthoNearPlane = 0.01f;
        const float OrthoFarPad = 0.02f;       // keep the floor just inside the far plane

        // The footprint is rasterized at this multiple of the sim resolution, then
        // box-filtered down (two successive 2x bilinear blits = exact 4x4 box). At 1x, a
        // silhouette edge sweeping a sim texel flipped that column's full submerged
        // thickness on/off between frames - deltas the size of a mouse drop, stamped as
        // micro-ripples whenever an object drifted or slowly ROTATED. Supersampled, the
        // same crossing becomes a smooth 1/16-step coverage ramp.
        const int SupersampleFactor = 4;

        // Shader passes in ObstacleDepth.shader.
        const int PassFootprint = 0;
        const int PassTemporalSmooth = 1;

        readonly Material _mat;
        readonly CommandBuffer _cb;
        readonly MaterialPropertyBlock _mpb;
        readonly int _resolution;
        RenderTexture _prev, _curr;    // temporally SMOOTHED footprints (what the sim diffs)
        RenderTexture _hiRes, _midRes; // supersample chain: hi (4x) -> mid (2x) -> raw (1x)
        RenderTexture _raw;            // this frame's box-filtered footprint, pre-smoothing
        Matrix4x4 _view, _gpuProj;

        static readonly int ID_Waterline = Shader.PropertyToID("_WaterlineY");
        static readonly int ID_DisplaceScale = Shader.PropertyToID("_DisplaceScale");
        static readonly int ID_SmoothedPrev = Shader.PropertyToID("_SmoothedPrev");
        static readonly int ID_TemporalBlend = Shader.PropertyToID("_TemporalBlend");

        public WaterObstacle(Shader obstacleShader, int resolution, Vector3 volumeCenter,
                             Quaternion volumeRotation, Vector3 volumeExtent)
        {
            _resolution = resolution;
            _mat = new Material(obstacleShader);
            _cb = new CommandBuffer { name = "WebGLWater.Obstacle" };
            _mpb = new MaterialPropertyBlock();
            _prev = Create(_resolution);
            _curr = Create(_resolution);
            _raw = Create(_resolution);
            _hiRes = Create(_resolution * SupersampleFactor);
            _midRes = Create(_resolution * SupersampleFactor / 2);

            SetFrame(volumeCenter, volumeRotation, volumeExtent);
        }

        /// <summary>Rebuild the top-down orthographic view/projection for a frame given its
        /// centre, rotation and half-extent. Whole-body bodies set this once (the volume
        /// frame); a windowed large body calls it each frame so the footprint tracks the
        /// scrolling sim window.</summary>
        public void SetFrame(Vector3 center, Quaternion rotation, Vector3 extent)
        {
            // Orthographic view looking DOWN the frame's up axis, so the submerged
            // footprint maps into the RT along the same axis the surface is displaced.
            // Extents (X half-width, Z half-length) set the ortho size; up = frame forward
            // so the RT's u<->x and v<->z (the sim's coordinate convention).
            float ex = Mathf.Max(extent.x, MinExtent);
            float ez = Mathf.Max(extent.z, MinExtent);
            float ey = Mathf.Max(extent.y, MinExtent);
            Vector3 up = rotation * Vector3.up;
            Vector3 eye = center + up * (EyeHeightInExtents * ey);
            Quaternion rot = Quaternion.LookRotation(-up, rotation * Vector3.forward);
            Matrix4x4 camToWorld = Matrix4x4.TRS(eye, rot, Vector3.one);
            _view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * camToWorld.inverse;

            Matrix4x4 proj = Matrix4x4.Ortho(-ex, ex, -ez, ez, OrthoNearPlane, FarPlaneInExtents * ey + OrthoFarPad);
            _gpuProj = GL.GetGPUProjectionMatrix(proj, true); // renderIntoTexture = true
        }

        RenderTexture Create(int resolution)
        {
            var rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "WaterObstacle"
            };
            rt.Create();
            return rt;
        }

        /// <summary>Render the current submerged footprint of all interactables at the
        /// supersampled resolution, box-filter it down to the sim grid, temporally smooth
        /// it (EMA against last frame's result), and ping-pong so Prev holds last frame's
        /// smoothed footprint and Curr holds this frame's. <paramref name="temporalBlend"/>
        /// is the EMA weight of the fresh footprint: 1 = no smoothing, smaller = heavier
        /// low-pass (fewer, longer waves from continuous bobbing/rotation noise).</summary>
        public void Render(float waterY, float temporalBlend)
        {
            (_prev, _curr) = (_curr, _prev);

            _cb.Clear();
            _cb.SetRenderTarget(_hiRes);
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetViewProjectionMatrices(_view, _gpuProj);

            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || it.Renderer == null) continue;

                float waterlineY = it.WaterlineY(waterY);
                if (!it.IsSubmerged(waterlineY)) continue;

                _mpb.SetFloat(ID_Waterline, waterlineY);
                _mpb.SetFloat(ID_DisplaceScale, it.displaceScale);
                it.Renderer.SetPropertyBlock(_mpb);
                _cb.DrawRenderer(it.Renderer, _mat, 0, PassFootprint);
            }

            // Two successive 2x bilinear downsamples = an exact 4x4 box filter: each sim
            // texel becomes the average of 16 rasterized samples, so silhouette motion
            // (drift AND slow rotation) changes coverage smoothly instead of popping.
            _cb.Blit(_hiRes, _midRes);
            _cb.Blit(_midRes, _raw);

            // Temporal EMA: curr = lerp(prev-smoothed, raw, blend). The wave equation is
            // linear, so decimating/accumulating injections changes nothing (superposition);
            // filtering the FREQUENCY content of the forcing is what removes the dense
            // packets of tight rings while keeping the total displaced volume intact.
            _mat.SetTexture(ID_SmoothedPrev, _prev);
            _mat.SetFloat(ID_TemporalBlend, Mathf.Clamp01(temporalBlend));
            _cb.Blit(_raw, _curr, _mat, PassTemporalSmooth);

            Graphics.ExecuteCommandBuffer(_cb);
        }

        public void Dispose()
        {
            if (_prev != null) _prev.Release();
            if (_curr != null) _curr.Release();
            if (_raw != null) _raw.Release();
            if (_hiRes != null) _hiRes.Release();
            if (_midRes != null) _midRes.Release();
            _cb?.Release();
        }
    }
}
