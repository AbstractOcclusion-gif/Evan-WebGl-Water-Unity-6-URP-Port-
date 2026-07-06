// WebGpuWater - large-body ocean god rays (URP RenderGraph fullscreen).
// Scalable volumetric light shafts for an unbounded ocean: a fullscreen raymarch of the view
// ray through the main light's shadow map, in-scattering with a Henyey-Greenstein phase so the
// beams brighten toward the sun. Driven by the LargeBodyAtmosphere render feature (ocean-only,
// gated on _LargeGodRayDensity > 0); the pool's in-mesh GodRays volume stays suppressed there.
//
// Two passes: 0 = raymarch into a half-res target (reads scene depth + main-light shadows via
// URP globals); 1 = additive composite of that target (bound as the global _LargeGodRayTex) back
// over the camera colour. Self-contained fullscreen triangle (no Blit.hlsl), so no dependence on
// the Blitter's scale-bias global state.
//
// Requires the URP asset's Depth Texture ON and main-light shadows enabled. All tuning comes from
// globals published by WaterUniformPublisher, so there are no per-material knobs.
Shader "WebGpuWater/LargeBodyGodRays"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // ---- Pass 0: raymarch the shafts into the half-res target --------------------
        Pass
        {
            Name "LargeBodyGodRaysRaymarch"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma target 4.0
            // Sample the main light's shadow MAP (cascades), matching the pool GodRays pass. The
            // screen-space variant is intentionally omitted: it is keyed to opaque-surface depth
            // and would be wrong for arbitrary volumetric samples. Without a shadowmap the pass
            // degrades gracefully to unshadowed shafts.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float3 _LightDir;   // global, normalized direction toward the sun
            float3 _SunColor;   // global, sun colour * intensity

            float4 _LargeGodRayColor;
            float  _LargeGodRayDensity;
            float  _LargeGodRaySteps;
            float  _LargeGodRayAnisotropy;
            float  _LargeGodRayExtinction;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            // Interleaved gradient noise (Jimenez 2014): a stable per-pixel [0,1) dither that turns
            // step-count banding into high-frequency noise the eye averages out across the shafts.
            float InterleavedGradientNoise(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            // Henyey-Greenstein phase: forward-scattering lobe. g -> 1 sharpens the glow toward the
            // sun. Normalised so _LargeGodRayDensity stays the single intensity control.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;
                return (1.0 - g2) / (4.0 * PI * pow(max(denom, 1e-4), 1.5));
            }

            half4 FragRaymarch(Varyings input) : SV_Target
            {
                // Off / gate: density 0 means the feature should not enqueue this pass, but guard anyway.
                if (_LargeGodRayDensity <= 0.0) return half4(0.0, 0.0, 0.0, 1.0);

                float rawDepth = SampleSceneDepth(input.uv);
                float3 sceneWorld = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);

                float3 camWorld = _WorldSpaceCameraPos;
                float3 toScene = sceneWorld - camWorld;
                float sceneDist = length(toScene);
                float3 rayDir = toScene / max(sceneDist, 1e-5);
                // Cap the march at the scene hit or the far plane (sky pixels), so shafts have a finite
                // length; the distance extinction below thins them within that span.
                float marchDist = min(sceneDist, _ProjectionParams.z);

                int steps = max(1, (int)_LargeGodRaySteps);
                float dt = marchDist / steps;
                float jitter = InterleavedGradientNoise(input.positionCS.xy);

                // Phase and light direction are constant along a straight view ray -> hoist out of the loop.
                float phase = HenyeyGreenstein(dot(rayDir, _LightDir), _LargeGodRayAnisotropy);
                float transStep = exp(-_LargeGodRayExtinction * dt);

                float accum = 0.0;
                float trans = 1.0;
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + jitter) * dt;
                    float3 p = camWorld + rayDir * t;
                    float4 shadowCoord = TransformWorldToShadowCoord(p);
                    float shadow = MainLightRealtimeShadow(shadowCoord);
                    accum += shadow * trans;
                    trans *= transStep;
                }
                // Average over the samples so brightness is independent of march distance: a pixel
                // looking at the horizon marches the whole far plane while a near pixel marches a few
                // metres. The old Riemann sum (* dt) made the far sky blow out to white unless density
                // was ~0.0001; averaged, density reads ~O(1).
                accum /= steps;

                float3 col = _LargeGodRayColor.rgb * _SunColor * (accum * _LargeGodRayDensity * phase);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 1: additive composite of the half-res shafts over the camera colour --
        Pass
        {
            Name "LargeBodyGodRaysComposite"
            Blend One One   // additive glow

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.0

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_LargeGodRayTex);
            SAMPLER(sampler_LargeGodRayTex);

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                // _LargeGodRayTex is the half-res shaft target, bound as a global by the raymarch pass.
                return SAMPLE_TEXTURE2D(_LargeGodRayTex, sampler_LargeGodRayTex, input.uv);
            }
            ENDHLSL
        }
    }
}
