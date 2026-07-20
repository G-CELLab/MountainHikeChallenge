Shader "Custom/OrganicSkybox"
{
    // ── What this does ──────────────────────────────────────────────────────
    // A skybox (background) shader for the "inside the body" look — no mesh,
    // no collider, fills the whole background automatically. Three things
    // combine to make it read as organic tissue instead of a flat color:
    //
    //   1. A vertical gradient between a top color and bottom color.
    //   2. Drifting "blobby" noise, built from a few layered sine waves.
    //   3. A slow ambient pulse.
    //
    // VR STEREO NOTE: includes UNITY_VERTEX_INPUT_INSTANCE_ID /
    // UNITY_VERTEX_OUTPUT_STEREO / UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX —
    // required for this to render correctly in both eyes under Single Pass
    // Instanced rendering (the default for Quest/OpenXR). Without these, one
    // eye doesn't get set up correctly, which is exactly the "left eye shows
    // it, right eye doesn't" bug this shader used to have.
    //
    // Reused across every mini-game scene: duplicate the MATERIAL (not the
    // shader) once per scene and change the two color swatches. Assign via
    // Window → Rendering → Lighting → Environment → Skybox Material.

    Properties
    {
        _TopColor       ("Top Color", Color)    = (0.55, 0.15, 0.2, 1)
        _BottomColor    ("Bottom Color", Color) = (0.85, 0.35, 0.35, 1)
        _NoiseScale     ("Noise Scale", Range(0.5, 8))    = 2.5
        _NoiseSpeed     ("Noise Drift Speed", Range(0, 2)) = 0.15
        _NoiseIntensity ("Noise Intensity", Range(0, 1))   = 0.35
        _PulseSpeed     ("Pulse Speed", Range(0, 5))       = 1
        _PulseIntensity ("Pulse Intensity", Range(0, 1))   = 0.2
    }

    SubShader
    {
        Tags { "RenderType"="Background" "Queue"="Background" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID   // per-eye instance ID comes in here
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 dirOS       : TEXCOORD0;  // direction from center, in object space
                UNITY_VERTEX_OUTPUT_STEREO       // carries which-eye info to the fragment shader
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _TopColor;
                float4 _BottomColor;
                float  _NoiseScale;
                float  _NoiseSpeed;
                float  _NoiseIntensity;
                float  _PulseSpeed;
                float  _PulseIntensity;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);              // reads which eye this vertex is for
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT); // passes that along to the fragment shader

                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = vpi.positionCS;
                OUT.dirOS = normalize(IN.positionOS.xyz);
                return OUT;
            }

            float BlobbyNoise(float3 p, float time)
            {
                float n = 0;
                n += sin(p.x * 1.0 + p.y * 1.7 + p.z * 1.3 + time);
                n += sin(p.x * 2.1 - p.y * 0.9 + p.z * 2.7 + time * 0.7) * 0.5;
                n += sin(p.y * 3.3 + p.z * 1.9 - p.x * 1.1 + time * 1.3) * 0.25;
                n /= 1.75;
                return n * 0.5 + 0.5;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN); // confirms correct eye for this pixel

                float3 dir = normalize(IN.dirOS);

                float t = saturate(dir.y * 0.5 + 0.5);
                float3 gradientColor = lerp(_BottomColor.rgb, _TopColor.rgb, t);

                float noise = BlobbyNoise(dir * _NoiseScale, _Time.y * _NoiseSpeed);
                float noiseFactor = lerp(1.0 - _NoiseIntensity * 0.5, 1.0 + _NoiseIntensity * 0.5, noise);

                float pulse = (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5) * _PulseIntensity;

                float3 finalCol = gradientColor * noiseFactor + pulse * gradientColor;
                return half4(finalCol, 1);
            }
            ENDHLSL
        }
    }
}
