// Recast/Inspect
//
// Channel isolation for asset review, in the spirit of Marmoset Toolbag's view modes.
//
// Each mode draws one input on its own, unlit and unshaded. That is the point: lighting is
// exactly what you want removed when the question is "is this normal map correct" or "is
// this albedo too dark". A lit view answers a different question, and a shaded surface hides
// the flaws you are hunting for.
//
// The modes are numbered rather than compiled as separate shaders because there are only a
// handful and switching needs to be instant.
Shader "Recast/Inspect"
{
    Properties
    {
        _BaseMap      ("Base map", 2D) = "white" {}
        _BumpMap      ("Normal map", 2D) = "bump" {}
        _SpecGlossMap ("Specular map", 2D) = "white" {}
        _Smoothness   ("Smoothness", Range(0,1)) = 0.5
        _Mode         ("Mode", Float) = 0
        _CheckerScale ("Checker density", Float) = 24
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Inspect"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);      SAMPLER(sampler_BumpMap);
            TEXTURE2D(_SpecGlossMap); SAMPLER(sampler_SpecGlossMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float  _Smoothness;
                float  _Mode;
                float  _CheckerScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                int mode = (int)round(_Mode);

                if (mode == 1)   // ALBEDO -- the base map with nothing done to it
                {
                    return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                }
                if (mode == 2)   // NORMAL -- the tangent-space map as authored
                {
                    // Decoded, not read raw. Unity stores normal maps in a compressed layout
                    // that puts X and Y in the alpha and green channels, so sampling the RGB
                    // directly shows pink rather than the lilac a normal map should look
                    // like -- and worse, it would be lying about the data.
                    half4 packed = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
                    half3 n = UnpackNormal(packed);
                    return half4(n * 0.5 + 0.5, 1);
                }
                if (mode == 3)   // SMOOTHNESS -- grey, so a gradient is readable as a value
                {
                    half s = SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, input.uv).a;
                    s = lerp(_Smoothness, s * _Smoothness, 0.999);
                    return half4(s, s, s, 1);
                }
                if (mode == 4)   // UV -- a checker, so stretching and seams become obvious
                {
                    float2 g = floor(input.uv * _CheckerScale);
                    float c = fmod(g.x + g.y, 2.0);
                    half3 a = half3(0.83, 0.83, 0.86);
                    half3 b = half3(0.20, 0.42, 0.62);
                    half3 col = lerp(a, b, c);
                    // a faint shade off the geometry normal, so form is still readable
                    half shade = saturate(dot(normalize(input.normalWS), half3(0.3, 0.8, 0.5)) * 0.35 + 0.75);
                    return half4(col * shade, 1);
                }
                if (mode == 5)   // WORLD NORMAL -- the surface direction after normal mapping
                {
                    half3 n = normalize(input.normalWS) * 0.5 + 0.5;
                    return half4(n, 1);
                }

                // 0, unused: the lit view is the real material, not this shader
                return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
