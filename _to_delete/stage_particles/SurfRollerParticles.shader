// WebGpuWater - surf roller foam particle rendering.
//
// Draws the pool written by WaterSurfRoller.compute as procedural camera-facing quads:
// the vertex shader pulls a RollerParticle from a StructuredBuffer by SV_VertexID
// (6 vertices per particle, dead slots collapse to degenerate triangles) - the same
// everywhere-on-WebGPU expansion path as FoamParticles.shader, with two deliberate
// differences:
//   - NO velocity stretch: roller foam is phase-locked to the wave front, so a fixed
//     square with a slow per-seed yaw spin reads as churning foam, never as streaks;
//   - NO scene-depth soft fade: these particles sit ON/ABOVE the breaking wave, so the
//     depth texture fetch would buy nothing.
// The compute writes ABSOLUTE world positions (height included), so this shader needs no
// surface glue - it is a pure billboard pass. Lighting/erosion/envelope come from
// WaterFoamCommon.hlsl so the roller matches every other foam element.
Shader "AbstractOcclusion/WebGpuWater/SurfRollerParticles"
{
    Properties
    {
        _ParticleTex ("Particle Sprite Atlas", 2D) = "white" {}
        _Tint ("Tint", Color) = (0.95, 0.98, 1.0, 1.0)
        _ParticleOpacity ("Opacity", Range(0, 1)) = 0.9
        // Flipbook grid + FPS are NOT material sliders: they are driven from the
        // WaterSurfRollerParticles component (one place to tweak) via its
        // MaterialPropertyBlock. Declared as uniforms below.
    }
    SubShader
    {
        // Transparent+11: one above the ambient foam quads (Transparent+10), so the roller
        // reads as the freshest, topmost foam on the breaking front.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+11" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "WaterFoamCommon.hlsl" // shared foam lighting + erosion + life envelope
            #include "WaterParticleCommon.hlsl" // billboard corner expansion + flipbook atlas cell

            // Slow idle spin of the billboard (rad/s); direction flips per seed so a group
            // of particles never rotates in lockstep.
            #define ROLLER_SPIN_RATE 0.6

            static const float KIND_SPRAY = 1.0;
            // Corner expansion + flipbook cell come from WaterParticleCommon.hlsl (shared
            // with FoamParticles.shader).

            // MUST match RollerParticle in WaterSurfRoller.compute (80 bytes).
            struct RollerParticle
            {
                float3 worldPos;
                float  age;
                float3 velocity;
                float  life;
                float  frontIndex;
                float  crestDist;
                float  dAcross;
                float  birthOverCap;
                float  size;
                float  seed;
                float  kind;
                float  strength;
                float  brokenTimer;
                float3 pad;
            };
            StructuredBuffer<RollerParticle> _Particles;

            sampler2D _ParticleTex;
            float4 _Tint;
            float _ParticleOpacity;
            float2 _ParticleFlipbookGrid; // atlas (cols, rows); (1,1) = plain texture, no flipbook
            float _ParticleFlipbookFps;   // 0 = static per-seed variant; >0 animates over age
            // Sun direction + colour, fed by the body's property block (WriteBodyProps) the
            // same way the other foam passes receive them.
            float3 _LightDir;
            float3 _SunColor;

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float3 litColor : TEXCOORD1; // per-vertex foam lighting (soft blobs: no per-pixel need)
                float  envelope : TEXCOORD2; // life envelope x whitewash-matched strength
            };

            // Degenerate output for dead slots: w = 0 collapses the triangle.
            v2f Dead()
            {
                v2f o;
                o.pos = float4(0, 0, 0, 0);
                o.uv = 0; o.litColor = 0; o.envelope = 0;
                return o;
            }

            v2f vert(uint vid : SV_VertexID)
            {
                RollerParticle particle = _Particles[vid / 6];
                if (particle.life <= 0.0 || particle.age >= particle.life
                    || particle.strength <= 0.0)
                    return Dead();

                float2 corner = ParticleQuadCorner(vid);

                // ---- camera-facing billboard, fixed square, slow per-seed yaw spin ----
                // (No velocity stretch on purpose: the roller's motion IS the wave's, and
                // stretching would read as wash streaking off the front.)
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float spinDir = (particle.seed < 0.5) ? -1.0 : 1.0;
                float spin = particle.seed * PARTICLE_TWO_PI
                           + particle.age * ROLLER_SPIN_RATE * spinDir;
                float cosSpin = cos(spin);
                float sinSpin = sin(spin);
                float3 axisX = camRight * cosSpin + camUp * sinSpin;
                float3 axisY = camUp * cosSpin - camRight * sinSpin;

                float3 worldVertex = particle.worldPos
                                   + axisX * (corner.x * particle.size)
                                   + axisY * (corner.y * particle.size);

                // ---- life envelope x the compute's whitewash-matched strength ----
                float envelope = FoamParticleEnvelope(particle.age, particle.life)
                               * particle.strength;

                // ---- sprite cell from the atlas: a fixed per-seed variant, or an animated
                // flipbook when _ParticleFlipbookFps > 0 (shared math, WaterParticleCommon.hlsl) ----
                float2 uv = ParticleFlipbookUv(corner, _ParticleFlipbookGrid.xy,
                                               particle.seed, particle.age, _ParticleFlipbookFps);

                // ---- lighting, matched to the surface foam. A billboard has no meaningful
                // normal, so treat it as up-facing foam (the splash-sheet convention from
                // WaterFoamCommon.hlsl): N.L = the sun's height. ----
                float wrapped = FoamWrappedDiffuseNdotL(saturate(_LightDir.y));

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldVertex, 1.0));
                o.uv = uv;
                o.litColor = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                o.envelope = envelope;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 sprite = tex2D(_ParticleTex, i.uv);
                float envelope = i.envelope;

                // Erosion fade: the sprite's thin regions dissolve first as the envelope
                // decays - the roller foam dies ragged, exactly like the ambient foam.
                float alpha = FoamErosionAlpha(sprite.a, envelope);
                alpha *= envelope * _ParticleOpacity;

                // No scene-depth soft fade: these quads ride on/above the wave crest, never
                // half-submerged against pool walls (see the header note).
                return fixed4(i.litColor * sprite.rgb, alpha);
            }
            ENDCG
        }
    }
}
