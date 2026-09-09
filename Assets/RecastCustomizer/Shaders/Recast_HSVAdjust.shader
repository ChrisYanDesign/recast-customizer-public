// Recast/HSVAdjust
//
// A texture pass, not a surface shader. It takes a base colour map and shifts its hue,
// saturation and brightness, and the result is written to an offscreen texture that the
// real material then uses as its base map.
//
// Doing it this way matters. A stock Lit material can only multiply its base colour, and a
// multiply can never rotate a hue or drain saturation -- it can only tint. Rewriting the Lit
// shader itself would put the project's carefully matched lighting at risk. Adjusting the
// texture before it reaches an untouched Lit material keeps the lighting exactly as it was
// and still gives true hue and saturation control.
Shader "Recast/HSVAdjust"
{
    Properties
    {
        _MainTex     ("Source", 2D) = "white" {}
        _Hue         ("Hue shift", Range(-0.5, 0.5)) = 0
        _Saturation  ("Saturation", Range(0, 2)) = 1
        _Brightness  ("Brightness", Range(0, 2)) = 1
        _NeutralCut  ("Neutral threshold", Range(0.02, 0.6)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Hue;
            float _Saturation;
            float _Brightness;
            float _NeutralCut;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 RgbToHsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HsvToRgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);
                float3 hsv = RgbToHsv(src.rgb);

                // ---- the normal path: rotate the hue the pixel already has ----
                float3 rot = hsv;
                rot.x = frac(rot.x + _Hue + 1.0);   // wrap, so the wheel is continuous
                rot.y = saturate(rot.y * _Saturation);
                rot.z = rot.z * _Brightness;
                float3 rotated = HsvToRgb(rot);

                // ---- the colourize path, for pixels that have no hue to rotate ----
                //
                // A near-grey pixel cannot be hue-shifted: there is no wheel to turn. The
                // black thumb variant is exactly this case, and rotating its hue did nothing
                // at all. So instead of turning a colour, give it one, keeping the map's own
                // luminance and every scratch and grain in it.
                //
                // Which path a pixel takes is decided per pixel by how neutral it already
                // is, so a variant that IS colourful still gets true rotation and only the
                // dead-grey areas are colourized. No mode switch, no second slider.
                //
                // Nothing happens until saturation is pushed above its resting value of 1,
                // which keeps the default behaviour of both sliders exactly as it was.
                float neutral = 1.0 - saturate(hsv.y / max(_NeutralCut, 0.001));
                float inject  = saturate(_Saturation - 1.0);
                float3 tinted = HsvToRgb(float3(frac(_Hue + 1.0), inject, hsv.z * _Brightness));

                float3 outCol = lerp(rotated, tinted, neutral * inject);
                return fixed4(outCol, src.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
