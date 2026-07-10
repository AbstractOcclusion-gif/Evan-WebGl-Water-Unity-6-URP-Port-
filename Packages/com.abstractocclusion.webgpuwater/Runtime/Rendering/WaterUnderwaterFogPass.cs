// WebGpuWater - real underwater fog pass (RenderGraph).
// When the camera is submerged, fogs the whole camera colour by water-path length using two
// hardware-blend fullscreen passes (per-channel absorb, then inscatter). No scene-colour copy:
// both passes read the destination through the blender, which is why the colour attachment is
// bound ReadWrite (load the scene) rather than Write (which would discard it).
//
// Runs before post so bloom/tonemapping treat the fogged scene as the final image.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUnderwaterFogPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        const int AbsorbShaderPass = 0;
        const int InscatterShaderPass = 1;

        // The fog reconstructs the scene from the LIVE (post-transparent) depth, which includes the
        // ZWrite-On water surface, so the underwater waterline follows the real waves. URP's
        // _CameraDepthTexture is the opaque copy captured BEFORE transparents (no water) - reading it
        // flattened the boundary. Handed to the fog sub-passes as this global (project convention).
        static readonly int ID_SceneDepth = Shader.PropertyToID("_WaterFogSceneDepth");

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterUnderwaterFog");

        internal WaterUnderwaterFogPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData { public Material material; public int shaderPass; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            // Publish the live post-transparent depth (with the water surface) as _WaterFogSceneDepth first.
            RecordDepthHandoff(renderGraph, resources, cameraColor);
            // Order matters: absorb (scene *= transmittance) then inscatter (scene += fog).
            RecordFogPass(renderGraph, resources, cameraColor, AbsorbShaderPass, "WaterUnderwaterFog.Absorb");
            RecordFogPass(renderGraph, resources, cameraColor, InscatterShaderPass, "WaterUnderwaterFog.Inscatter");
        }

        void RecordFogPass(RenderGraph renderGraph, UniversalResourceData resources,
                           TextureHandle cameraColor, int shaderPass, string passName)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            data.shaderPass = shaderPass;
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // _WaterFogSceneDepth (from the handoff) + published fog globals
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, d.shaderPass));
        }

        // Hand the LIVE camera depth (post-transparent, includes the ZWrite water surface) to the fog
        // sub-passes as _WaterFogSceneDepth, following the god-ray pass's SetGlobalTextureAfterPass
        // convention. The ReadWrite colour keeps this a valid, un-culled raster pass; the no-op render
        // func leaves the scene untouched (load + store) and exists only so the handoff runs.
        // NOTE: if the boundary still reads flat, this is the spot to verify - the correct
        // "post-transparent depth" handle can differ by URP version (resources.activeDepthTexture here).
        void RecordDepthHandoff(RenderGraph renderGraph, UniversalResourceData resources, TextureHandle cameraColor)
        {
            TextureHandle liveDepth = resources.activeDepthTexture;
            if (!liveDepth.IsValid()) return;
            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                "WaterUnderwaterFog.DepthHandoff", out PassData data, _sampler);
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(liveDepth, AccessFlags.Read);
            builder.SetGlobalTextureAfterPass(liveDepth, ID_SceneDepth);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => { });
        }
    }
}
#endif
