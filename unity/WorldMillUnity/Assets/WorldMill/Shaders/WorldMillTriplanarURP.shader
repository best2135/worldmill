Shader "WorldMill/TriplanarTerrainURP"
{
    Properties
    {
        _WaterLevel ("Water Level", Float) = -6.0
        _BumpStrength ("Triplanar Bump Strength", Range(0.0, 1.5)) = 0.65
        _LodTint ("LOD Tint Mode", Float) = 0.0
        _ChunkLod ("Chunk LOD", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _WaterLevel;
            float _BumpStrength;
            float _LodTint;
            float _ChunkLod;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0; // x = biome (0..5), y = GPU-baked SDF AO
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 biomeAo    : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float vnoise2(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1.0, 0.0));
                float c = hash21(i + float2(0.0, 1.0));
                float d = hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float fbm2(float2 p)
            {
                float sum = 0.0;
                float amp = 0.5;
                float2x2 rot = float2x2(0.80, -0.60, 0.60, 0.80);
                for (int i = 0; i < 4; i++)
                {
                    sum += amp * vnoise2(p);
                    p = mul(rot, p) * 2.04 + float2(13.7, 7.3);
                    amp *= 0.5;
                }
                return sum;
            }

            float ridged2(float2 p)
            {
                float sum = 0.0;
                float amp = 0.55;
                float2x2 rot = float2x2(0.80, -0.60, 0.60, 0.80);
                for (int i = 0; i < 3; i++)
                {
                    float n = 1.0 - abs(vnoise2(p) * 2.0 - 1.0);
                    sum += amp * (n * n);
                    p = mul(rot, p) * 2.15 + float2(5.1, 11.9);
                    amp *= 0.48;
                }
                return sum;
            }

            // World-space triplanar fBm height sample
            float triplanarFbm(float3 wp, float3 w, float scale)
            {
                float sx = fbm2(wp.zy * scale);
                float sy = fbm2(wp.xz * scale);
                float sz = fbm2(wp.xy * scale);
                return sx * w.x + sy * w.y + sz * w.z;
            }

            float triplanarRidged(float3 wp, float3 w, float scale)
            {
                float sx = ridged2(wp.zy * scale);
                float sy = ridged2(wp.xz * scale);
                float sz = ridged2(wp.xy * scale);
                return sx * w.x + sy * w.y + sz * w.z;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normInputs.normalWS;
                OUT.biomeAo = IN.uv0;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 wp = IN.positionWS;
                float3 N0 = normalize(IN.normalWS);
                float3 triW = pow(abs(N0), 4.0);
                triW /= max(triW.x + triW.y + triW.z, 1e-5);

                float dist = length(_WorldSpaceCameraPos - wp);
                float detailFade = 1.0 / (1.0 + dist * 0.012);

                // Multi-scale triplanar procedural textures
                float macro = triplanarFbm(wp, triW, 0.09);
                float mid   = triplanarFbm(wp, triW, 0.42);
                float fine  = triplanarFbm(wp, triW, 1.85);
                float crack = triplanarRidged(wp, triW, 0.34);
                float strata = sin(wp.y * 1.15 + macro * 6.5) * 0.5 + 0.5;

                int biomeId = (int)round(IN.biomeAo.x);
                float ao = pow(saturate(IN.biomeAo.y), 1.35);

                float3 albedo = float3(0.4, 0.4, 0.4);
                float smoothness = 0.22;
                float metallic = 0.0;
                float3 emission = float3(0.0, 0.0, 0.0);
                float bumpScale = 1.0;

                if (biomeId == 0)
                {
                    // Cavern basalt: fractured volcanic stone + damp mineral sheen
                    float3 darkRock = float3(0.08, 0.09, 0.13);
                    float3 slateRock = float3(0.19, 0.21, 0.27);
                    albedo = lerp(darkRock, slateRock, mid * 0.7 + crack * 0.35);
                    albedo *= lerp(0.65, 1.08, strata);
                    smoothness = lerp(0.18, 0.58, smoothstep(0.55, 0.85, macro));
                    bumpScale = 1.25;
                }
                else if (biomeId == 1)
                {
                    // Warm sandstone / shoreline with wet darkened water band
                    float3 sandLight = float3(0.76, 0.62, 0.42);
                    float3 sandOchre = float3(0.56, 0.38, 0.24);
                    albedo = lerp(sandOchre, sandLight, strata * 0.6 + mid * 0.4);
                    albedo *= lerp(0.88, 1.08, fine);
                    smoothness = 0.20;
                    bumpScale = 0.85;
                }
                else if (biomeId == 2)
                {
                    // Lush highland meadow with natural rock slope transition
                    float3 grassRich = float3(0.14, 0.36, 0.11);
                    float3 grassSun  = float3(0.32, 0.48, 0.16);
                    float3 mossEarth = float3(0.26, 0.22, 0.14);
                    float3 grassCol = lerp(grassRich, grassSun, macro);
                    grassCol = lerp(mossEarth, grassCol, smoothstep(0.22, 0.55, mid));
                    grassCol *= lerp(0.84, 1.12, fine);

                    // Blend into granite rock on steeper edges
                    float3 cliffCol = lerp(float3(0.24, 0.24, 0.27), float3(0.44, 0.42, 0.40), strata * 0.6 + crack * 0.4);
                    float rockMask = smoothstep(0.78, 0.58, N0.y + (macro - 0.5) * 0.15);
                    albedo = lerp(grassCol, cliffCol, rockMask);
                    smoothness = lerp(0.14, 0.26, rockMask);
                    bumpScale = lerp(0.55, 1.15, rockMask);
                }
                else if (biomeId == 3)
                {
                    // Striated granite canyon cliff + moss dust on upward ledges
                    float3 crevice = float3(0.16, 0.16, 0.19);
                    float3 granite = float3(0.45, 0.43, 0.40);
                    float3 warmBand = float3(0.54, 0.42, 0.32);
                    albedo = lerp(crevice, granite, crack * 0.65 + mid * 0.35);
                    albedo = lerp(albedo, warmBand, strata * 0.35);
                    float ledgeMoss = smoothstep(0.50, 0.72, N0.y) * smoothstep(0.42, 0.72, macro);
                    albedo = lerp(albedo, float3(0.20, 0.34, 0.15), ledgeMoss * 0.55);
                    smoothness = lerp(0.15, 0.34, crack);
                    bumpScale = 1.35;
                }
                else if (biomeId == 4)
                {
                    // Alpine snow with wind-sculpted drifts + sparkle glints
                    float3 snowShadow = float3(0.72, 0.82, 0.94);
                    float3 snowBright = float3(0.95, 0.97, 1.00);
                    albedo = lerp(snowShadow, snowBright, mid * 0.55 + 0.45);
                    float sparkle = smoothstep(0.84, 0.96, fine) * detailFade;
                    smoothness = lerp(0.32, 0.88, sparkle);
                    bumpScale = 0.40;
                }
                else
                {
                    // Bioluminescent crystal cavern veins (HDR emissive triggers URP Bloom!)
                    float3 crystalCore = float3(0.12, 0.88, 0.98);
                    float3 crystalViolet = float3(0.42, 0.24, 0.96);
                    albedo = lerp(crystalViolet, crystalCore, crack);
                    smoothness = 0.90;
                    metallic = 0.15;
                    emission = lerp(crystalViolet, crystalCore, crack) * (1.8 + 0.6 * sin(_Time.y * 2.2 + wp.x * 0.3));
                    bumpScale = 1.1;
                }

                // Wet shoreline darkening & mirror-like sheen right near water level
                float wetBand = 1.0 - smoothstep(_WaterLevel - 0.6, _WaterLevel + 2.2, wp.y);
                if (wp.y > _WaterLevel - 6.0)
                {
                    albedo *= lerp(1.0, 0.52, wetBand);
                    smoothness = lerp(smoothness, 0.82, wetBand);
                }

                // Optional LOD debug tint
                if (_LodTint > 0.5)
                {
                    float3 lodCol = _ChunkLod > 0.5 ? float3(0.96, 0.56, 0.16) : float3(0.18, 0.85, 0.55);
                    albedo = lerp(albedo, lodCol, 0.38);
                }

                // Analytical triplanar bump normal perturbation (central differences in world space)
                float eps = 0.18;
                float h0 = triplanarFbm(wp, triW, 0.52) * 0.6 + triplanarRidged(wp, triW, 0.36) * 0.4;
                float hx = triplanarFbm(wp + float3(eps, 0, 0), triW, 0.52) * 0.6 + triplanarRidged(wp + float3(eps, 0, 0), triW, 0.36) * 0.4;
                float hy = triplanarFbm(wp + float3(0, eps, 0), triW, 0.52) * 0.6 + triplanarRidged(wp + float3(0, eps, 0), triW, 0.36) * 0.4;
                float hz = triplanarFbm(wp + float3(0, 0, eps), triW, 0.52) * 0.6 + triplanarRidged(wp + float3(0, 0, eps), triW, 0.36) * 0.4;
                float3 grad = float3(hx - h0, hy - h0, hz - h0) / eps;
                // Project gradient onto tangent plane so perturbation never flattens silhouette
                grad -= N0 * dot(grad, N0);
                float3 N = normalize(N0 - grad * (_BumpStrength * bumpScale * detailFade));

                // Full Unity URP PBR Lighting with Cascaded Shadow Maps + GI + Fog
                InputData inputData = (InputData)0;
                inputData.positionWS = wp;
                inputData.normalWS = N;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(wp);
                inputData.shadowCoord = TransformWorldToShadowCoord(wp);
                inputData.fogCoord = IN.fogFactor;
                inputData.bakedGI = SampleSH(N);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = metallic;
                surfaceData.specular = half3(0.5, 0.5, 0.5);
                surfaceData.smoothness = smoothness;
                surfaceData.occlusion = ao;
                surfaceData.emission = emission;
                surfaceData.alpha = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormVert
            #pragma fragment DepthNormFrag

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings DepthNormVert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DepthNormFrag(Varyings IN) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(IN.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
}
