// WebGpuWater - volumetric water CHUNK shell (the submerged body below a chunk's surface).
// Owned by a WaterVolume configured as a chunk (WaterVolume.Chunk.cs) and drawn as a body renderer,
// so it is fed THAT body's per-body block (frame + waves + fog) - the shell's waterline is the SAME
// SurfaceHeightAtXZ the disc surface uses, so the two meet with no seam, and it needs no external
// primary (the body publishes its own state). A pool-space box mesh (BuildChunkShellBox, [-1,1])
// placed by the volume frame; the primitive (box / inscribed sphere) is resolved analytically in
// pool space. It FILLS the primitive below the surface with the lit in-scatter colour integrated
// over the water column, tinting + refracting the real scene behind by the water's optical depth.
//
// Cull Front (draw the box's BACK faces) so every covered pixel gets ONE fragment and the column
// integrates entry->exit for a camera OUTSIDE or INSIDE the body. ZTest Always (Crest volume-pass
// pattern): opaque geometry INSIDE the chunk sits nearer than the back face and would z-reject the
// fragment, punching an unfogged hole in the water in front of it - the sceneDist clamp below caps
// the column against the real scene instead. Stays OUT of _CameraDepthTexture (no depth passes) so
// it composites over the real scene; the analytic silhouette discards fragments off the shape or
// above the surface.
//
// TIER GATE (_RealRefraction, 0 on Low where the URP opaque texture is released): High/Med take the
// FULL refracted-backdrop path; Low takes a cheap premultiplied inscatter veil.
Shader "AbstractOcclusion/WebGpuWater/WaterChunkWall"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha // premultiplied: rgb is the finished composite, a = coverage

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterChunkPrimitive.hlsl" // ChunkIntersect / ChunkSurfaceNormalPool (+ WaterShared: IOR_*)
            #include "WaterCommon.hlsl"         // SampleWaterBicubic (interactive ripple) + _WaterTex + _LightDir
            #include "WaterFog.hlsl"            // WaterInscatterColor + DownwellingAttenuation + _ScatterAmbient + fog globals
            #include "WaterVolume.hlsl"         // PoolToWorld / WorldToPool / PoolNormalToWorld (this body's frame)
            #include "WaterWaterline.hlsl"      // WaveHeight (wind-wave layer) via its wave includes

            // Published globals (WaterUniformPublisher). _LightDir comes from WaterCommon; _RealRefraction
            // is the tier flag (0 on Low).
            float3 _SunColor;
            float  _RealRefraction;
            sampler2D _CameraOpaqueTexture;

            // Per-chunk state (set on the body block by WaterVolume.Chunk.cs). The chunk's density
            // boost is NOT a shell uniform: SetChunkSurfaceProps bakes it into the body block's
            // _WaterFogDensity once, so the disc column, this shell and membership objects all read
            // the same (boosted) water.
            float _ChunkShape;        // CHUNK_SHAPE_* selector (box / sphere)
            float _ChunkRefraction;   // 0 = flat window; higher bends the backdrop (a lens)
            float _ChunkReflectivity; // fresnel sheen strength (sky + sun reflected toward grazing)
            // 1 = the camera (lowest near-plane corner, hysteresis) is in THIS chunk's water.
            // Decided per FRAME on the CPU (ComputeChunkCameraUnder) - a per-pixel test off the ray
            // interval flickered across the waterline band. Drives the veil-vs-backdrop split below.
            float _ChunkCameraUnderwater;

            #define CHUNK_SUN_WRAP 0.5
            #define CHUNK_COLUMN_EPSILON 1e-4
            #define CHUNK_UV_MIN 0.001
            #define CHUNK_UV_MAX 0.999
            #define CHUNK_CLIP_W_EPS 1e-5
            #define CHUNK_FRESNEL_F0 0.02
            #define CHUNK_SUN_SPEC_POWER 200.0
            #define CHUNK_SUN_SPEC_GAIN 1.0
            // Bisection steps for the ray<->displaced-surface crossing, bounded to the primitive
            // span (<= the chunk diameter), so precision is span / 2^steps regardless of ray angle.
            #define CHUNK_WATERLINE_BISECT_STEPS 6

            float2 ChunkClipToScreenUV(float4 clipPos)
            {
                float2 uv = clipPos.xy / max(clipPos.w, CHUNK_CLIP_W_EPS) * 0.5 + 0.5;
                if (_ProjectionParams.x < 0.0) uv.y = 1.0 - uv.y;
                return uv;
            }

            // Interactive ripple height at a point, window-aware - mirrors WaterSurface.shader's
            // SampleRipple: whole-body samples the pool UV, a windowed body (ocean) samples the
            // camera-following sim window by WORLD position and fades to flat at its border. Without
            // the window path an ocean chunk read the wrong (static) UV and the fog stopped moving.
            float ChunkRippleHeight(float2 poolXZ, float3 worldPos)
            {
                if (_SimWindowed < 0.5)
                    return SampleWaterBicubic(poolXZ * 0.5 + 0.5).r;

                float2 uv = WorldToSim(worldPos).xz * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
                float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x;
                float2 d = min(uv, 1.0 - uv);
                float fade = saturate(min(d.x, d.y) / max(band, 1e-5));
                return SampleWaterBicubic(uv).r * fade;
            }

            // The EXACT height the disc surface renders here. SurfaceHeightAtXZ is the shared source of
            // truth for the wind (ocean world-metre / pond pool) + swell/large-wave layers - the ocean's
            // dominant motion - and the interactive ripple is added on top, lifted through the frame
            // exactly as the surface vert lifts it. So the shell waterline tracks the surface for BOTH a
            // bounded ocean (wind + swell) and a pond (ripple), with no lag.
            float ChunkSurfaceHeightWorld(float2 worldXZ, float3 worldPos)
            {
                float baseline = SurfaceHeightAtXZ(worldXZ);
                float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
                float2 poolXZ = poolAtRest.xz;
                float ripple = ChunkRippleHeight(poolXZ, worldPos);
                float rippleLift = PoolToWorld(float3(poolXZ.x, ripple, poolXZ.y)).y
                                 - PoolToWorld(float3(poolXZ.x, 0.0, poolXZ.y)).y;
                return baseline + rippleLift;
            }

            float3 ChunkSurfaceReflection(float3 surfaceNormal, float3 viewDirWS)
            {
                float fresnel = CHUNK_FRESNEL_F0 + (1.0 - CHUNK_FRESNEL_F0)
                              * pow(1.0 - saturate(dot(surfaceNormal, viewDirWS)), 5.0);
                float3 reflDir = reflect(-viewDirWS, surfaceNormal);
                float sunGlint = pow(saturate(dot(reflDir, _LightDir)), CHUNK_SUN_SPEC_POWER);
                float3 reflectColor = _ScatterAmbient.rgb + _SunColor * (sunGlint * CHUNK_SUN_SPEC_GAIN);
                return reflectColor * (fresnel * _ChunkReflectivity);
            }

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            // The box mesh is authored in POOL space [-1,1]; the volume frame places it in the world,
            // exactly like the analytic pool renderer (so a rotated / non-uniform chunk is the frame's).
            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = PoolToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 rayDir = normalize(IN.positionWS - _WorldSpaceCameraPos);
                float3 viewDirWS = -rayDir;

                // Primitive interval along the view ray, in world metres (pool space is an affine image
                // of world, so the pool-space t of a normalised world ray is the world t).
                float3 poolOrigin = WorldToPool(_WorldSpaceCameraPos);
                float3 poolDir    = WorldDirToPool(rayDir);
                float2 t = ChunkIntersect(_ChunkShape, poolOrigin, poolDir);

                // Real scene behind the fragment caps the column at any geometry inside/behind the body.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float3 sceneWorld = ComputeWorldSpacePosition(screenUV, SampleSceneDepth(screenUV),
                                                              UNITY_MATRIX_I_VP);
                float sceneDist = max(dot(sceneWorld - _WorldSpaceCameraPos, rayDir), 0.0);

                float entryT = max(t.x, 0.0);
                float exitT  = min(t.y, sceneDist);

                // Waterline = THIS body's displaced surface (the SAME function the disc surface
                // uses, via the shared per-body block) -> the shell and the disc meet with no seam.
                // Solved by BISECTION on the bounded primitive span (the underwater fog shader's
                // pattern): classify the two endpoints by their signed gap to the surface, then
                // bisect the sign change. Angle-robust by construction - the former fixed-point
                // solve divided by rayDir.y, which DIVERGED for grazing rays (the crossing xz leapt
                // metres between iterations) and confettied the whole waterline band.
                float sA = entryT;
                float sB = exitT;
                float3 pA = _WorldSpaceCameraPos + rayDir * sA;
                float3 pB = _WorldSpaceCameraPos + rayDir * sB;
                float gapA = pA.y - ChunkSurfaceHeightWorld(pA.xz, pA);
                float gapB = pB.y - ChunkSurfaceHeightWorld(pB.xz, pB);
                bool enteredThroughTop = gapA >= 0.0 && gapB < 0.0;
                float wavyTopY = pA.y - gapA; // surface height over the entry (downwelling reference)
                if (gapA >= 0.0 && gapB >= 0.0)
                {
                    exitT = entryT; // whole span in air -> zero column, clipped below
                }
                else if (gapA != gapB && (gapA >= 0.0 || gapB >= 0.0))
                {
                    // Exactly one waterline crossing inside the span: bisect it.
                    bool nearInAir = gapA >= 0.0;
                    [unroll]
                    for (int bisect = 0; bisect < CHUNK_WATERLINE_BISECT_STEPS; bisect++)
                    {
                        float sM = (sA + sB) * 0.5;
                        float3 pM = _WorldSpaceCameraPos + rayDir * sM;
                        float gapM = pM.y - ChunkSurfaceHeightWorld(pM.xz, pM);
                        if ((gapM >= 0.0) == nearInAir) sA = sM; else sB = sM;
                    }
                    float sCross = (sA + sB) * 0.5;
                    float3 pCross = _WorldSpaceCameraPos + rayDir * sCross;
                    wavyTopY = ChunkSurfaceHeightWorld(pCross.xz, pCross);
                    if (nearInAir) entryT = sCross; // air -> water: entered through the waterline
                    else           exitT  = sCross; // water -> air: capped at the waterline
                }
                // else: whole span submerged -> no waterline cap.

                float column = max(exitT - entryT, 0.0);
                clip(column - CHUNK_COLUMN_EPSILON); // no water here (off the shape / above the surface)

                // Ownership split vs the disc surface (deterministic: the shell material renders
                // AFTER the discs via its render queue - WaterVolume.Chunk.cs): a ray that entered
                // through the WATERLINE from above is the disc's pixel - it already rendered the
                // full fogged column (chunk fog clamp), and the disc's sphere clip overshoots the
                // rim slightly (CHUNK_SPHERE_CLIP_MARGIN) so this shared boundary stays covered.
                // The shell must not paint it twice - and must NOT replace it with the opaque
                // backdrop, which never contains transparents (that erased the disc). Discard.
                clip(enteredThroughTop ? -1.0 : 1.0);

                // Entry surface normal: the analytic shell normal (pool -> world via the frame's
                // inverse-transpose); top entries are discarded above, so no UP branch remains.
                float3 poolEntry = poolOrigin + poolDir * entryT;
                float3 surfaceN = PoolNormalToWorld(ChunkSurfaceNormalPool(_ChunkShape, poolEntry));
                if (dot(surfaceN, viewDirWS) < 0.0) surfaceN = -surfaceN;

                float sunWrap = saturate((dot(surfaceN, _LightDir) + CHUNK_SUN_WRAP) / (1.0 + CHUNK_SUN_WRAP));
                float3 inscatter = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunWrap, 0.0);
                float3 transmittance = exp(-_WaterExtinction.rgb * (_WaterFogDensity * column));
                float3 reflection = ChunkSurfaceReflection(surfaceN, viewDirWS);

                float deepestY = min(min(_WorldSpaceCameraPos.y + rayDir.y * entryT,
                                         _WorldSpaceCameraPos.y + rayDir.y * exitT), wavyTopY);
                float3 depthDarken = DownwellingAttenuation(deepestY, wavyTopY);

                // VEIL path: premultiplied in-scatter over the framebuffer. Taken on the CHEAP tier
                // (no opaque-texture copy) AND whenever the camera is IN the water (per-frame CPU
                // state, hysteresis - see _ChunkCameraUnderwater): there the entry is the eye (no
                // interface, no lens), and the surfaces already drawn behind (the disc underside,
                // the scene) must stay visible through the fog rather than being replaced by the
                // opaque backdrop. No fresnel sheen from inside the water.
                bool cameraInWater = _ChunkCameraUnderwater > 0.5;
                if (_RealRefraction < 0.5 || cameraInWater)
                {
                    float3 opacity = 1.0 - transmittance;
                    float coverage = max(opacity.r, max(opacity.g, opacity.b));
                    float3 sheen = cameraInWater ? float3(0.0, 0.0, 0.0) : reflection;
                    float3 veil = (inscatter * opacity + sheen) * depthDarken;
                    return half4(veil, coverage);
                }

                // FULL tier: refract the backdrop sample by the view ray bending at the surface.
                float2 refractUV = screenUV;
                if (_ChunkRefraction > 0.0)
                {
                    float3 entryWS = _WorldSpaceCameraPos + rayDir * entryT;
                    float3 refrDir = refract(rayDir, surfaceN, IOR_AIR / IOR_WATER);
                    float2 uvStraight = ChunkClipToScreenUV(TransformWorldToHClip(entryWS + rayDir  * column));
                    float2 uvBent     = ChunkClipToScreenUV(TransformWorldToHClip(entryWS + refrDir * column));
                    refractUV = clamp(screenUV + (uvBent - uvStraight) * _ChunkRefraction,
                                      CHUNK_UV_MIN, CHUNK_UV_MAX);
                }

                float3 sceneColor = tex2Dlod(_CameraOpaqueTexture, float4(refractUV, 0.0, 0.0)).rgb;
                float3 color = sceneColor * transmittance + inscatter * (1.0 - transmittance);
                color += reflection;
                color *= depthDarken;
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // NO DepthOnly / DepthNormals passes ON PURPOSE: the shell must stay out of
        // _CameraDepthTexture so it reads the REAL scene behind it.
    }
}
