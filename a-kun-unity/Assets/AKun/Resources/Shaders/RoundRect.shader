// 角丸の四角 (ボタン、パネルの板、スライダー、色見本、カーソルの輪)。
// Quad (一辺 1, UV 0〜1) を _Size (m) に拡大して使う。縁取り付き
Shader "AKun/RoundRect"
{
    Properties
    {
        _Fill ("Fill", Color) = (0.17, 0.19, 0.23, 1)
        _Border ("Border", Color) = (0.29, 0.32, 0.38, 1)
        _Size ("Size (m)", Vector) = (0.1, 0.04, 0, 0)
        _Radius ("Radius (m)", Float) = 0.008
        _BorderWidth ("Border Width (m)", Float) = 0.0008
        _Alpha ("Alpha", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-20" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Fill;
                half4 _Border;
                float4 _Size;
                float _Radius;
                float _BorderWidth;
                half _Alpha;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 p : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.p = (i.uv - 0.5) * _Size.xy; // 中心からの位置 (m)
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 hs = _Size.xy * 0.5;
                float r = min(_Radius, min(hs.x, hs.y));
                float2 q = abs(i.p) - hs + r;
                float sd = length(max(q, 0)) + min(max(q.x, q.y), 0) - r; // 角丸の四角までの距離 (内側が負)
                float aa = max(fwidth(sd), 1e-5);
                float inside = saturate(0.5 - sd / aa);
                float border = saturate(0.5 - (-sd - _BorderWidth) / aa); // 縁から _BorderWidth の内側まで
                half4 c = lerp(_Fill, _Border, _BorderWidth > 0 ? border : 0);
                c.a *= inside * _Alpha;
                return c;
            }
            ENDHLSL
        }
    }
}
