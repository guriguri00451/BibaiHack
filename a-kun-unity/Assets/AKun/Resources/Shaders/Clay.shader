// 粘土 (頂点カラー、リニア) と台座。
// Web 版 (three.js の MeshStandardMaterial, roughness 0.78) に近い見た目になるように、
// 半球光 + 平行光 2 つを自前で計算する (光の向きと色は ClayApp が Shader.SetGlobal* で渡す)
Shader "AKun/Clay"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _UseVertexColor ("Use Vertex Color", Float) = 1
        _Spec ("Specular", Float) = 0.12
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "AKunLighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _UseVertexColor;
                half _Spec;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.color = i.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 albedo = lerp(half3(1, 1, 1), i.color.rgb, _UseVertexColor) * _BaseColor.rgb;
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                return half4(AKunShade(albedo, n, v, _Spec), 1);
            }
            ENDHLSL
        }
    }
}
