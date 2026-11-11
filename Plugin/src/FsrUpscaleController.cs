using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;

namespace ResoniteFSR3
{
    /// <summary>
    /// FSR 3.1 VR upscaling control.
    ///
    /// Resonite's Unity ignores XRSettings.renderViewportScale, so upscaling uses XRSettings.eyeTextureResolutionScale
    /// instead: the eye textures themselves become smaller (Unity reallocates them when the scale changes, which costs a
    /// brief hitch). The whole frame, including post-processing, renders at that size. At the end of the camera, FSR 3.1
    /// upscales each eye into its own full-resolution texture, and the ResoniteFSR3-patched vrperfkit dxgi.dll (which hooks
    /// OpenVR submits) swaps those textures in when Unity submits the eyes.
    /// </summary>
    internal static class FsrUpscaleController
    {
        private delegate void SetSubmitViewportScaleDelegate(float scale);
        private delegate void SetSubmitTextureOverrideDelegate(int eyeIndex, IntPtr texture);
        private delegate void SetSubmitFlipVerticalDelegate(int flip);

        private static SetSubmitFlipVerticalDelegate _setSubmitFlipVertical;
        private static int _appliedFlip = -1;

        /// <summary>Tells the submit hook whether to present the override textures vertically flipped.</summary>
        public static void SetSubmitFlipVertical(bool flip)
        {
            int value = flip ? 1 : 0;
            if (_setSubmitFlipVertical == null || value == _appliedFlip)
                return;
            _appliedFlip = value;
            _setSubmitFlipVertical(value);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        private static SetSubmitTextureOverrideDelegate _setSubmitTextureOverride;
        private static bool _initialized;
        private static bool _eyeTextureScaleRejected;
        private static float _requestedScale = 1f;
        private static int _lastActiveFrame = -1;

        /// <summary>Benchmark override for the render scale (null = use the Upscaling setting).</summary>
        public static float? ScaleOverride;
        private static readonly IntPtr[] OverrideTextures = { IntPtr.Zero, IntPtr.Zero };

        public static bool SubmitHookAvailable
        {
            get
            {
                EnsureInitialized();
                return _setSubmitTextureOverride != null;
            }
        }

        /// <summary>
        /// True when this frame's eye textures are actually rendered at a reduced scale and FSR must upscale them at the
        /// end of the frame.
        /// </summary>
        public static bool UpscalingActive => _requestedScale < 0.999f && XRSettings.eyeTextureResolutionScale < 0.999f;

        /// <summary>The eye-texture scale the current eye textures were rendered with.</summary>
        public static float CurrentEyeTextureScale => Mathf.Clamp(XRSettings.eyeTextureResolutionScale, 0.25f, 1f);

        // Native (scale 1) eye size, measured while rendering at full resolution. Used as FSR's output size so that the
        // frame where the scale changes (before Unity reallocates the eye textures) doesn't produce a bogus target size.
        private static int _nativeEyeWidth;
        private static int _nativeEyeHeight;

        /// <summary>Full-resolution eye size FSR should upscale to.</summary>
        public static Vector2Int GetDisplaySize(int renderWidth, int renderHeight)
        {
            if (_nativeEyeWidth > 0 && _nativeEyeHeight > 0)
                return new Vector2Int(_nativeEyeWidth, _nativeEyeHeight);
            float scale = CurrentEyeTextureScale;
            return new Vector2Int(Mathf.RoundToInt(renderWidth / scale), Mathf.RoundToInt(renderHeight / scale));
        }

        private static void RecordNativeSizeIfFullResolution()
        {
            // SteamVR's recommended per-eye size (including its resolution setting) is what Unity allocates at scale 1.
            // Read it directly: Unity's eye texture size lags while textures are reallocated after a scale change.
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system != null)
                {
                    uint recommendedWidth = 0, recommendedHeight = 0;
                    system.GetRecommendedRenderTargetSize(ref recommendedWidth, ref recommendedHeight);
                    if (recommendedWidth >= 256 && recommendedHeight >= 256)
                    {
                        if (_nativeEyeWidth != (int)recommendedWidth || _nativeEyeHeight != (int)recommendedHeight)
                            ResoniteFsr3Plugin.Log.LogInfo($"Native VR eye size (SteamVR recommended): {recommendedWidth}x{recommendedHeight}");
                        _nativeEyeWidth = (int)recommendedWidth;
                        _nativeEyeHeight = (int)recommendedHeight;
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to measuring Unity's eye textures
            }

            if (_requestedScale >= 0.999f && XRSettings.eyeTextureResolutionScale >= 0.999f && XRSettings.eyeTextureWidth > 0)
            {
                _nativeEyeWidth = XRSettings.eyeTextureWidth;
                _nativeEyeHeight = XRSettings.eyeTextureHeight;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;

            // Bind to the patched dxgi.dll in the renderer folder by full path (the system dxgi.dll is loaded too)
            string proxyPath = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "dxgi.dll");
            IntPtr module = GetModuleHandleW(proxyPath);
            if (module == IntPtr.Zero)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"Patched vrperfkit dxgi.dll not loaded from {proxyPath}; FSR 3.1 VR upscaling unavailable (native AA only).");
                return;
            }

            IntPtr overrideFunction = GetProcAddress(module, "ResoniteFSR3_SetSubmitTextureOverride");
            if (overrideFunction == IntPtr.Zero)
            {
                ResoniteFsr3Plugin.Log.LogWarning("dxgi.dll is missing ResoniteFSR3_SetSubmitTextureOverride (older add-on?); FSR 3.1 VR upscaling unavailable.");
                return;
            }
            _setSubmitTextureOverride = (SetSubmitTextureOverrideDelegate)Marshal.GetDelegateForFunctionPointer(overrideFunction, typeof(SetSubmitTextureOverrideDelegate));

            IntPtr flipFunction = GetProcAddress(module, "ResoniteFSR3_SetSubmitFlipVertical");
            if (flipFunction != IntPtr.Zero)
                _setSubmitFlipVertical = (SetSubmitFlipVerticalDelegate)Marshal.GetDelegateForFunctionPointer(flipFunction, typeof(SetSubmitFlipVerticalDelegate));
            else
                ResoniteFsr3Plugin.Log.LogWarning("dxgi.dll is missing ResoniteFSR3_SetSubmitFlipVertical (older add-on?); upscaled VR may appear upside down.");

            // Older experiment: make sure the bounds-expansion path is off
            IntPtr viewportFunction = GetProcAddress(module, "ResoniteFSR3_SetSubmitViewportScale");
            if (viewportFunction != IntPtr.Zero)
                ((SetSubmitViewportScaleDelegate)Marshal.GetDelegateForFunctionPointer(viewportFunction, typeof(SetSubmitViewportScaleDelegate)))(1f);

            ResoniteFsr3Plugin.Log.LogInfo("Bound to patched vrperfkit submit hook; FSR 3.1 VR upscaling available.");
        }

        /// <summary>Called once per frame from the stereo jitter setup (OnPreCull), before the eyes render.</summary>
        public static void UpdateForStereoFrame()
        {
            _lastActiveFrame = Time.frameCount;
            RecordNativeSizeIfFullResolution();
            // Quad views has its own outer resolution; otherwise the Upscaling setting decides
            float settingScale = FoveationController.QuadViewsWanted
                ? ResoniteFsr3Plugin.Settings.QuadViewOuterScale.Value
                : ResoniteFsr3Plugin.Settings.RenderScale;
            float desiredScale = (!_eyeTextureScaleRejected && SubmitHookAvailable) ? (ScaleOverride ?? settingScale) : 1f;
            ApplyScale(desiredScale);
            if (!UpscalingActive)
                ClearOverrides();
        }

        /// <summary>Called every frame; restores native resolution once FSR stops handling VR frames.</summary>
        public static void RestoreIfInactive()
        {
            if (Time.frameCount - _lastActiveFrame > 2 && (_requestedScale < 0.999f || OverrideTextures[0] != IntPtr.Zero || OverrideTextures[1] != IntPtr.Zero))
                Reset();
        }

        public static void Reset()
        {
            ClearOverrides();
            ApplyScale(1f);
        }

        /// <summary>Registers the full-resolution FSR output for an eye with the submit hook (null clears it).</summary>
        public static void SetOverride(int eyeIndex, RenderTexture texture)
        {
            if (_setSubmitTextureOverride == null || eyeIndex < 0 || eyeIndex > 1)
                return;
            IntPtr nativeTexture = texture != null ? texture.GetNativeTexturePtr() : IntPtr.Zero;
            if (OverrideTextures[eyeIndex] == nativeTexture)
                return;
            OverrideTextures[eyeIndex] = nativeTexture;
            _setSubmitTextureOverride(eyeIndex, nativeTexture);
            if (ResoniteFsr3Plugin.Settings.VerboseLogging.Value)
                ResoniteFsr3Plugin.Log.LogInfo($"Submit override eye {eyeIndex}: {(texture != null ? $"{texture.width}x{texture.height} {texture.format}" : "cleared")}");
        }

        public static void ClearOverrides()
        {
            SetOverride(0, null);
            SetOverride(1, null);
        }

        private static void ApplyScale(float scale)
        {
            if (Mathf.Approximately(scale, _requestedScale) && Mathf.Approximately(XRSettings.eyeTextureResolutionScale, scale))
                return;

            float previous = _requestedScale;
            XRSettings.eyeTextureResolutionScale = scale;
            if (scale < 0.999f && !Mathf.Approximately(XRSettings.eyeTextureResolutionScale, scale))
            {
                _eyeTextureScaleRejected = true;
                ResoniteFsr3Plugin.Log.LogWarning($"Unity rejected XRSettings.eyeTextureResolutionScale = {scale:0.###} (reads back {XRSettings.eyeTextureResolutionScale:0.###}); " +
                                                  "FSR 3.1 VR upscaling unavailable, staying at native resolution.");
                XRSettings.eyeTextureResolutionScale = 1f;
                scale = 1f;
            }

            _requestedScale = scale;
            if (!Mathf.Approximately(previous, scale))
                ResoniteFsr3Plugin.Log.LogInfo($"VR eye texture scale {previous:0.###} -> {scale:0.###} (Unity reports {XRSettings.eyeTextureResolutionScale:0.###}, {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight})");
        }
    }
}
