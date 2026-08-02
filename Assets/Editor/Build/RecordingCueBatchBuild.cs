using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class RecordingCueBatchBuild
{
    public static void BuildAndroid()
    {
        string outputPath = CommandLineValue("outputPath");
        if (string.IsNullOrEmpty(outputPath) || !outputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("outputPath=<absolute .apk path> is required");

        bool originalUseCustomKeystore = PlayerSettings.Android.useCustomKeystore;
        try
        {
            PlayerSettings.Android.useCustomKeystore = false;
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.Development,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception($"Android build failed: {report.summary.result}");
        }
        finally
        {
            PlayerSettings.Android.useCustomKeystore = originalUseCustomKeystore;
        }
    }

    private static string CommandLineValue(string name)
    {
        string prefix = name + "=";
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                return argument.Substring(prefix.Length);
        }
        return null;
    }
}
