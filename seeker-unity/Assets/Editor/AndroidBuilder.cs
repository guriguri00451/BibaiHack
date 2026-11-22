using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Seeker.Editor
{
    public static class AndroidBuilder
    {
        private const string OutputPath = "Build/seeker.apk";

        // glTFast は実行時に Shader.Find でシェーダーグラフを引くため、シーンから参照されずビルドで削られる。
        // 削られると GLB のマテリアルがピンク(エラーシェーダー)になる
        private static readonly string[] GltfastShaderNames =
        {
            "Shader Graphs/glTF-pbrMetallicRoughness",
            "Shader Graphs/glTF-unlit",
            "Shader Graphs/glTF-pbrSpecularGlossiness",
        };

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            EnsureGltfastShadersIncluded();
            // 開発中のモックサーバーは http。既定(禁止)だと署名URL取得とGLBダウンロードが失敗する。
            // 本番サーバーは https 前提なので、リリースビルドでは外すこと
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SeekerAr.unity" },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception($"Android build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
        }

        private static void EnsureGltfastShadersIncluded()
        {
            var graphicsSettings = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/GraphicsSettings.asset"));
            var included = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in GltfastShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null) throw new Exception($"glTFast のシェーダーが見つからない: {name}");
                if (IsIncluded(included, shader)) continue;
                included.InsertArrayElementAtIndex(included.arraySize);
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
            }
            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool IsIncluded(SerializedProperty included, Shader shader)
        {
            for (var i = 0; i < included.arraySize; i++)
            {
                if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader) return true;
            }
            return false;
        }
    }
}
