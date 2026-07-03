// Renders each fluid particle as a soft radial "blob" into an offscreen field
// RenderTexture. Additive blending accumulates overlapping particles so the
// downstream threshold pass can extract a single liquid surface (metaball).
//
// Vertex COLOR carries the channel mask: color 0 = (1,0,0,0), color 1 = (0,1,0,0),
// etc. — so up to 4 fluid colors are packed into the RGBA channels of one RT.
Shader "Funtom/FluidParticle"
{
    Properties
    {
        _Softness ("Falloff Softness", Range(0.25, 6)) = 2.0
        _Strength ("Field Strength", Range(0.1, 4)) = 1.0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One          // additive: fields sum
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            float _Softness;
            float _Strength;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 d = i.uv * 2.0 - 1.0;   // -1..1 across the quad
                float r = length(d);
                float f = saturate(1.0 - r);
                f = pow(f, _Softness) * _Strength;
                return i.color * f;            // color = channel mask
            }
            ENDCG
        }
    }
}
