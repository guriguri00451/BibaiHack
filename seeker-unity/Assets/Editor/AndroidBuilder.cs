using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Seeker.Editor
{
    public static class AndroidBuilder
    {
        private const string OutputPath = "Build/seeker.apk";

        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
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
    }
}
