#ifndef AKUN_LIGHTING_INCLUDED
#define AKUN_LIGHTING_INCLUDED

// Web 版のライト: 半球光 (空 #ffffff / 地面 #3a3020, 1.3)、太陽 (2.2)、逆光 (#aabbff, 0.7)
// 値は ClayApp.SetupLights で設定する (色はリニア)
float4 _AK_SunDir;     // xyz: 光が来る向き (正規化済み)
float4 _AK_SunColor;   // rgb * 強さ
float4 _AK_RimDir;
float4 _AK_RimColor;
float4 _AK_SkyColor;   // rgb * 強さ
float4 _AK_GroundColor;

// three.js の MeshStandardMaterial の拡散 (albedo / π × 放射照度) に、弱い鏡面反射を足す
half3 AKunShade(half3 albedo, float3 n, float3 v, half spec)
{
    const float AK_INV_PI = 0.31830988;
    float3 hemi = lerp(_AK_GroundColor.rgb, _AK_SkyColor.rgb, n.y * 0.5 + 0.5);
    float ns = saturate(dot(n, _AK_SunDir.xyz));
    float nr = saturate(dot(n, _AK_RimDir.xyz));
    float3 irr = hemi + _AK_SunColor.rgb * ns + _AK_RimColor.rgb * nr;
    float3 c = albedo * irr * AK_INV_PI;
    float3 hv = normalize(_AK_SunDir.xyz + v);
    c += _AK_SunColor.rgb * pow(saturate(dot(n, hv)), 24) * spec * ns;
    return (half3)c;
}

#endif
