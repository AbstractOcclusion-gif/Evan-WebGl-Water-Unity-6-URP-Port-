// WebGpuWater - screen-space additive caustic projection (URP RenderGraph fullscreen).
// Paints the projected caustic pattern onto ANY underwater surface by reading the depth buffer,
// reusing the EXACT SAME pool-space projection the water surfaces use (WaterReceiver / AnalyticPool),
// so a Standard-Lit prop, terrain, or a bare ocean floor with no receiver shows caustics perfectly
// registered with the water. Because it works off depth it is independent of each surface's own shader.
//
// Driven by WaterCausticProjectionFeature, gated on WaterVolume.CausticProjectionActive (primary body
// active + a valid caustic RT + the per-body Screen-Space Caustics opt-in). Above water / outside the
// footprint the fragment contributes nothing (masked to 0), so an armed pass over dry pixels is a no-op.
//
// Double-caustics avoidance (Approach A, non-destructive): WaterReceiver and AnalyticPool ALREADY add
// caustics in-shader, so this pass must NOT paint them again. Those two shaders write stencil bit 3
// (URP StencilUsage.UserMask is bits [0,3]; bits 4-6 are URP's own, bit 7 reserved) during the opaque
// ForwardLit pass; this pass runs a NotEqual stencil test on that bit and skips them. Existing surfaces
// are visually unchanged.
Shader "AbstractOcclusion/WebGpuWater/WaterCausticProjection"
{
    Properties
    {
        // Mirrors WaterReceiver's caustic controls. These are NOT published globals (they are per-material
        // on the receiver/pool), so the screen-space pass carries its own, driven from the render feature's
        // serialized fields. Defaults match WaterReceiver (strength 4, white). The depth-fade rate is the
        // body's published _CausticDepthFade global, so it stays consistent with the surfaces automatically.
        _CausticStrength ("Caustic Strength", Range(0,8)) = 4
        _CausticTint ("Caustic Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterCausticProjection"
            // Additive onto the opaque scene, composited BEFORE the transparent water surface draws over it.
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            // Skip pixels the receiver/pool already caustic-shaded (they wrote bit 3). NotEqual: draw only
            // where (Ref & ReadMask) != (buffer & ReadMask), i.e. bit 3 is CLEAR. Read-only test (no write).
            Stencil
            {
                Ref 8
                ReadMask 8
                WriteMask 0
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterVolume.hlsl" // WorldToPool / WorldDirToPool / PoolToWorld / FootprintMaskPool (+ volume frame)
            #include "WaterShared.hlsl" // ProjectCausticUV, OccluderLitFromGreen, IOR_*
            #include "WaterFog.hlsl"    // DepthFadeScalar + _CausticDepthFade (published global)

            // Caustic map + green occluder-shadow channel (published globals; UseAllGlobalTextures binds them).
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            // Sim state, for the wavy surface height that decides above/below water (same source the surfaces use).
            TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
            float4 _WaterTexel;           // (1/w, 1/h, w, h) of _WaterTex
            float3 _LightDir;             // global "toward the light", driven from the Unity sun
            float _CausticOccluderActive; // 1 when caustic.g is this body's valid refracted occluder-shadow channel

            CBUFFER_START(UnityPerMaterial)
                float _CausticStrength;
                float4 _CausticTint;
            CBUFFER_END

            // Manual bilinear height sample (COPY of WaterReceiver's local helper): WebGPU cannot
            // hardware-filter the float32 sim texture, so a filtered SAMPLE_TEXTURE2D silently point-samples
            // there and the underwater cut goes blocky in builds. SAMPLE_TEXTURE2D_LOD => no implicit
            // derivatives, so this is valid anywhere (including after any discard).
            float SampleWaterHeightBilinear(float2 uv)
            {
                float2 texel = _WaterTexel.xy;
                float2 st = uv * _WaterTexel.zw - 0.5;
                float2 f = frac(st);
                float2 baseUV = (floor(st) + 0.5) * texel;
                float c00 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV, 0).r;
                float c10 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(texel.x, 0.0), 0).r;
                float c01 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(0.0, texel.y), 0).r;
                float c11 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + texel, 0).r;
                return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
            }

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // 1. Reconstruct world position from the RESOLVED scene depth (same source/convention the fog
                //    pass uses: URP's ComputeWorldSpacePosition handles reversed-Z + the platform Y-flip).
                float rawDepth = SampleSceneDepth(IN.uv);
                float3 worldPos = ComputeWorldSpacePosition(IN.uv, rawDepth, UNITY_MATRIX_I_VP);

                // 2. Pool space. The caustic UV + its GRAD sample are computed in UNIFORM control flow (before
                //    any gate), because an implicit-derivative sample inside a per-fragment branch is undefined
                //    on WebGPU/WGSL - the same rule WaterReceiver and GetWallShadeSplit follow.
                float3 poolPos = WorldToPool(worldPos);
                float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                // Pool-space refracted ray: ProjectCausticUV's xz/y ratio is only valid in pool space, so a
                // WORLD direction mis-projects on non-uniform (deep) bodies. Uniform extents are byte-identical.
                float2 cuv = ProjectCausticUV(poolPos, WorldDirToPool(refractedLight));
                float4 causticSample = SAMPLE_TEXTURE2D_GRAD(_CausticTex, sampler_CausticTex, cuv, ddx(cuv), ddy(cuv));

                // 3. Gate: only submerged pixels INSIDE the footprint get caustics, and never the sky (no
                //    geometry at the far plane). Multiplied in (rather than discarded) so the whole pass stays
                //    in uniform control flow and the additive blend adds exactly 0 on masked pixels.
                float inside = FootprintMaskPool(poolPos);
                float2 wuv = poolPos.xz * 0.5 + 0.5;
                float simH = SampleWaterHeightBilinear(wuv);
                bool isSky = (rawDepth == UNITY_RAW_FAR_CLIP_VALUE);
                float underwaterMask = (!isSky && inside > 0.5 && poolPos.y < simH) ? 1.0 : 0.0;

                // 4. Same depth fade + refracted occluder shadow the surfaces apply, measured against the sampled
                //    surface Y (so the caustics fade with depth and vanish under an occluder, registered with them).
                //    When _CausticOccluderActive is 0 the green channel is 1 (floor) => OccluderLitFromGreen = 1,
                //    i.e. simply unshadowed - consistent, no special-casing.
                float surfaceY = PoolToWorld(float3(poolPos.x, simH, poolPos.z)).y;
                float causticFade = DepthFadeScalar(worldPos.y, surfaceY, _CausticDepthFade);
                float lit = OccluderLitFromGreen(poolPos.y, causticSample.g);

                float3 caustic = _CausticTint.rgb
                               * (causticSample.r * _CausticStrength * causticFade * lit * underwaterMask);
                return half4(caustic, 0.0); // additive (Blend One One); alpha unused
            }
            ENDHLSL
        }
    }
}
