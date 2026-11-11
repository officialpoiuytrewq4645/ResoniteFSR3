using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Builds the FSR 3.1 compute shaders into an AssetBundle loadable by Resonite's Unity 2019.4 renderer.
// Run headless: Unity.exe -batchmode -quit -projectPath ShaderProject -executeMethod BuildFsrBundle.Build
public static class BuildFsrBundle
{
    const string ShaderFolder = "Assets/FSR3";
    const string BundleName = "resonitefsr3.shaders";
    const string OutputFolder = "../Build";

    public static void Build()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        var shaderPaths = AssetDatabase.FindAssets("t:ComputeShader", new[] { ShaderFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderBy(path => path)
            .ToArray();
        var graphicsShaderPaths = AssetDatabase.FindAssets("t:Shader", new[] { ShaderFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderBy(path => path)
            .ToArray();
        foreach (var graphicsShaderPath in graphicsShaderPaths)
        {
            var graphicsShader = AssetDatabase.LoadAssetAtPath<Shader>(graphicsShaderPath);
            bool hasError = ShaderUtil.ShaderHasError(graphicsShader);
            Debug.Log($"[BuildFsrBundle] {graphicsShaderPath}: graphics shader '{graphicsShader.name}', error={hasError}");
            if (hasError)
            {
                Debug.LogError($"[BuildFsrBundle] Graphics shader {graphicsShaderPath} has compile errors, not building bundle");
                EditorApplication.Exit(1);
                return;
            }
        }

        if (shaderPaths.Length == 0)
        {
            Debug.LogError("[BuildFsrBundle] No compute shaders found in " + ShaderFolder);
            EditorApplication.Exit(1);
            return;
        }

        int errorCount = 0;
        foreach (var shaderPath in shaderPaths)
        {
            var computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(shaderPath);
            int messageCount = ShaderUtil.GetComputeShaderMessageCount(computeShader);
            var messages = ShaderUtil.GetComputeShaderMessages(computeShader);
            int shaderErrors = messages.Count(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
            errorCount += shaderErrors;
            Debug.Log($"[BuildFsrBundle] {shaderPath}: kernel CS={(computeShader.HasKernel("CS") ? "ok" : "MISSING")}, messages={messageCount}, errors={shaderErrors}");
            foreach (var message in messages)
            {
                Debug.Log($"[BuildFsrBundle]   {message.severity} {message.file}:{message.line} {message.message} {message.messageDetails}");
            }
        }

        if (errorCount > 0)
        {
            Debug.LogError($"[BuildFsrBundle] {errorCount} shader compile error(s), not building bundle");
            EditorApplication.Exit(1);
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        var bundleBuild = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = shaderPaths.Concat(graphicsShaderPaths).ToArray(),
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            OutputFolder,
            new[] { bundleBuild },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[BuildFsrBundle] BuildAssetBundles failed");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[BuildFsrBundle] Built {BundleName} with {shaderPaths.Length} compute shaders into {Path.GetFullPath(OutputFolder)}");
    }
}
