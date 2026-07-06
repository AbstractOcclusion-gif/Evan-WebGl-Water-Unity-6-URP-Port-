// WebGpuWater - large-body atmosphere pass (RenderGraph).
// The fullscreen pass that will composite ocean-scale distance haze and god-ray light
// shafts over the rendered scene. It runs after transparents so it can read the final
// camera colour + depth (the ocean surface is already drawn).
//
// Increment 1 scope: this is a GATED, NO-OP entry point only. It records no RenderGraph
// work, so the ocean and every bounded body render byte-for-byte unchanged while the
// feature wiring and the ocean-only gate are verified in the editor. The composite work
// (horizon haze, then shafts) lands in later increments.
#if WEBGPUWATER_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class LargeBodyAtmospherePass : ScriptableRenderPass
    {
        // After transparents: the ocean surface is drawn, and camera colour + depth are the
        // final scene the haze/shafts composite onto. Held as a field so the feature owns the
        // choice in one place when we later expose it as a knob.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.AfterRenderingTransparents;

        internal LargeBodyAtmospherePass()
        {
            renderPassEvent = InjectionPoint;
        }

        // Intentionally records nothing in Increment 1. Adding no graph passes contributes no
        // rendering, which is exactly the acceptance test: the pass is enqueued and wired, yet
        // the frame is unchanged. Real compositing is added in the next increment.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
        }
    }
}
#endif
