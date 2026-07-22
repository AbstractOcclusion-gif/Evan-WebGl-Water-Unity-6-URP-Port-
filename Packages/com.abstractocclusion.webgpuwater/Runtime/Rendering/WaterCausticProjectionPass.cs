// WebGpuWater - screen-space caustic projection pass (RenderGraph).
// One additive fullscreen pass that reconstructs each opaque pixel's world position from the resolved
// _CameraDepthTexture, reprojects it through the water's pool-space caustic projection, and ADDS the
// caustic pattern onto submerged surfaces. It composites BEFORE the transparent water surface draws, so
// the caustics land on the OPAQUE scene (floor, terrain, props) and the surface then draws over them.
//
// Injection point is AfterRenderingSkybox, which URP records IMMEDIATELY BEFORE it copies the camera
// colour into _CameraOpaqueTexture (UniversalRendererRenderGraph: custom AfterRenderingSkybox passes then
// m_CopyColorPass). That ordering is essential: the transparent water surface refracts by sampling
// _CameraOpaqueTexture, so the caustics must be composited into the opaque scene BEFORE that copy or they
// are invisible THROUGH the surface (they only showed on directly-viewed floor). Running here they land in
// the opaque texture, so the refraction sees them, and direct floor views still show them. ConfigureInput
// (Depth) guarantees _CameraDepthTexture is produced before this early pass.
//
// Two attachments are bound: the camera colour (ReadWrite, so the hardware One-One blend composites onto
// the scene) and the camera DEPTH-STENCIL (Read), whose stencil holds bit 3 written by WaterReceiver /
// AnalyticPool during the opaque pass - the shader's NotEqual stencil test uses it to skip those
// already-caustic-shaded surfaces (no double caustics). The resolved _CameraDepthTexture is bound
// separately as a sampled texture for the world reconstruction.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterCausticProjectionPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.AfterRenderingSkybox;

        const int CausticProjectionShaderPass = 0;

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterCausticProjection");

        internal WaterCausticProjectionPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
            // Force _CameraDepthTexture to be produced before this pass runs (it injects earlier than the
            // fog pass, which ran late enough to always find it ready). Needed for the world reconstruction.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        sealed class PassData { public Material material; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            using var builder = renderGraph.AddRasterRenderPass<PassData>("WaterCausticProjection", out PassData data, _sampler);

            data.material = _material;
            // ReadWrite loads the existing scene so the hardware One-One blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            // Bind the depth-stencil target (Read only: the shader tests the stencil bit, it never writes
            // depth). This is the buffer WaterReceiver / AnalyticPool wrote bit 3 into during the opaque pass.
            if (resources.activeDepthTexture.IsValid())
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
            // Resolved scene depth for the world reconstruction (SampleSceneDepth in the shader).
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // _CausticTex + _WaterTex (published per-frame by the primary body)
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, CausticProjectionShaderPass));
        }
    }
}
#endif
