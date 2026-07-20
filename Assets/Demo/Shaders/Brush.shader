Shader "Custom/Brush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BrushColor ("Brush Color", Color) = (0,0,0,1)
        _BrushPos ("Brush Position (xy=pos, z=radius)", Vector) = (0.5,0.5,0.01,0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _MainTex;
            float4 _BrushColor;
            float4 _BrushPos;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                float d = distance(i.uv, _BrushPos.xy);
                float t = 1.0 - smoothstep(0, _BrushPos.z, d);
                t *= _BrushColor.a;
                return lerp(col, _BrushColor, t);
            }
            ENDCG
        }
    }
}
