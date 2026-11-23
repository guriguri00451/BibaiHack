using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Android;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.Hands.OpenXR;

namespace AKun.EditorTools
{
    // プロジェクトの設定とビルドをコマンドラインから行う (エディタを開かなくてもよい)。
    //   Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.ProjectSetup.Setup
    //   Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.ProjectSetup.BuildApk
    // メニュー: AKun > Setup Project / Build APK
    public static class ProjectSetup
    {
        const string ScenePath = "Assets/AKun/Scenes/Main.unity";
        const string ApkPath = "Builds/a-kun-clay.apk";

        [MenuItem("AKun/Setup Project")]
        public static void Setup()
        {
            SetupPlayer();
            SetupUrp();
            SetupXR();
            SetupScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[setup] done");
        }

        [MenuItem("AKun/Build APK")]
        public static void BuildApk()
        {
            Setup();
            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            Debug.Log($"[build] {report.summary.result} {report.summary.totalSize} bytes, {report.summary.totalErrors} errors");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        static void SetupPlayer()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "BibaiHack";
            PlayerSettings.productName = "a-kun Clay";
            PlayerSettings.SetApplicationIdentifier(android, "com.bibaihack.akunclay");
            PlayerSettings.bundleVersion = AKun.ClayApp.VERSION;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.Android.resizeableActivity = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.Android.forceInternetPermission = true; // ゲームサーバーにつなぐ

            // 入力は Input System (新しい方) だけ
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (ps != null)
            {
                var so = new SerializedObject(ps);
                var p = so.FindProperty("activeInputHandler");
                if (p != null) { p.intValue = 1; so.ApplyModifiedPropertiesWithoutUndo(); }
            }
        }

        static void SetupUrp()
        {
            const string dir = "Assets/AKun/Settings";
            Directory.CreateDirectory(dir);
            var rendererPath = $"{dir}/AKunRenderer.asset";
            var assetPath = $"{dir}/AKunURP.asset";
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            renderer.postProcessData = null; // ポストエフェクトは使わない (Android XR の推奨)
            AddSplatFeature(renderer);
            EditorUtility.SetDirty(renderer);
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
            if (urp == null)
            {
                urp = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(urp, assetPath);
            }
            urp.supportsHDR = false;      // Android XR の推奨
            urp.msaaSampleCount = 4;
            urp.renderScale = 1f;
            urp.supportsCameraDepthTexture = false;
            urp.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(urp);
            GraphicsSettings.defaultRenderPipeline = urp;
            int cur = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(cur, false);
        }

        // マップの Gaussian Splat (SPZ) を描く URP の Renderer Feature (UnityGaussianSplatting)。足りなければ足す。
        // パッケージ側で internal なので型名で引く。描くには Vulkan が要る (並べ替えのシェーダーが Wave 命令を使う)
        static void AddSplatFeature(UniversalRendererData data)
        {
            var type = Type.GetType("GaussianSplatting.Runtime.GaussianSplatURPFeature, GaussianSplatting");
            if (type == null) throw new InvalidOperationException("GaussianSplatURPFeature が見つかりません (Packages/manifest.json に org.nesnausk.gaussian-splatting があるか確認)");
            if (data.rendererFeatures.Exists(f => f != null && f.GetType() == type)) return;
            var feature = ScriptableObject.CreateInstance(type);
            feature.name = type.Name;
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetupXR()
        {
            // XR Plug-in Management: Android で OpenXR を使う
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perBT);
            if (perBT == null)
            {
                Directory.CreateDirectory("Assets/XR");
                perBT = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perBT, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBT, true);
            }
            if (!perBT.HasSettingsForBuildTarget(BuildTargetGroup.Android)) perBT.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!perBT.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android)) perBT.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var general = perBT.SettingsForBuildTarget(BuildTargetGroup.Android);
            general.InitManagerOnStart = true;
            var mgr = perBT.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            bool ok = XRPackageMetadataStore.AssignLoader(mgr, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android);
            Debug.Log($"[setup] OpenXR loader: {ok}");
            EditorUtility.SetDirty(mgr);
            EditorUtility.SetDirty(general);

            // OpenXR の機能: Android XR、セッション、パススルー、手の追跡、手のレイ (Hand Interaction)
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var set = OpenXRFeatureSetManager.GetFeatureSetWithId(BuildTargetGroup.Android, "com.unity.openxr.featureset.android");
            if (set != null)
            {
                set.isEnabled = true;
                OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(BuildTargetGroup.Android);
            }
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            // 機能グループで有効になる機能のうち、使わないものは外す (余計な権限の確認が出ないように)
            var unused = new[] { "ARBoundingBoxFeature", "ARMeshFeature", "ARPlaneFeature", "AROcclusionFeature", "ARRaycastFeature", "ARAnchorFeature", "ARFaceFeature", "AndroidXRHandMeshData" };
            foreach (var f in settings.GetFeatures())
                if (Array.IndexOf(unused, f.GetType().Name) >= 0 && f.enabled) { f.enabled = false; EditorUtility.SetDirty(f); }
            void Enable<T>() where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
            {
                var f = settings.GetFeature<T>();
                if (f == null) { Debug.LogWarning($"[setup] feature not found: {typeof(T).Name}"); return; }
                f.enabled = true;
                EditorUtility.SetDirty(f);
            }
            Enable<AndroidXRSupportFeature>();
            Enable<ARSessionFeature>();
            Enable<ARCameraFeature>();
            Enable<HandTracking>();
            Enable<HandInteractionProfile>();
            settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            EditorUtility.SetDirty(settings);
            foreach (var f in settings.GetFeatures())
                if (f.enabled) Debug.Log($"[setup] OpenXR feature on: {f.GetType().Name}");
        }

        static void SetupScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("ClayApp").AddComponent<AKun.ClayApp>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
