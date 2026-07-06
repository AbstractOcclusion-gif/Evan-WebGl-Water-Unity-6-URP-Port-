// WebGpuWater - large-body atmosphere pass (RenderGraph).
// Fullscreen ocean god-ray shafts: a half-res raymarch of the view ray through the main light's
// shadow map (in-scatter with a Henyey-Greenstein phase), then an additive composite over the
// camera colour. Runs before post so bloom/tonemapping treat the shafts as scene light.
//
// Ocean-only: the feature gates enqueue on an active ocean with god rays on, and the shader reads
// _LargeGodRayDensity (0 for bounded bodies) as a second guard. Pools stay untouched.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class LargeBodyAtmospherePass : ScriptableRenderPass
    {
        // Before post so the additive shafts feed bloom/tonemapping like real in-scattered light.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        const int RaymarchShaderPass = 0;
        const int CompositeShaderPass = 1;
        const int HalfResDivisor = 2; // shafts are low-frequency; half res halves the march cost

        // The raymarch pass hands its half-res target to the composite pass through this global,
        // via SetGlobalTextureAfterPass (the project's RenderGraph handoff convention).
        static readonly int ID_ShaftTexture = Shader.PropertyToID("_LargeGodRayTex");

        readonly Material _material;
        readonly ProfilingSampler _raymarchSampler = new ProfilingSampler("LargeBodyGodRays.Raymarch");
        readonly ProfilingSampler _compositeSampler = new ProfilingSampler("LargeBodyGodRays.Composite");

        internal LargeBodyAtmospherePass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData { public Material material; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            TextureHandle shaftTexture = CreateHalfResTarget(renderGraph, cameraColor);
            RecordRaymarch(renderGraph, resources, shaftTexture);
            RecordComposite(renderGraph, cameraColor);
        }

        TextureHandle CreateHalfResTarget(RenderGraph renderGraph, TextureHandle cameraColor)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(cameraColor);
            desc.name = "LargeBodyGodRaysHalfRes";
            desc.width = Mathf.Max(1, desc.width / HalfResDivisor);
            desc.height = Mathf.Max(1, desc.height / HalfResDivisor);
            desc.clearBuffer = true;         // start black so the additive composite adds only shafts
            desc.clearColor = Color.clear;
            return renderGraph.CreateTexture(desc);
        }

        void RecordRaymarch(RenderGraph renderGraph, UniversalResourceData resources, TextureHandle shaftTexture)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                _raymarchSampler.name, out PassData data, _raymarchSampler);

            data.material = _material;
            builder.SetRenderAttachment(shaftTexture, 0, AccessFlags.Write);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            if (resources.mainShadowsTexture.IsValid())
                builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true);                       // scene depth + shadow + shaft globals
            builder.SetGlobalTextureAfterPass(shaftTexture, ID_ShaftTexture); // hand to the composite pass
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, RaymarchShaderPass));
        }

        void RecordComposite(RenderGraph renderGraph, TextureHandle cameraColor)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                _compositeSampler.name, out PassData data, _compositeSampler);

            data.material = _material;
            // ReadWrite (not Write): the Read half forces the rendered scene to be LOADED before the
            // additive Blend One One, instead of discarded (Write alone left the screen black).
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseAllGlobalTextures(true);                             // resolve _LargeGodRayTex
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, CompositeShaderPass));
        }
    }
}
#endif
