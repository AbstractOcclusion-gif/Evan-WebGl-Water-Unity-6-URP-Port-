// WebGL Water - Shuriken splash particle rendering (crown + droplets)
//
// Replaces Sprites/Default on the splash emitters so event splashes sit in the same
// light as the water's foam: wrapped sun diffuse over an ambient floor (driven by the
// _LightDir/_SunColor globals the primary WaterVolume publishes), erosion-based
// dissolve driven by the particle's own colorOverLifetime alpha, and a soft fade
// against the opaque scene. Queued after the water surface so ordering is stable.
//
// Works with standard Shuriken vertex data (position/color/uv), including the crown's
// Texture Sheet Animation - no custom vertex streams required.
Shader "AbstractOcclusion/WebGpuWater/SplashParticles"
{
    Properties
    {
        _MainTex ("Sprite (or flipbook sheet)", 2D) = "white" {}
        _Tint ("Tint", Color) = (0.95, 0.98, 1.0, 1.0)
        _ParticleOpacity ("Opacity", Range(0, 1)) = 1.0
        _SoftFadeDistance ("Soft Fade vs Scene Depth (world)", Range(0.001, 0.5)) = 0.05
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // Foam lighting, matched to WaterSurface/FoamParticles so every foam-like
            // element in the scene shades consistently.
            #define FOAM_LIGHT_WRAP  0.4
            #define FOAM_AMBIENT     0.35
            // As the particle's lifetime alpha decays the sprite ERODES (thin regions
            // drop out first) instead of uniformly ghosting - same trick everywhere.
            #define EROSION_SOFTNESS 0.35

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Tint;
            float _ParticleOpacity;
            float _SoftFadeDistance;
            float3 _LightDir; // globals published by the primary WaterVolume
            float3 _SunColor;
            sampler2D _CameraDepthTexture;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;     // Shuriken per-particle color (incl. colorOverLifetime)
                float2 uv     : TEXCOORD0; // Texture Sheet Animation writes the flipbook frame here
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                fixed4 color     : COLOR;
                float2 uv        : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float2 fade      : TEXCOORD2; // x = lit sun factor, y = fragment eye depth
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screenPos = ComputeScreenPos(o.pos);
                // Splash sheets/droplets have no meaningful normal; light them as
                // upward-facing foam so brightness tracks the sun's height and color.
                float wrapped = saturate(_LightDir.y * (1.0 - FOAM_LIGHT_WRAP) + FOAM_LIGHT_WRAP);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.fade = float2(wrapped, -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 sprite = tex2D(_MainTex, i.uv);
                float envelope = i.color.a;

                // erosion dissolve driven by the particle's own lifetime alpha
                float alpha = saturate((sprite.a - (1.0 - envelope)) / EROSION_SOFTNESS);
                alpha *= envelope * _ParticleOpacity;

                // soft fade against the opaque scene (pool walls, floating objects)
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                alpha *= saturate((sceneEye - i.fade.y) / _SoftFadeDistance);

                // The flipbook is premultiplied (rgb = intensity * tint), so sprite.rgb
                // re-applies the intensity already present in sprite.a and darkens the
                // sprite wherever erosion boosts coverage above it -> the black halo.
                // The crown's true (un-premultiplied) color is flat _Tint, so light that.
                float3 lit = _Tint.rgb * i.color.rgb * (FOAM_AMBIENT + _SunColor * i.fade.x);
                return fixed4(lit, alpha);
            }
            ENDCG
        }
    }
}
