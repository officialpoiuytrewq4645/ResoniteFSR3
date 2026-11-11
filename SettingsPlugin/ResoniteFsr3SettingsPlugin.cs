using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;

namespace ResoniteFSR3
{
    /// <summary>
    /// Engine-side companion to the ResoniteFSR3 renderer plugin.
    ///
    /// The FSR 3.1 renderer plugin runs inside Resonite's separate Unity renderer process, whose BepInEx config is
    /// invisible to the dashboard. This plugin declares the same settings in the engine's BepInEx (so BepisModSettings
    /// shows them under Settings -> Mods) and mirrors every change into Renderer\BepInEx\config\ResoniteFSR3.cfg,
    /// which the renderer plugin watches and reloads live.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("ResoniteModding.BepisModSettings", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ResoniteFsr3SettingsPlugin : BasePlugin
    {
        public const string PluginGuid = "ResoniteFSR3.Settings";
        public const string PluginName = "Resonite FSR 3.1";
        public const string PluginVersion = "0.1.0";

        private const string RendererConfigFileName = "ResoniteFSR3.cfg";

        private ConfigFile _rendererConfig;
        private ResoniteGazeForwarder _gazeForwarder;

        public override void Load()
        {
            // First run: adopt whatever the renderer config already holds, instead of overwriting it with defaults
            bool engineConfigExisted = File.Exists(Config.ConfigFilePath);
            var engineSettings = new FsrSettings(Config);

            // Pass Resonite's own eye tracking to the renderer for foveation (only sends while Gaze = Resonite)
            _gazeForwarder = new ResoniteGazeForwarder(engineSettings, Log);
            _gazeForwarder.Start();

            string rendererConfigPath = FindRendererConfigPath();
            if (rendererConfigPath == null)
            {
                Log.LogError("Could not find Resonite's Renderer\\BepInEx\\config folder; is the ResoniteFSR3 renderer plugin installed?");
                return;
            }

            _rendererConfig = new ConfigFile(rendererConfigPath, true);
            new FsrSettings(_rendererConfig);

            if (engineConfigExisted)
            {
                CopyAll(Config, _rendererConfig);
                Log.LogInfo($"Pushed FSR settings to renderer config {rendererConfigPath}");
            }
            else
            {
                CopyAll(_rendererConfig, Config);
                Log.LogInfo($"Imported existing FSR settings from renderer config {rendererConfigPath}");
            }

            Config.SettingChanged += OnEngineSettingChanged;
        }

        private void OnEngineSettingChanged(object sender, SettingChangedEventArgs eventArguments)
        {
            try
            {
                var definition = eventArguments.ChangedSetting.Definition;
                if (_rendererConfig.ContainsKey(definition))
                {
                    _rendererConfig[definition].BoxedValue = eventArguments.ChangedSetting.BoxedValue;
                    _rendererConfig.Save();
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning($"Failed to mirror setting to renderer: {exception.Message}");
            }
        }

        private static void CopyAll(ConfigFile source, ConfigFile target)
        {
            bool previousSaveOnSet = target.SaveOnConfigSet;
            target.SaveOnConfigSet = false;
            foreach (var sourceEntry in source)
            {
                if (target.ContainsKey(sourceEntry.Key))
                    target[sourceEntry.Key].BoxedValue = sourceEntry.Value.BoxedValue;
            }
            target.Save();
            target.SaveOnConfigSet = previousSaveOnSet;
        }

        private static string FindRendererConfigPath()
        {
            foreach (var root in new[] { Paths.GameRootPath, AppContext.BaseDirectory })
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                string configDirectory = Path.Combine(root, "Renderer", "BepInEx", "config");
                if (Directory.Exists(configDirectory))
                    return Path.Combine(configDirectory, RendererConfigFileName);
            }
            return null;
        }
    }
}
