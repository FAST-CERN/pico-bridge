// Stereo SBS (side-by-side) unlit shader.
// Samples the left half of the texture for the left eye and the right half
// for the right eye, driven by unity_StereoEyeIndex so a single quad shared
// by both eyes renders a correct stereoscopic pair (televuer VideoMaterial
// equivalent). Works under Multi Pass and Single Pass Instanced rendering.
Shader "PicoBridge/StereoSbsQuad"
{
    Properties
    {
        _MainTex ("SBS Texture", 2D) = "black" {}
        [Toggle(_FLIP_Y)] _FlipY("Flip Y (WebRTC texture)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Lighting Off
        Cull Back
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _FLIP_Y
            // stereo instancing support (Single Pass Instanced)
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
            #ifdef _FLIP_Y
                uv.y = 1.0 - uv.y;
            #endif
                // Left eye -> left half, right eye -> right half of the SBS frame.
                float eye = unity_StereoEyeIndex;
                uv.x = uv.x * 0.5 + eye * 0.5;
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
