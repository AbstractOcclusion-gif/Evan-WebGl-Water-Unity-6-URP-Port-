// WebGL Water - DEPRECATED.
// The draggable sphere was removed when the fake analytic ball was decommissioned
// (replaced by real two-way object interaction). This file is now an inert stub
// kept only so any lingering material reference still compiles; it can be safely
// deleted from the Unity Project window (along with Generated/Sphere.mat and
// Generated/UnitSphere.asset).
Shader "WebGLWater/WaterSphere"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target { return fixed4(0.5, 0.5, 0.5, 1.0); }
            ENDCG
        }
    }
}
