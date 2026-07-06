// WebGpuWater - real underwater fog render feature (URP, RenderGraph).
// Fogs the whole view when the camera is submerged in ANY water body, replacing the per-object
// trick for the camera-underwater case. Add this feature once to the renderer used by the water
// camera and assign the WaterUnderwaterFog shader; it self-gates on WaterVolume.UnderwaterFogActive,
// so above water it never enqueues and nothing changes.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterUnderwaterFogFeature : ScriptableRendererFeature
    {
        [Tooltip("The WebGpuWater/WaterUnderwaterFog shader. Assign the shader asset of that name.")]
        [SerializeField] Shader underwaterFogShader;

        WaterUnderwaterFogPass _pass;
        Material _material;

        public override void Create()
        {
            if (underwaterFogShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(underwaterFogShader);
            _pass = new WaterUnderwaterFogPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;                     // shader unassigned / not created
            if (!WaterVolume.UnderwaterFogActive) return;  // ocean: submerged only; pond: whenever fog is on
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
