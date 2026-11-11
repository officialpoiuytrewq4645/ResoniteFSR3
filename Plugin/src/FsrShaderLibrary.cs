using System.Linq;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using FidelityFX.FSR3;
using UnityEngine;

namespace ResoniteFSR3
{
    /// <summary>
    /// Loads the precompiled FSR 3.1 compute shaders from the AssetBundle built by ShaderProject (Unity 2019.4) and
    /// assembles the shader sets FSR needs. HDR and LDR inputs use different accumulate-pass variants.
    /// </summary>
    internal sealed class FsrShaderLibrary
    {
        public const string BundleFileName = "resonitefsr3.shaders";

        public Fsr3UpscalerShaders LowDynamicRangeShaders { get; private set; }
        public Fsr3UpscalerShaders HighDynamicRangeShaders { get; private set; }
        public ComputeShader EyeSplitShader { get; private set; }
        public int EyeSplitKernel { get; private set; }
        public int EyeSplitWithOpaqueKernel { get; private set; }
        public int EyeSplitColorOnlyKernel { get; private set; }
        public int MarkFoveaReactiveKernel { get; private set; }
        public Shader FoveationMaskShader { get; private set; }
        public Shader InsetCompositeShader { get; private set; }

        public Fsr3UpscalerShaders GetShaders(bool highDynamicRange)
        {
            return highDynamicRange ? HighDynamicRangeShaders : LowDynamicRangeShaders;
        }

        public static FsrShaderLibrary Load(string bundlePath, ManualLogSource log)
        {
            if (!File.Exists(bundlePath))
            {
                log.LogError($"Shader bundle not found at {bundlePath}");
                return null;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                log.LogError($"Failed to load shader bundle {bundlePath} (built with an incompatible Unity version?)");
                return null;
            }

            var shadersByName = new Dictionary<string, ComputeShader>();
            foreach (var computeShader in bundle.LoadAllAssets<ComputeShader>())
            {
                shadersByName[computeShader.name] = computeShader;
            }
            log.LogInfo($"Loaded {shadersByName.Count} compute shaders: {string.Join(", ", shadersByName.Keys)}");

            ComputeShader Require(string name)
            {
                if (shadersByName.TryGetValue(name, out var shader))
                    return shader;
                throw new KeyNotFoundException($"Compute shader '{name}' missing from {BundleFileName}");
            }

            try
            {
                var library = new FsrShaderLibrary();
                var commonShaders = new Fsr3UpscalerShaders
                {
                    prepareInputsPass = Require("fsr3_prepare_inputs"),
                    lumaPyramidPass = Require("fsr3_luma_pyramid"),
                    shadingChangePyramidPass = Require("fsr3_shading_change_pyramid"),
                    shadingChangePass = Require("fsr3_shading_change"),
                    prepareReactivityPass = Require("fsr3_prepare_reactivity"),
                    lumaInstabilityPass = Require("fsr3_luma_instability"),
                    sharpenPass = Require("fsr3_rcas"),
                    autoGenReactivePass = Require("fsr3_autogen_reactive"),
                    tcrAutoGenPass = Require("fsr3_tcr_autogen"),
                    debugViewPass = Require("fsr3_debug_view"),
                };

                library.LowDynamicRangeShaders = commonShaders.Clone();
                library.LowDynamicRangeShaders.accumulatePass = Require("fsr3_accumulate");
                library.LowDynamicRangeShaders.accumulateSharpenPass = Require("fsr3_accumulate_sharpen");

                library.HighDynamicRangeShaders = commonShaders.Clone();
                library.HighDynamicRangeShaders.accumulatePass = Require("fsr3_accumulate_hdr");
                library.HighDynamicRangeShaders.accumulateSharpenPass = Require("fsr3_accumulate_hdr_sharpen");

                library.EyeSplitShader = Require("resonitefsr3_eye_split");
                library.EyeSplitKernel = library.EyeSplitShader.FindKernel("SplitEye");
                library.EyeSplitWithOpaqueKernel = library.EyeSplitShader.FindKernel("SplitEyeWithOpaque");
                library.EyeSplitColorOnlyKernel = library.EyeSplitShader.FindKernel("SplitColorOnly");
                library.MarkFoveaReactiveKernel = library.EyeSplitShader.FindKernel("MarkFoveaReactive");
                library.FoveationMaskShader = bundle.LoadAllAssets<Shader>().FirstOrDefault(shader => shader.name == "Hidden/ResoniteFSR3/FoveationMask");
                library.InsetCompositeShader = bundle.LoadAllAssets<Shader>().FirstOrDefault(shader => shader.name == "Hidden/ResoniteFSR3/InsetComposite");
                if (library.InsetCompositeShader == null)
                    log.LogWarning("Inset composite shader not found in bundle; quad-views foveation unavailable.");
                if (library.FoveationMaskShader == null)
                    log.LogWarning("Foveation mask shader not found in bundle; foveated rendering tests unavailable.");
                return library;
            }
            catch (KeyNotFoundException exception)
            {
                log.LogError(exception.Message);
                return null;
            }
        }
    }
}
