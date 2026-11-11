using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ResoniteFSR3
{
    /// <summary>
    /// Renderer-side BepInEx plugin: replaces Resonite's TAA anti-aliasing option with AMD FSR 3.1 (via a Unity 2019.4
    /// backport of FSR3Unity), including in VR.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ResoniteFsr3Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ResoniteFSR3";
        public const string PluginName = "Resonite FSR 3.1";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static FsrSettings Settings;
        internal static FsrShaderLibrary Shaders;

        private void Awake()
        {
            Log = Logger;
            Settings = new FsrSettings(Config);

            if (!SystemInfo.supportsComputeShaders)
            {
                Log.LogError("Compute shaders are not supported on this GPU/API; FSR 3.1 disabled.");
                return;
            }

            var pluginDirectory = Path.GetDirectoryName(Info.Location);
            Shaders = FsrShaderLibrary.Load(Path.Combine(pluginDirectory, FsrShaderLibrary.BundleFileName), Log);
            if (Shaders == null)
            {
                Log.LogError("FSR 3.1 shaders failed to load; plugin inactive.");
                return;
            }

            VrPerfKitConfigSync.Apply(Settings);

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(ResoniteFsr3Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Graphics: {SystemInfo.graphicsDeviceType} on {SystemInfo.graphicsDeviceName}. " +
                        "Select TAA in Resonite's anti-aliasing setting to use FSR 3.1.");
        }

        // BepInEx does not watch its config files, so reload ResoniteFSR3.cfg when it changes on disk.
        // The watcher fires on a background thread; the reload itself happens on Unity's main thread in Update.
        private FileSystemWatcher _configWatcher;
        private volatile bool _configChanged;
        private float _configChangedAt;

        private void Start()
        {
            try
            {
                _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(Config.ConfigFilePath), Path.GetFileName(Config.ConfigFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _configWatcher.Changed += (sender, eventArguments) => _configChanged = true;
            }
            catch (System.Exception exception)
            {
                Log.LogWarning($"Live config reload unavailable: {exception.Message}");
            }
        }

        private void Update()
        {
            if (Shaders != null)
            {
                FsrUpscaleController.RestoreIfInactive();
                FoveationController.Tick();
            }

            if (!_configChanged)
                return;

            // Wait briefly so an editor has finished writing the file
            if (_configChangedAt <= 0f)
            {
                _configChangedAt = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _configChangedAt < 0.5f)
                return;

            _configChanged = false;
            _configChangedAt = 0f;
            try
            {
                bool treatNoMotionWasOn = Settings.TreatNoMotionAsCamera.Value;
                Config.Reload();
                FsrTemporalPass.RequestReset();
                VrPerfKitConfigSync.Apply(Settings);
                if (!treatNoMotionWasOn && Settings.TreatNoMotionAsCamera.Value)
                    MotionVectorModePatch.RemapExistingRenderers();
                Log.LogInfo("Config reloaded: " +
                    $"Enabled={Settings.Enabled.Value} Upscaling={Settings.Upscaling.Value} (scale {Settings.RenderScale:0.###}) Sharpening={Settings.Sharpening.Value} Sharpness={Settings.Sharpness.Value} " +
                    $"DebugView={Settings.DebugView.Value} DisableHistory={Settings.DisableHistory.Value} DepthBuffer={Settings.DepthSource.Value} " +
                    $"MotionVectorScale=({Settings.MotionVectorScaleX.Value},{Settings.MotionVectorScaleY.Value}) JitterScale={Settings.JitterScale.Value} " +
                    $"FlipJitter=({Settings.FlipJitterX.Value},{Settings.FlipJitterY.Value})");
            }
            catch (System.Exception exception)
            {
                Log.LogWarning($"Config reload failed: {exception.Message}");
            }
        }

        private void OnDestroy()
        {
            _configWatcher?.Dispose();
            FsrUpscaleController.Reset();
            FoveationController.DestroyAll();
            FsrTemporalPass.DestroyAll();
        }
    }
}
