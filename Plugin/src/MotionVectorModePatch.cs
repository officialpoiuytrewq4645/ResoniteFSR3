using System.Reflection;
using HarmonyLib;
using Renderite.Shared;
using UnityEngine;

namespace ResoniteFSR3
{
    /// <summary>
    /// Resonite maps each renderer's MotionVectorMode to Unity through Renderite.Unity.Helper.ToUnity(MotionVectorMode)
    /// (used by mesh, billboard and trail renderers). NoMotion becomes Unity's ForceNoMotion, which writes zero motion
    /// vectors; FSR then reuses history as if the object never moves on screen. When enabled, remap it to Camera so
    /// world-fixed objects get correct motion from the camera.
    /// </summary>
    [HarmonyPatch]
    internal static class MotionVectorModePatch
    {
        private static int _remappedCount;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Renderite.Unity.Helper:ToUnity", new[] { typeof(MotionVectorMode) });
        }

        private static void Postfix(ref MotionVectorGenerationMode __result)
        {
            if (__result != MotionVectorGenerationMode.ForceNoMotion || !ResoniteFsr3Plugin.Settings.TreatNoMotionAsCamera.Value)
                return;

            __result = MotionVectorGenerationMode.Camera;
            if (_remappedCount++ == 0)
                ResoniteFsr3Plugin.Log.LogInfo("Remapping NoMotion renderers to Camera motion vectors (TreatNoMotionAsCamera)");
        }

        /// <summary>One-time pass when the option is switched on, for renderers that were already set up.</summary>
        public static void RemapExistingRenderers()
        {
            int changed = 0;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer.motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion)
                {
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                    changed++;
                }
            }
            ResoniteFsr3Plugin.Log.LogInfo($"TreatNoMotionAsCamera: remapped {changed} already-loaded renderers from NoMotion to Camera");
        }
    }
}
