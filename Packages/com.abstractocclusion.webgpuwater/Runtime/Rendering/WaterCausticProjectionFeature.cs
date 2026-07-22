// WebGpuWater - screen-space caustic projection render feature (URP, RenderGraph).
// Paints the projected caustic pattern onto ANY underwater surface (terrain, Standard Lit props, a bare
// ocean floor with no WaterReceiver) by reading the depth buffer and reusing the water's own pool-space
// projection. Add this feature once to the renderer used by the water camera and assign the
// WaterCausticProjection shader; it self-gates on WaterVolume.CausticProjectionActive, so it only enqueues
// when the primary body has a caustic RT and its Screen-Space Caustics opt-in is on.
//
// WIRING / CAVEATS:
//  * Must be ADDED to the URP Renderer asset(s) the water camera uses, and the shader assigned - exactly
//    like WaterUnderwaterFogFeature (which had to be re-added to Mobile_RPAsset / Mobile_Renderer for
//    builds; do the same here if you target those).
//  * Double-caustics is avoided by stencil (Approach A): WaterReceiver / AnalyticPool write stencil bit 3
//    and this pass skips those pixels, so they are visually unchanged. If your project uses screen-space
//    decals or a Render Objects feature that also writes URP user stencil bit 3 (0x08) on submerged
//    geometry, those pixels would be skipped too - re-home the bit in both places if so.
//  * v1 covers the PRIMARY body's caustics (its _CausticTex + volume frame globals). Secondary bodies
//    (WaterMembership) would need per-body caustic RTs bound - a known v1 limit.
//  * This adds the caustic PATTERN only. It does not fix the object SHADOW on foreign shaders (the
//    separate un-refracted-URP-shadow limitation); use WaterReceiver on submerged props for that.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the Universal
// Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterCausticProjectionFeature : ScriptableRendererFeature
    {
        // Defaults mirror WaterReceiver's caustic controls so foreign surfaces read at the same strength.
        const float DefaultCausticStrength = 4f;

        [Tooltip("The AbstractOcclusion/WebGpuWater/WaterCausticProjection shader. Assign the shader asset of that name.")]
        [SerializeField] Shader causticProjectionShader;

        [Tooltip("Brightness of the projected caustics on foreign surfaces. Matches WaterReceiver's Caustic Strength (default 4).")]
        [Range(0f, 8f)]
        [SerializeField] float causticStrength = DefaultCausticStrength;

        [Tooltip("Colour tint of the projected caustics, applied like WaterReceiver's Caustic Tint.")]
        [SerializeField] Color causticTint = Color.white;

        WaterCausticProjectionPass _pass;
        Material _material;

        static readonly int ID_CausticStrength = Shader.PropertyToID("_CausticStrength");
        static readonly int ID_CausticTint = Shader.PropertyToID("_CausticTint");

        public override void Create()
        {
            if (causticProjectionShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(causticProjectionShader);
            ApplyMaterialParameters();
            _pass = new WaterCausticProjectionPass(_material);
        }

        // Re-applied on every enqueue so inspector edits to strength/tint take effect live (Create already
        // seeds them; this keeps them current without forcing a full feature rebuild).
        void ApplyMaterialParameters()
        {
            if (_material == null) return;
            _material.SetFloat(ID_CausticStrength, causticStrength);
            _material.SetColor(ID_CausticTint, causticTint);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;                          // shader unassigned / not created
            if (!WaterVolume.CausticProjectionActive) return;   // opt-in off / no caustic RT / not submerged view
            ApplyMaterialParameters();
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
