Shader "Hidden/OSFR/LinearHdrBoxDownsample"
{
    Properties
    {
        _MainTex("Source", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Linear HDR Box Downsample"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment Fragment

            #include "UnityCG.cginc"

            Texture2D<float4> _MainTex;
            SamplerState sampler_MainTex;
            float4 _MainTex_TexelSize;
            int _OSFRSupersampleFactor;

            float4 Fragment(v2f_img input) : SV_Target
            {
                int factor = clamp(_OSFRSupersampleFactor, 1, 16);
                float2 sourceTexel = abs(_MainTex_TexelSize.xy);
                float2 blockCenter = 0.5 * factor;
                float4 sum = 0.0;

                [loop]
                for (int y = 0; y < factor; ++y)
                {
                    [loop]
                    for (int x = 0; x < factor; ++x)
                    {
                        float2 sampleOffset = (float2(x + 0.5, y + 0.5) - blockCenter) * sourceTexel;
                        sum += _MainTex.SampleLevel(sampler_MainTex, input.uv + sampleOffset, 0.0);
                    }
                }

                return sum / (factor * factor);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
