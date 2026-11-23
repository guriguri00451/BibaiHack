using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using Unity.XR.CoreUtils;
using Seeker.App;

namespace Seeker.Editor
{
    /// <summary>
    /// AR 用のプロジェクト設定・参照画像ライブラリ・シーンを再現可能に作るスクリプト。
    /// 実行: unity run -- -executeMethod Seeker.Editor.ProjectSetup.Run -quit
    /// </summary>
    public static class ProjectSetup
    {
        private const string MarkerTexturePath = "Assets/AR/marker.png";
        private const string LibraryPath = "Assets/AR/MarkerLibrary.asset";
        private const string ScenePath = "Assets/Scenes/SeekerAr.unity";
        // 印刷する実物の一辺（余白込み）。実物とずれると位置がスケール分ずれる
        private const float MarkerWidthMeters = 0.15f;

        public static void Run()
        {
            ConfigurePlayer();
            ConfigureXrManagement();
            AddRendererFeatures();
            var library = CreateReferenceImageLibrary();
            CreateScene(library);
            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSetup] done");
        }

        private static void ConfigurePlayer()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "BibaiHack";
            PlayerSettings.productName = "Seeker";
            PlayerSettings.SetApplicationIdentifier(android, "com.bibaihack.seeker");
            // ARCore 必須アプリを Vulkan で動かすには API 29 以上が要る（ビルド時に ARCore が弾く）
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // Gaussian Splatting の並べ替えシェーダーが Wave 命令を使い GLES3 では動かないため、Vulkan に固定する
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        }

        private static void ConfigureXrManagement()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perBuildTarget))
            {
                Directory.CreateDirectory("Assets/XR");
                perBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perBuildTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBuildTarget, true);
            }

            const BuildTargetGroup group = BuildTargetGroup.Android;
            if (!perBuildTarget.HasSettingsForBuildTarget(group)) perBuildTarget.CreateDefaultSettingsForBuildTarget(group);
            perBuildTarget.SettingsForBuildTarget(group).InitManagerOnStart = true;
            if (!perBuildTarget.HasManagerSettingsForBuildTarget(group)) perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            var manager = perBuildTarget.ManagerSettingsForBuildTarget(group);
            if (!XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.ARCore.ARCoreLoader", group))
                throw new System.InvalidOperationException("ARCoreLoader を XR Management に割り当てられなかった");
            EditorUtility.SetDirty(perBuildTarget);
            EditorUtility.SetDirty(manager);
        }

        /// <summary>
        /// URP の Renderer Feature をそろえる（足りないものだけ足す）。
        /// 既存プロジェクトに足したいときは -executeMethod Seeker.Editor.ProjectSetup.AddRendererFeatures で呼ぶ。
        /// </summary>
        public static void AddRendererFeatures()
        {
            // カメラ映像の描画に必須。無いと背景が真っ黒になる
            AddRendererFeature(typeof(ARBackgroundRendererFeature));
            // Vulkan では ARCore がカメラ映像の受け渡しをこの Feature の描画イベントで行う。無いと背景が黒くトラッキングも始まらない
            AddRendererFeature(typeof(ARCommandBufferSupportRendererFeature));
            // マップの Gaussian Splat（SPZ）を描く。パッケージ側で internal なので型名で引く
            var splatFeature = System.Type.GetType("GaussianSplatting.Runtime.GaussianSplatURPFeature, GaussianSplatting")
                ?? throw new System.InvalidOperationException("GaussianSplatURPFeature が見つからない（UnityGaussianSplatting と URP が入っているか確認）");
            AddRendererFeature(splatFeature);
            AssetDatabase.SaveAssets();
        }

        private static void AddRendererFeature(System.Type featureType)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data.rendererFeatures.Exists(f => f != null && f.GetType() == featureType)) continue;

                var feature = ScriptableObject.CreateInstance(featureType);
                feature.name = featureType.Name;
                AssetDatabase.AddObjectToAsset(feature, data);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

                var serialized = new SerializedObject(data);
                var features = serialized.FindProperty("m_RendererFeatures");
                var map = serialized.FindProperty("m_RendererFeatureMap");
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(data);
            }
        }

        private static XRReferenceImageLibrary CreateReferenceImageLibrary()
        {
            AssetDatabase.ImportAsset(MarkerTexturePath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(MarkerTexturePath);
            AssetDatabase.DeleteAsset(LibraryPath);
            var library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
            library.Add();
            library.SetName(0, "marker");
            library.SetTexture(0, texture, true);
            library.SetSpecifySize(0, true);
            library.SetSize(0, new Vector2(MarkerWidthMeters, MarkerWidthMeters));
            EditorUtility.SetDirty(library);
            return library;
        }

        private static void CreateScene(XRReferenceImageLibrary library)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            new GameObject("AR Session", typeof(ARSession));

            var origin = new GameObject("XR Origin", typeof(XROrigin));
            var cameraObject = new GameObject("AR Camera", typeof(Camera), typeof(ARCameraManager), typeof(ARCameraBackground), typeof(TrackedPoseDriver));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(origin.transform, false);
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 20f;
            cameraObject.AddComponent<UniversalAdditionalCameraData>();

            var poseDriver = cameraObject.GetComponent<TrackedPoseDriver>();
            poseDriver.positionInput = CreateAction("position", "<XRHMD>/centerEyePosition", "<HandheldARInputDevice>/devicePosition");
            poseDriver.rotationInput = CreateAction("rotation", "<XRHMD>/centerEyeRotation", "<HandheldARInputDevice>/deviceRotation");
            poseDriver.trackingStateInput = CreateAction("trackingState", "<XRHMD>/trackingState", "<HandheldARInputDevice>/trackingState");

            var xrOrigin = origin.GetComponent<XROrigin>();
            xrOrigin.Camera = camera;
            xrOrigin.CameraFloorOffsetObject = origin;

            var imageManager = origin.AddComponent<ARTrackedImageManager>();
            imageManager.referenceLibrary = library;
            imageManager.requestedMaxNumberOfMovingImages = 1;

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "MapCube";
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            var cubeMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.2f, 0.4f) };
            AssetDatabase.CreateAsset(cubeMaterial, "Assets/AR/MapCube.mat");
            cube.GetComponent<MeshRenderer>().sharedMaterial = cubeMaterial;

            var controller = new GameObject("SeekerArController", typeof(SeekerArController));
            var serialized = new SerializedObject(controller.GetComponent<SeekerArController>());
            serialized.FindProperty("imageManager").objectReferenceValue = imageManager;
            serialized.FindProperty("cube").objectReferenceValue = cube.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static InputActionProperty CreateAction(string name, params string[] bindings)
        {
            var action = new InputAction(name);
            foreach (var binding in bindings) action.AddBinding(binding);
            return new InputActionProperty(action);
        }
    }
}
