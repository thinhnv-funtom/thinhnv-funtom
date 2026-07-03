// Full-screen composite: reads the accumulated field RenderTexture and turns it
// into a thick, jelly-looking liquid surface with a bright rim.
//
//  field > _T1            -> solid body color
//  _T2 < field <= _T1     -> bright rim (edge highlight)
//  field <= _T2           -> transparent
//
// Each RGBA channel is an independent color group; the dominant channel wins so
// colors read as distinct blobs instead of muddy blends.
//
// Designed to be dropped on a UI RawImage (its RenderTexture -> _MainTex) drawn
// full-screen on an overlay canvas, so it composites on top of the game view.
Shader "Funtom/FluidMetaball"
{
    Properties
    {
        _MainTex ("Field RT", 2D) = "black" {}
        _T1 ("Body Threshold", Range(0,4)) = 0.85
        _T2 ("Edge Threshold", Range(0,4)) = 0.35
        _AA ("Edge Antialias", Range(0.001,0.6)) = 0.08
        _RimBoost ("Rim Brightness", Range(0,1)) = 0.55
        _Color0 ("Color 0", Color) = (0.20,0.55,1.00,1)
        _Color1 ("Color 1", Color) = (1.00,0.35,0.45,1)
        _Color2 ("Color 2", Color) = (0.35,0.85,0.45,1)
        _Color3 ("Color 3", Color) = (1.00,0.80,0.25,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
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

            sampler2D _MainTex;
            float _T1, _T2, _AA, _RimBoost;
            float4 _Color0, _Color1, _Color2, _Color3;

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
                float4 field = tex2D(_MainTex, i.uv);

                // Pick the dominant color channel.
                int best = 0;
                float bestF = field.r;
                if (field.g > bestF) { bestF = field.g; best = 1; }
                if (field.b > bestF) { bestF = field.b; best = 2; }
                if (field.a > bestF) { bestF = field.a; best = 3; }

                // Outer edge: soft alpha ramp starting at the edge threshold.
                float alpha = smoothstep(_T2 - _AA, _T2 + _AA, bestF);
                if (alpha <= 0.001) discard;

                float3 base =
                    best == 0 ? _Color0.rgb :
                    best == 1 ? _Color1.rgb :
                    best == 2 ? _Color2.rgb : _Color3.rgb;

                // body = 1 at the core, 0 near the edge -> rim highlight near the edge.
                float body = smoothstep(_T2, _T1, bestF);
                float3 rim = base + (1.0 - base) * _RimBoost;   // brighter, toward white
                float3 col = lerp(rim, base, body);

                return fixed4(col, alpha) * i.color;
            }
            ENDCG
        }
    }
}
