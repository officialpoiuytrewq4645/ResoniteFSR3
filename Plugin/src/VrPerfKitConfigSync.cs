using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ResoniteFSR3
{
    /// <summary>
    /// Writes the vrperfkit (FSR 1) settings from ResoniteFSR3's config into Renderer\vrperfkit_RSF.yml, so they can be
    /// changed from Resonite's in-game settings page. vrperfkit only reads its yml when Resonite starts, so changes apply
    /// on the next launch. Only the upscaling section's enabled / renderScale / sharpness lines are touched.
    /// </summary>
    internal static class VrPerfKitConfigSync
    {
        public static void Apply(FsrSettings settings)
        {
            try
            {
                string ymlPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "vrperfkit_RSF.yml"));
                if (!File.Exists(ymlPath))
                    return;

                string original = File.ReadAllText(ymlPath);
                string updated = original;
                updated = ReplaceUpscalingValue(updated, "enabled", settings.VrPerfKitUpscaling.Value ? "true" : "false");
                updated = ReplaceUpscalingValue(updated, "renderScale", FormatNumber(Mathf.Clamp(settings.VrPerfKitRenderScale.Value, 25f, 100f), "0.0"));
                updated = ReplaceUpscalingValue(updated, "sharpness", FormatNumber(Mathf.Clamp01(settings.VrPerfKitSharpness.Value), "0.00"));

                if (updated == original)
                    return;

                File.WriteAllText(ymlPath, updated, new UTF8Encoding(false));
                ResoniteFsr3Plugin.Log.LogInfo($"Updated vrperfkit_RSF.yml: upscaling enabled={settings.VrPerfKitUpscaling.Value}, " +
                                               $"renderScale={settings.VrPerfKitRenderScale.Value:0.#}, sharpness={settings.VrPerfKitSharpness.Value:0.##} (applies next launch)");
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"Could not update vrperfkit_RSF.yml: {exception.Message}");
            }
        }

        // Replaces "  key: value" inside the top-level "upscaling:" block only (other sections reuse key names like "enabled")
        private static string ReplaceUpscalingValue(string yml, string key, string value)
        {
            var pattern = new Regex(@"(?ms)(^upscaling:\s*$.*?^  " + Regex.Escape(key) + @":[ \t]*)[^\r\n#]*?([ \t]*(#[^\r\n]*)?)$");
            return pattern.Replace(yml, match => match.Groups[1].Value + value + match.Groups[2].Value, 1);
        }

        private static string FormatNumber(float number, string format)
        {
            return number.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
