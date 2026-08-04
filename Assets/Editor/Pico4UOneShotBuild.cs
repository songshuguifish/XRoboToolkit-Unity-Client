using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class Pico4UOneShotBuild
{
    public static void Build()
    {
        const string output = "/private/tmp/pico-enterprise-usb.apk";
        var scenes = Array.FindAll(EditorBuildSettings.scenes, s => s.enabled);
        var scenePaths = new string[scenes.Length];
        for (var i = 0; i < scenes.Length; i++) scenePaths[i] = scenes[i].path;

        var originalBundleVersionCode = PlayerSettings.Android.bundleVersionCode;
        try
        {
            PlayerSettings.Android.bundleVersionCode = Math.Max(originalBundleVersionCode, 1010);
            EditorUserBuildSettings.androidBuildType = AndroidBuildType.Debug;
            PlayerSettings.Android.useCustomKeystore = false;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.AllowDebugging
            });

            Debug.Log($"PICO4U build result={report.summary.result}, output={output}, errors={report.summary.totalErrors}");
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("PICO4U Android build failed");
        }
        finally
        {
            PlayerSettings.Android.bundleVersionCode = originalBundleVersionCode;
            AssetDatabase.SaveAssets();
        }
    }
}
