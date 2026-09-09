// Recast Customizer — Blurred Skybox
//
// Drop-in replacement for Unity's built-in Skybox/Cubemap, with one extra slider: Blur.
//
// It works by sampling the cubemap at a higher mip level. Mips are the progressively
// smaller, pre-filtered copies Unity generates for every texture — reading a smaller one
// and stretching it back over the sky gives a soft, correctly-filtered blur for free,
// with no extra render passes.
//
// Only the *visible background* is affected. Ambient light and reflections are computed
// from the cubemap by the engine, not by this shader, so lighting on the subject stays
// crisp no matter how far the background is blurred.
//
// Requires the cubemap to have mipmaps (Generate Mip Maps is on by default).

Shader "Recast/Blurred Skybox"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        [NoScaleOffset] _Tex ("Cubemap (HDR)", Cube) = "grey" {}
        _Blur ("Blur", Range(0, 7)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            samplerCUBE _Tex;
            half4  _Tex_HDR;
            half4  _Tint;
            half   _Exposure;
            float  _Rotation;
            half   _Blur;

            float3 RotateAroundYInDegrees(float3 vertex, float degrees)
            {
                float alpha = degrees * UNITY_PI / 180.0;
                float sina, cosa;
                sincos(alpha, sina, cosa);
                float2x2 m = float2x2(cosa, -sina, sina, cosa);
                return float3(mul(m, vertex.xz), vertex.y).xzy;
            }

            struct appdata_t
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float3 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 rotated = RotateAroundYInDegrees(v.vertex.xyz, _Rotation);
                o.vertex   = UnityObjectToClipPos(rotated);
                o.texcoord = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // w component of the sample coordinate selects the mip level = the blur
                half4 tex = texCUBElod(_Tex, float4(i.texcoord, _Blur));
                half3 c   = DecodeHDR(tex, _Tex_HDR);
                c = c * _Tint.rgb * unity_ColorSpaceDouble.rgb;
                c *= _Exposure;
                return half4(c, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
