using HarmonyLib;
using Renderite.Shared;
using UnityEngine.Rendering.PostProcessing;

namespace ResoniteFSR3
{
    /// <summary>TAA.Render -> FSR 3.1 per-eye pass.</summary>
    [HarmonyPatch(typeof(TemporalAntialiasing), "Render")]
    internal static class TemporalAntialiasingRenderPatch
    {
        private static bool Prefix(PostProcessRenderContext context)
        {
            return !FsrTemporalPass.TryRender(context);
        }
    }

    /// <summary>Mono jitter -> FSR's Halton jitter.</summary>
    [HarmonyPatch(typeof(TemporalAntialiasing), nameof(TemporalAntialiasing.ConfigureJitteredProjectionMatrix))]
    internal static class TemporalAntialiasingMonoJitterPatch
    {
        private static bool Prefix(TemporalAntialiasing __instance, PostProcessRenderContext context)
        {
            return !FsrTemporalPass.TryConfigureJitter(__instance, context);
        }
    }

    /// <summary>Stereo jitter -> FSR's Halton jitter, same offset for both eyes.</summary>
    [HarmonyPatch(typeof(TemporalAntialiasing), nameof(TemporalAntialiasing.ConfigureStereoJitteredProjectionMatrices))]
    internal static class TemporalAntialiasingStereoJitterPatch
    {
        private static bool Prefix(TemporalAntialiasing __instance, PostProcessRenderContext context)
        {
            return !FsrTemporalPass.TryConfigureStereoJitter(__instance, context);
        }
    }

    /// <summary>PPv2 resets TAA history on camera cuts / teleports; reset FSR's history too.</summary>
    [HarmonyPatch(typeof(TemporalAntialiasing), "ResetHistory")]
    internal static class TemporalAntialiasingResetPatch
    {
        private static void Postfix()
        {
            FsrTemporalPass.RequestReset();
        }
    }

    /// <summary>
    /// Resonite forces TAA off in VR (CameraPostprocessingManager.UpdateAA: "This doesn't work right in VR").
    /// When FSR is handling TAA, allow it.
    /// </summary>
    [HarmonyPatch(typeof(CameraPostprocessingManager), "UpdateAA")]
    internal static class CameraPostprocessingManagerUpdateAAPatch
    {
        private static readonly AccessTools.FieldRef<CameraPostprocessingManager, PostProcessLayer> PostProcessLayerRef =
            AccessTools.FieldRefAccess<CameraPostprocessingManager, PostProcessLayer>("_postProcessing");

        private static bool Prefix(CameraPostprocessingManager __instance, AntiAliasingMethod method)
        {
            if (method != AntiAliasingMethod.TAA || !__instance.IsVR || !FsrTemporalPass.CanHandleVR)
                return true;

            var postProcessLayer = PostProcessLayerRef(__instance);
            if (postProcessLayer == null)
                return true;

            postProcessLayer.antialiasingMode = PostProcessLayer.Antialiasing.TemporalAntialiasing;
            postProcessLayer.finalBlitToCameraTarget = !__instance.IsPrimary;
            ResoniteFsr3Plugin.Log.LogInfo($"Enabled TAA (FSR 3.1) for VR camera '{__instance.Camera?.name}', primary {__instance.IsPrimary}");
            return false;
        }
    }
}
