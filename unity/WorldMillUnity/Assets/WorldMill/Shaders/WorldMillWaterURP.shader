Shader "WorldMill/WaterURP"
{
    Properties
    {
        _DeepColor ("Deep Color", Color) = (0.015, 0.12, 0.19, 1.0)
        _ShallowColor ("Shallow / Crest Color", Color) = (0.04, 0.34, 0.42, 1.0)
        _WaveScale ("Wave Scale", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _DeepColor;
                float4 _ShallowColor;
                float _WaveScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float  fogFactor  : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 wp = IN.positionWS;
                float3 viewVec = _WorldSpaceCameraPos - wp;
                float dist = length(viewVec);
                float3 V = viewVec / max(dist, 1e-4);

                float t = _Time.y;
                float rf = _WaveScale / (1.0 + dist * 0.008);
                float2 p = wp.xz;

                float dhdx = 0.0, dhdz = 0.0, wh = 0.0;
                const float2 dirs[5] = {
                    float2( 0.82,  0.57),
                    float2(-0.55,  0.83),
                    float2( 0.36, -0.93),
                    float2(-0.91, -0.41),
                    float2( 0.68, -0.73)
                };
                const float freqs[5]  = { 0.22, 0.37, 0.85, 1.75, 3.20 };
                const float amps[5]   = { 0.24, 0.15, 0.065, 0.032, 0.014 };
                const float speeds[5] = { 1.15, 1.48, 2.15, 2.85, -3.40 };

                [unroll]
                for (int i = 0; i < 5; i++)
                {
                    float a = amps[i] * rf * (i == 4 ? rf : 1.0);
                    float ph = dot(dirs[i], p) * freqs[i] + t * speeds[i];
                    wh += a * sin(ph);
                    float d = a * freqs[i] * cos(ph);
                    dhdx += dirs[i].x * d;
                    dhdz += dirs[i].y * d;
                }

                float nSign = _WorldSpaceCameraPos.y >= wp.y ? 1.0 : -1.0;
                float3 N = normalize(float3(-dhdx, nSign, -dhdz));

                Light mainLight = GetMainLight();
                float3 L = normalize(mainLight.direction);
                float3 sunColor = mainLight.color;

                float NdotV = max(dot(N, V), 0.0);
                float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

                float3 skyRefl = SampleSH(reflect(-V, N)) * 1.15;
                float crest = saturate(wh * 1.4 + 0.38 + max(dot(N, L), 0.0) * 0.35);
                float3 body = lerp(_DeepColor.rgb, _ShallowColor.rgb, crest);

                float3 col = lerp(body, skyRefl, saturate(fresnel * 1.05));

                float3 H = normalize(L + V);
                float NdotH = max(dot(N, H), 0.0);
                float spec = pow(NdotH, 96.0) * 0.55 + pow(NdotH, 512.0) * 3.8;
                col += sunColor * spec;

                col = MixFog(col, IN.fogFactor);
                float alpha = lerp(0.76, 0.96, fresnel);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
