// WebGL Water - GPU heightfield simulation driver (Unity 6 / URP port)
// Owns two RGBAFloat ping-pong RenderTextures and dispatches the compute kernels.
// Port of water.js by Evan Wallace (MIT).
using UnityEngine;

namespace WebGLWater
{
    public class WaterSimulation
    {
        public const int Resolution = 256;

        readonly ComputeShader _cs;
        readonly int _kDrop, _kUpdate, _kNormal, _kObstacle, _kFoam, _kConserve;
        readonly int _groups;

        RenderTexture _a; // current state (height, velocity, normal.x, normal.z)
        RenderTexture _b; // scratch
        RenderTexture _foamA, _foamB; // foam amount ping-pong (R)

        /// <summary>The texture holding the current simulation state.</summary>
        public RenderTexture Texture => _a;

        /// <summary>The current foam amount texture (R channel).</summary>
        public RenderTexture FoamTexture => _foamA;

        public WaterSimulation(ComputeShader cs)
        {
            _cs = cs;
            _kDrop   = cs.FindKernel("Drop");
            _kUpdate = cs.FindKernel("Update");
            _kNormal = cs.FindKernel("Normal");
            _kObstacle = cs.FindKernel("Obstacle");
            _kFoam = cs.FindKernel("Foam");
            _kConserve = cs.FindKernel("Conserve");
            _groups = Resolution / 8;

            _a = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _b = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _foamA = Create(RenderTextureFormat.RFloat, "WaterFoam");
            _foamB = Create(RenderTextureFormat.RFloat, "WaterFoam");
            Clear(_a); Clear(_b); Clear(_foamA); Clear(_foamB);
        }

        static RenderTexture Create(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(Resolution, Resolution, 0, format)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = name
            };
            rt.Create();
            return rt;
        }

        static void Clear(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        public void Dispose()
        {
            if (_a != null) _a.Release();
            if (_b != null) _b.Release();
            if (_foamA != null) _foamA.Release();
            if (_foamB != null) _foamB.Release();
        }

        void Dispatch(int kernel)
        {
            _cs.SetFloat("_Size", Resolution);
            _cs.SetVector("_Delta", new Vector4(1f / Resolution, 1f / Resolution, 0, 0));
            _cs.SetTexture(kernel, "Src", _a);
            _cs.SetTexture(kernel, "Dst", _b);
            _cs.Dispatch(kernel, _groups, _groups, 1);
            (_a, _b) = (_b, _a); // ping-pong: _a is always the latest state
        }

        public void AddDrop(float x, float y, float radius, float strength)
        {
            _cs.SetVector("_Center", new Vector4(x, y, 0, 0));
            _cs.SetFloat("_Radius", radius);
            _cs.SetFloat("_Strength", strength);
            Dispatch(_kDrop);
        }

        /// <summary>Forces the surface by the change in submerged footprint
        /// (prev - curr), generalising the old sphere displacement to any meshes.</summary>
        public void ApplyObstacle(Texture prev, Texture curr, float strength, bool flipY)
        {
            _cs.SetTexture(_kObstacle, "ObstaclePrev", prev);
            _cs.SetTexture(_kObstacle, "ObstacleCurr", curr);
            _cs.SetFloat("_ObstacleStrength", strength);
            _cs.SetFloat("_ObstacleFlipY", flipY ? 1f : 0f);
            Dispatch(_kObstacle);
        }

        public void StepSimulation(float waveSpeed = 2f, float damping = 0.995f)
        {
            _cs.SetFloat("_WaveSpeed", waveSpeed);
            _cs.SetFloat("_Damping", damping);
            Dispatch(_kUpdate);
        }

        public void UpdateNormals() => Dispatch(_kNormal);

        /// <summary>Advance the foam buffer: advect along the surface flow, diffuse,
        /// generate from turbulence, decay. Reads the current height/normal state;
        /// ping-pongs the foam textures.</summary>
        public void StepFoam(float genRate, float decay, float spread, float fromSpeed, float fromCurv, float advect)
        {
            _cs.SetFloat("_Size", Resolution);
            _cs.SetVector("_Delta", new Vector4(1f / Resolution, 1f / Resolution, 0, 0));
            _cs.SetFloat("_FoamGenRate", genRate);
            _cs.SetFloat("_FoamDecay", decay);
            _cs.SetFloat("_FoamSpread", spread);
            _cs.SetFloat("_FoamFromSpeed", fromSpeed);
            _cs.SetFloat("_FoamFromCurv", fromCurv);
            _cs.SetFloat("_FoamAdvect", advect);
            _cs.SetTexture(_kFoam, "Src", _a);        // height state (read)
            _cs.SetTexture(_kFoam, "FoamSrc", _foamA);
            _cs.SetTexture(_kFoam, "FoamDst", _foamB);
            _cs.Dispatch(_kFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);
        }

        /// <summary>Subtracts the mean height (from heightMip's top mip) to conserve volume.</summary>
        public void ConserveVolume(RenderTexture heightMip)
        {
            _cs.SetTexture(_kConserve, "HeightMip", heightMip);
            Dispatch(_kConserve);
        }
    }
}
