// Opaque double-sided vertex-color shader for the weight/sharpness overlays.
// Sprites/Default (transparent, ZWrite Off) let BACK faces render through FRONT faces,
// interleaving into a checkerboard — an overlay must write depth and stay opaque.
Shader "Hidden/SoftBodyOverlay"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" }
        Cull Off
        ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float4 col : COLOR; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.col = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return i.col; }
            ENDCG
        }
    }
}
