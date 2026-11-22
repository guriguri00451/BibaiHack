using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace Tumamu.EditorTools
{
    // MR テンプレートは Android 向けが Meta Quest 前提で構成されているため、
    // XREAL Aura（Android XR）向けに切り替える。YAML 直編集だと同名の Meta 用 / Android XR 用
    // feature の区別がつかないので、型の完全名で判定して API 経由で設定する。
    public static class TumamuProjectSetup
    {
        const string AndroidXrNamespacePrefix = "UnityEngine.XR.OpenXR.Features.Android";
        const string ApplicationId = "jp.bibaihack.tumamu";

        [MenuItem("Tumamu/Dump OpenXR Android Features")]
        public static void DumpAndroidFeatures()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            foreach (var feature in settings.GetFeatures())
            {
                Debug.Log($"[TumamuSetup] {(feature.enabled ? "ON " : "off")} {feature.GetType().FullName}");
            }
        }

        [MenuItem("Tumamu/Configure For Android XR")]
        public static void ConfigureForAndroidXr()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            foreach (var feature in settings.GetFeatures())
            {
                var fullName = feature.GetType().FullName ?? string.Empty;
                // Android XR の feature を全部 ON にすると Face Tracking などの不要な権限まで要求されるので、使うものだけ列挙する
                feature.enabled = EnabledFeatureTypes.Contains(fullName);
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            DumpAndroidFeatures();
        }

        const string Phase0ApkPath = "Builds/tumamu-phase0.apk";

        // ランタイムで Shader.Find した URP シェーダーはビルドで strip されることがあるので、
        // Resources 配下のマテリアルアセットとして参照を残しておく
        const string PreviewMaterialPath = "Assets/Tumamu/Phase0/Resources/Phase0Preview.mat";

        static void EnsurePreviewMaterial()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(PreviewMaterialPath) != null)
                return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PreviewMaterialPath));
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(material, PreviewMaterialPath);
            AssetDatabase.SaveAssets();
        }

        public static void BuildPhase0Apk()
        {
            EnsurePreviewMaterial();
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Phase0ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[TumamuSetup] build result={report.summary.result} errors={report.summary.totalErrors} path={Phase0ApkPath}");
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }

        static readonly string[] EnabledFeatureTypes =
        {
            AndroidXrNamespacePrefix + ".AndroidXRSupportFeature",
            AndroidXrNamespacePrefix + ".ARSessionFeature",
            AndroidXrNamespacePrefix + ".ARPlaneFeature",
            AndroidXrNamespacePrefix + ".ARCameraFeature", // パススルー制御用。カメラ画像の取得はできない（spec 参照）
            AndroidXrNamespacePrefix + ".ARRaycastFeature",
            AndroidXrNamespacePrefix + ".ARAnchorFeature",
            AndroidXrNamespacePrefix + ".AROcclusionFeature",
            AndroidXrNamespacePrefix + ".DisplayUtilitiesFeature",
            AndroidXrNamespacePrefix + ".OpenXRLifeCycleFeature",
            "UnityEngine.XR.Hands.OpenXR.HandTracking",
            "UnityEngine.XR.OpenXR.Features.Interactions.HandInteractionProfile",
            "UnityEngine.XR.OpenXR.Features.CompositionLayers.OpenXRCompositionLayersFeature",
        };
    }
}
