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

        /// <summary>開発用（Development ビルド）。ローカルサーバー（http）に繋ぐため平文HTTPを許可し、dev-connection.json を読む。</summary>
        public static void Build() => BuildApk(InsecureHttpOption.AlwaysAllowed, BuildOptions.Development);

        /// <summary>リリース用。平文HTTPを禁止するので、https / wss の実サーバーにしか繋がらない。</summary>
        public static void BuildRelease() => BuildApk(InsecureHttpOption.NotAllowed, BuildOptions.None);

        private static void BuildApk(InsecureHttpOption insecureHttpOption, BuildOptions buildOptions)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            EnsureGltfastShadersIncluded();
            // ProjectSettings に書き戻されるので、ビルドのたびに明示して前回の値を持ち越さない
            PlayerSettings.insecureHttpOption = insecureHttpOption;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SeekerAr.unity" },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = buildOptions,
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
