// WebGpuWater - real underwater fog (URP RenderGraph fullscreen).
// Fogs only the part of each camera->scene ray that is actually IN the water, so it reads as a
// volume and a waterline falls out for free (a ray that never enters the water gets no fog):
//   * Ocean (unbounded): the below-surface half-space -> the fullscreen screen effect.
//   * Pond  (bounded):   the ray clipped to the pool box (pool space [-1,1] xz, [-1,0] y) via
//                        IntersectCube -> a finite fog volume you can circle around.
// Per-channel Beer-Lambert absorption + downwelling depth darkening, reusing the body's fog and
// depth globals. Two hardware-blend passes so the scene colour never has to be copied:
//   0 Absorb:    scene *= pathTransmittance * depthAttenuation   (Blend Zero SrcColor)
//   1 Inscatter: scene += fog * (1 - pathTransmittance) * depthAttenuation   (Blend One One)
// Driven by WaterUnderwaterFogFeature (gated on WaterVolume.UnderwaterFogActive: ocean = submerged
// only, pond = whenever Water Fog is on). U2: camera-origin waterline (KWS half-line is later polish).
Shader "WebGpuWater/WaterUnderwaterFog"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "WaterFog.hlsl"    // _WaterFogColor/_WaterExtinction/_WaterFogDensity, WaterPathLength, DownwellingAttenuation
        #include "WaterVolume.hlsl" // PoolToWorld / WorldToPool (+ the body's volume frame globals)
        #include "WaterShared.hlsl" // IntersectCube

        float _UnderwaterSurfaceY;
        float _UnderwaterUnbounded; // 1 = ocean half-space, 0 = clip to this body's box (pond)

        struct Attributes { uint vertexID : SV_VertexID; };
        struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes IN)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
            o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
            return o;
        }

        float3 SceneWorldPos(float2 uv)
        {
            float rawDepth = SampleSceneDepth(uv);
            return ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
        }

        // World-space length of the in-water part of the camera->scene ray, and the deepest submerged
        // point's world Y (for downwelling). pathLen 0 = this pixel's ray never enters the water.
        void UnderwaterSegment(float3 sceneWorld, out float pathLen, out float deepestY)
        {
            float3 cam = _WorldSpaceCameraPos;

            if (_UnderwaterUnbounded > 0.5)
            {
                // Ocean: the whole below-surface span of the camera->scene segment.
                pathLen = WaterPathLength(sceneWorld, cam, _UnderwaterSurfaceY);
                deepestY = min(cam.y, sceneWorld.y);
                return;
            }

            // Pond: clip the ray to the pool water box in pool space ([-1,1] xz, [-1,0] y). Working in
            // pool space lets one IntersectCube handle the surface top AND the walls/floor at once.
            float3 originPool = WorldToPool(cam);
            float3 scenePool = WorldToPool(sceneWorld);
            float3 rayPool = scenePool - originPool;
            float sceneT = length(rayPool);
            rayPool /= max(sceneT, 1e-5);

            float2 hit = IntersectCube(originPool, rayPool, float3(-1.0, -1.0, -1.0), float3(1.0, 0.0, 1.0));
            float tEnter = max(hit.x, 0.0);
            float tExit = min(hit.y, sceneT); // never fog past the scene surface
            if (tExit <= tEnter)
            {
                pathLen = 0.0;
                deepestY = _UnderwaterSurfaceY;
                return;
            }

            // Convert the entry/exit back to world for a correct length (pool axes are scaled by extent).
            float3 enterWorld = PoolToWorld(originPool + rayPool * tEnter);
            float3 exitWorld = PoolToWorld(originPool + rayPool * tExit);
            pathLen = length(exitWorld - enterWorld);
            deepestY = min(enterWorld.y, exitWorld.y);
        }

        // Per-channel path transmittance for this pixel; also returns the depth-darkening term.
        float3 UnderwaterFog(float2 uv, out float3 depthAttenuation)
        {
            float3 sceneWorld = SceneWorldPos(uv);
            float pathLen;
            float deepestY;
            UnderwaterSegment(sceneWorld, pathLen, deepestY);
            depthAttenuation = DownwellingAttenuation(deepestY, _UnderwaterSurfaceY);
            return exp(-_WaterExtinction.rgb * (_WaterFogDensity * pathLen));
        }
        ENDHLSL

        // ---- Pass 0: absorption + depth darkening (dst *= pathTrans * depthAtten) ----
        Pass
        {
            Name "WaterUnderwaterFogAbsorb"
            Blend Zero SrcColor

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAbsorb
            #pragma target 4.0

            half4 FragAbsorb(Varyings input) : SV_Target
            {
                float3 depthAttenuation;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation);
                return half4(pathTransmittance * depthAttenuation, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 1: inscattered fog colour, also dimmed by depth (dst += fog * (1-pathTrans) * depthAtten) ----
        Pass
        {
            Name "WaterUnderwaterFogInscatter"
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragInscatter
            #pragma target 4.0

            half4 FragInscatter(Varyings input) : SV_Target
            {
                float3 depthAttenuation;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation);
                float3 inscatter = _WaterFogColor.rgb * (1.0 - pathTransmittance);
                return half4(inscatter * depthAttenuation, 1.0);
            }
            ENDHLSL
        }
    }
}
