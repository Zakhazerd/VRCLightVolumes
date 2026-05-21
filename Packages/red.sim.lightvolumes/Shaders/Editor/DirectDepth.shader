Shader "Hidden/DirectDepthToColor"
{
    SubShader
    {
        // This tag ensures we only render solid objects, ignoring transparents
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata_base v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                // In the fragment shader, the Z component of SV_POSITION 
                // contains the exact hardware depth value [0, 1].
                return float4(i.pos.z, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}