using BepInEx.Configuration;

namespace ResoniteFSR3
{
    /// <summary>FSR quality presets (per-axis render scale = 1 / ratio), plus a custom scale.</summary>
    internal enum UpscaleMode
    {
        NativeAA,          // 1.0x  (100%)
        UltraQuality,      // 1.2x  (83%)
        Quality,           // 1.5x  (67%)
        Balanced,          // 1.7x  (59%)
        Performance,       // 2.0x  (50%)
        Custom,
    }

    /// <summary>How the graded foveation mask is injected (experimental test pathways).</summary>
    internal enum FoveationMode
    {
        Off,
        /// <summary>Camera command buffer at BeforeGBuffer draws the depth mask.</summary>
        MaskCommandBuffer,
        /// <summary>Invisible full-screen renderer drawn first in the G-buffer pass writes the depth mask.</summary>
        MaskRenderer,
        /// <summary>
        /// Quad views: the whole view renders at the Upscaling render scale and FSR upscales it, plus a small
        /// full-resolution inset per eye that follows the gaze. Needs Upscaling below NativeAA.
        /// </summary>
        QuadViews,
    }

    internal enum FoveationFill
    {
        None,
        Spatial,
    }

    internal enum GazeSource
    {
        Center,
        Baballonia,
        /// <summary>The eye tracking Resonite itself receives (any supported tracker), forwarded by the engine-side plugin.</summary>
        Resonite,
        /// <summary>SteamVR's own eye-tracked foveation center (any eye tracker with a SteamVR driver). Lowest latency.</summary>
        SteamVR,
    }

    /// <summary>How the gaze is turned into the two eyes' foveation centers.</summary>
    internal enum GazeMode
    {
        /// <summary>Each eye's circle follows that eye's own tracked direction.</summary>
        PerEye,
        /// <summary>Both circles are centred on one shared point (averaged direction at the convergence distance).</summary>
        SharedPoint,
    }

    /// <summary>How the low-resolution outer view is brought up to display resolution in quad views.</summary>
    internal enum OuterUpscaler
    {
        /// <summary>Plain bilinear stretch: cheapest, no anti-aliasing outside the inset.</summary>
        Bilinear,
        /// <summary>FSR 3.1 temporal upscaling: sharper and stable, costs GPU time at display resolution.</summary>
        Fsr,
    }

    /// <summary>Where FSR reads depth from for the quad-view inset cameras.</summary>
    internal enum InsetDepthSource
    {
        /// <summary>Unity's _CameraDepthTexture for the inset camera.</summary>
        CameraDepthTexture,
        /// <summary>The depth buffer of the inset's own render texture.</summary>
        InsetTarget,
        /// <summary>Same as the main view (camera target depth). Reads as empty for the insets in testing.</summary>
        CameraTarget,
    }

    internal enum HdrInputMode
    {
        Auto,
        ForceOn,
        ForceOff,
    }

    /// <summary>How sub-pixel jitter is applied in VR (single-pass stereo).</summary>
    internal enum VrJitterMode
    {
        /// <summary>No jitter in VR (Unity 2019 refuses custom eye projections in single-pass stereo). FSR is told 0.</summary>
        Off,
        /// <summary>Experimental: overwrite the stereo matrix shader globals with jittered ones from a camera command buffer.</summary>
        ShaderGlobals,
    }

    internal enum DepthInputSource
    {
        /// <summary>The depth sub-element of the camera's render target (what FSR3Unity uses for deferred rendering).</summary>
        CameraTargetDepth,
        /// <summary>_CameraDepthTexture by name. Only resolves temporary RTs, so not found in Resonite (kept for testing).</summary>
        CameraDepthTextureGlobal,
        /// <summary>BuiltinRenderTextureType.Depth. Not available in Resonite's deferred renderer (kept for testing).</summary>
        BuiltinDepth,
    }

    /// <summary>
    /// All user-facing and calibration settings. Values are read every frame, so edits to the BepInEx config file
    /// (or a config manager) apply live without restarting Resonite.
    /// </summary>
    internal sealed class FsrSettings
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> EnableInVR;
        public readonly ConfigEntry<UpscaleMode> Upscaling;
        public readonly ConfigEntry<float> CustomRenderScale;
        public readonly ConfigEntry<bool> Sharpening;
        public readonly ConfigEntry<float> Sharpness;
        public readonly ConfigEntry<float> VelocityFactor;

        public readonly ConfigEntry<bool> VrPerfKitUpscaling;
        public readonly ConfigEntry<float> VrPerfKitRenderScale;
        public readonly ConfigEntry<float> VrPerfKitSharpness;

        public readonly ConfigEntry<bool> TreatNoMotionAsCamera;

        public readonly ConfigEntry<FoveationMode> Foveation;
        public readonly ConfigEntry<FoveationFill> FoveationFillMode;
        public readonly ConfigEntry<float> FoveationMaskedReactive;
        public readonly ConfigEntry<GazeSource> FoveationGaze;
        public readonly ConfigEntry<float> FoveationInnerRadius;
        public readonly ConfigEntry<float> FoveationMidRadius;
        public readonly ConfigEntry<float> FoveationOuterRadius;
        public readonly ConfigEntry<int> BaballoniaGazePort;
        public readonly ConfigEntry<int> ResoniteGazePort;
        public readonly ConfigEntry<bool> GazeInvertPitch;
        public readonly ConfigEntry<bool> GazeInvertYaw;
        public readonly ConfigEntry<float> GazeDistance;
        public readonly ConfigEntry<GazeMode> FoveationGazeMode;
        public readonly ConfigEntry<bool> RunFoveationBenchmark;
        public readonly ConfigEntry<float> QuadViewInsetSize;
        public readonly ConfigEntry<float> QuadViewDeadZone;
        public readonly ConfigEntry<float> QuadViewOuterScale;
        public readonly ConfigEntry<OuterUpscaler> QuadViewOuterUpscaler;
        public readonly ConfigEntry<float> QuadViewFeather;
        public readonly ConfigEntry<bool> QuadViewShowBorder;
        public readonly ConfigEntry<bool> QuadViewFlipVertical;
        public readonly ConfigEntry<bool> QuadViewDumpInset;
        public readonly ConfigEntry<InsetDepthSource> QuadViewInsetDepth;

        public readonly ConfigEntry<bool> ReactiveMask;
        public readonly ConfigEntry<float> ReactiveScale;
        public readonly ConfigEntry<float> ReactiveThreshold;
        public readonly ConfigEntry<float> ReactiveBinaryValue;

        public readonly ConfigEntry<bool> DebugView;
        public readonly ConfigEntry<bool> DisableHistory;
        public readonly ConfigEntry<float> MotionVectorScaleX;
        public readonly ConfigEntry<float> MotionVectorScaleY;
        public readonly ConfigEntry<float> JitterScale;
        public readonly ConfigEntry<bool> FlipJitterX;
        public readonly ConfigEntry<bool> FlipJitterY;
        public readonly ConfigEntry<VrJitterMode> VrJitter;
        public readonly ConfigEntry<HdrInputMode> HdrInput;
        public readonly ConfigEntry<bool> AutoExposure;
        public readonly ConfigEntry<DepthInputSource> DepthSource;
        public readonly ConfigEntry<bool> VerboseLogging;
        public readonly ConfigEntry<bool> UpscaleFlipVertical;

        /// <summary>Per-axis render scale for the selected upscaling mode.</summary>
        public float RenderScale
        {
            get
            {
                switch (Upscaling.Value)
                {
                    case UpscaleMode.UltraQuality: return 1f / 1.2f;
                    case UpscaleMode.Quality: return 1f / 1.5f;
                    case UpscaleMode.Balanced: return 1f / 1.7f;
                    case UpscaleMode.Performance: return 0.5f;
                    case UpscaleMode.Custom: return CustomRenderScale.Value < 0.5f ? 0.5f : (CustomRenderScale.Value > 1f ? 1f : CustomRenderScale.Value);
                    default: return 1f;
                }
            }
        }

        public FsrSettings(ConfigFile config)
        {
            const string general = "General";
            Enabled = config.Bind(general, "Enabled", true,
                "Replace Resonite's TAA anti-aliasing option with FSR 3.1. Select 'TAA' in Resonite's anti-aliasing setting to use it.");
            EnableInVR = config.Bind(general, "EnableInVR", true,
                "Allow the TAA option (and therefore FSR 3.1) in VR. Resonite normally turns TAA off in VR.");

            const string quality = "Quality";
            Upscaling = config.Bind(quality, "Upscaling", UpscaleMode.NativeAA,
                "EXPERIMENTAL: FSR 3.1 VR upscaling. Renders the eyes at a lower resolution (per axis: UltraQuality 83%, Quality 67%, " +
                "Balanced 59%, Performance 50%, Custom = CustomRenderScale) and upscales with FSR 3.1. Changing it causes a brief hitch " +
                "while the eye textures are reallocated. Requires the vrperfkit add-on dxgi.dll, with the vrperfkit section's upscaling " +
                "DISABLED (otherwise two upscalers stack). Desktop always stays at native resolution.");
            CustomRenderScale = config.Bind(quality, "CustomRenderScale", 0.75f,
                new ConfigDescription("Per-axis render scale used when Upscaling = Custom.", new AcceptableValueRange<float>(0.33f, 1f)));
            Sharpening = config.Bind(quality, "Sharpening", true, "Apply FSR's RCAS sharpening pass.");
            Sharpness = config.Bind(quality, "Sharpness", 0.3f,
                new ConfigDescription("RCAS sharpness (0 = none, 1 = maximum).", new AcceptableValueRange<float>(0f, 1f)));
            VelocityFactor = config.Bind(quality, "VelocityFactor", 1.0f,
                new ConfigDescription("How strongly FSR reacts to motion (lower = more temporal stability, more ghosting).", new AcceptableValueRange<float>(0f, 1f)));

            const string vrPerfKit = "vrperfkit FSR 1 (legacy, normally OFF)";
            VrPerfKitUpscaling = config.Bind(vrPerfKit, "Enabled", false,
                "OFF IS NORMAL. FSR 3.1 upscaling (Quality > Upscaling) replaces this and still uses the vrperfkit add-on, so the add-on " +
                "keeps working with this off. Only turn it on to use the older FSR 1 upscaling INSTEAD of FSR 3.1 upscaling (never both). " +
                "Written to Renderer\\vrperfkit_RSF.yml; takes effect the next time Resonite starts.");
            VrPerfKitRenderScale = config.Bind(vrPerfKit, "RenderScale", 60f,
                new ConfigDescription("Percentage of pixels rendered (like SteamVR's resolution %). 60 = ~77% per axis. Takes effect the next time Resonite starts.",
                    new AcceptableValueRange<float>(25f, 100f)));
            VrPerfKitSharpness = config.Bind(vrPerfKit, "Sharpness", 0.3f,
                new ConfigDescription("FSR 1 sharpening strength. Takes effect the next time Resonite starts.", new AcceptableValueRange<float>(0f, 1f)));

            const string motionVectors = "Motion Vectors";
            TreatNoMotionAsCamera = config.Bind(motionVectors, "TreatNoMotionAsCamera", true,
                "Objects whose creator set MotionVectorMode = NoMotion write zero motion, so FSR smears them when you move or turn " +
                "(e.g. decorations in some worlds). This treats them as Camera (correct motion for objects fixed in the world). " +
                "Objects that genuinely move with your view may smear instead; turn this off if that happens. Turning it on also fixes " +
                "already-loaded objects; turning it off applies to objects loaded afterwards (rejoin the world to restore everything).");

            const string foveation = "Foveated Rendering (experimental test)";
            Foveation = config.Bind(foveation, "Mode", FoveationMode.Off,
                "VR only. QuadViews: everything renders at QuadViewOuterScale and a small " +
                "full-resolution inset follows your gaze. MaskCommandBuffer / MaskRenderer: older depth-mask tests (native AA only; measured slower than no foveation).");
            QuadViewOuterScale = config.Bind(foveation, "QuadViewOuterScale", 0.5f,
                new ConfigDescription("QuadViews: per-axis resolution of everything outside the inset (0.5 = 50%). Replaces the Upscaling setting while Mode = QuadViews.",
                    new AcceptableValueRange<float>(0.33f, 0.95f)));
            QuadViewOuterUpscaler = config.Bind(foveation, "QuadViewOuterUpscaler", OuterUpscaler.Bilinear,
                "QuadViews: how the outer view is upscaled. Bilinear = cheapest (no anti-aliasing outside the inset, may shimmer in motion). Fsr = FSR 3.1 (smoother, costs more).");
            QuadViewInsetSize = config.Bind(foveation, "QuadViewInsetSize", 0.3f,
                new ConfigDescription("QuadViews: width of the full-resolution inset as a fraction of the eye image height.", new AcceptableValueRange<float>(0.1f, 0.7f)));
            QuadViewDeadZone = config.Bind(foveation, "QuadViewDeadZone", 0f,
                new ConfigDescription("QuadViews: how far the gaze may move (fraction of the inset size) before the inset re-centres. Larger = steadier image inside the inset, but your gaze can get closer to its edge.",
                    new AcceptableValueRange<float>(0f, 0.4f)));
            QuadViewFeather = config.Bind(foveation, "QuadViewFeather", 0.2f,
                new ConfigDescription("QuadViews: how much of the inset's radius is used to fade into the surrounding image.", new AcceptableValueRange<float>(0.02f, 0.6f)));
            QuadViewShowBorder = config.Bind(foveation, "QuadViewShowBorder", false, "QuadViews: draw a red ring at the inset's edge (to check position and alignment).");
            FoveationFillMode = config.Bind(foveation, "Fill", FoveationFill.Spatial,
                "How skipped pixels are filled before FSR: Spatial (copied from the nearest rendered pixels) or None (holes, for checking the mask).");
            FoveationMaskedReactive = config.Bind(foveation, "MaskedReactive", 0.5f,
                new ConfigDescription("How much FSR trusts the current frame over its history on filled pixels. Higher = less ghosting, softer/blockier periphery.",
                    new AcceptableValueRange<float>(0f, 1f)));
            FoveationGaze = config.Bind(foveation, "Gaze", GazeSource.SteamVR,
                "Gaze point source. SteamVR: read straight from SteamVR (needs an eye-tracking SteamVR driver, e.g. Bigscreen's or Pimax's; lowest delay; falls back to Resonite's eye tracking while SteamVR has none). " +
                "Resonite: the eye tracking the game already receives (e.g. Babble on 8888). " +
                "Baballonia: Baballonia's separate DFR output (enable 'Use DFR', port 9020). Center: fixed foveation.");
            FoveationInnerRadius = config.Bind(foveation, "InnerRadius", 0.18f,
                new ConfigDescription("Full-resolution radius around the gaze point (fraction of eye height).", new AcceptableValueRange<float>(0.05f, 1f)));
            FoveationMidRadius = config.Bind(foveation, "MidRadius", 0.30f,
                new ConfigDescription("Radius where 3 of 4 pixels are still rendered.", new AcceptableValueRange<float>(0.05f, 1.5f)));
            FoveationOuterRadius = config.Bind(foveation, "OuterRadius", 0.42f,
                new ConfigDescription("Radius where 2 of 4 pixels are rendered; beyond it, 1 of 4.", new AcceptableValueRange<float>(0.05f, 2f)));
            BaballoniaGazePort = config.Bind(foveation, "BaballoniaGazePort", 9020, "UDP port Baballonia's DFR output sends to (Gaze = Baballonia only).");
            ResoniteGazePort = config.Bind(foveation, "ResoniteGazePort", 9025,
                "Internal local UDP port the engine-side plugin uses to pass Resonite's eye tracking to the renderer (Gaze = Resonite). Must not be Resonite's own Babble port.");
            GazeInvertPitch = config.Bind(foveation, "GazeInvertPitch", false, "Flip gaze up/down.");
            GazeInvertYaw = config.Bind(foveation, "GazeInvertYaw", false, "Flip gaze left/right.");
            FoveationGazeMode = config.Bind(foveation, "GazeMode", GazeMode.PerEye,
                "PerEye: each eye's circle follows that eye's own direction. SharedPoint: both circles on one point (averaged direction at the convergence distance / GazeDistance).");
            GazeDistance = config.Bind(foveation, "GazeDistance", 0f,
                new ConfigDescription("Distance (m) of the point both eyes' circles are centred on. 0 = automatic from eye convergence (Baballonia), 2 m for Center gaze.",
                    new AcceptableValueRange<float>(0f, 20f)));
            RunFoveationBenchmark = config.Bind(foveation, "RunBenchmark", false,
                "Turn on to run the quad-views benchmark (~40 s, hold your head still): native AA, FSR upscaling, upscaling + inset. Results go to the log and a CSV in Renderer\\BepInEx. " +
                "Turn it off again afterwards.");

            const string reactive = "Reactive Mask";
            ReactiveMask = config.Bind(reactive, "Enabled", true,
                "Auto-generate a reactive mask from the difference between the opaque-only and final image. Reduces ghosting on " +
                "transparent things without motion vectors: UI panels being dragged, glass, particles.");
            ReactiveScale = config.Bind(reactive, "Scale", 0.5f,
                new ConfigDescription("How strongly transparent changes count as reactive.", new AcceptableValueRange<float>(0f, 2f)));
            ReactiveThreshold = config.Bind(reactive, "Threshold", 0.2f,
                new ConfigDescription("Minimum difference before a pixel is treated as reactive.", new AcceptableValueRange<float>(0f, 1f)));
            ReactiveBinaryValue = config.Bind(reactive, "BinaryValue", 0.9f,
                new ConfigDescription("Reactiveness assigned to pixels above the threshold (higher = less history = less ghosting, more aliasing).", new AcceptableValueRange<float>(0f, 1f)));

            const string calibration = "Calibration";
            DebugView = config.Bind(calibration, "DebugView", false,
                "Draw FSR's debug view (motion vectors, depth, reactive masks) instead of the upscaled image.");
            DisableHistory = config.Bind(calibration, "DisableHistory", false,
                "Reset FSR's history every frame. If artifacts disappear with this on, the problem is temporal (motion vectors / jitter).");
            MotionVectorScaleX = config.Bind(calibration, "MotionVectorScaleX", 1.0f, "Multiplier on horizontal motion vectors. Try 0.5 or 2 if moving the head smears horizontally.");
            MotionVectorScaleY = config.Bind(calibration, "MotionVectorScaleY", 1.0f, "Multiplier on vertical motion vectors.");
            JitterScale = config.Bind(calibration, "JitterScale", 1.0f, "Multiplier on the sub-pixel camera jitter. 0 disables jitter (FSR then only does temporal smoothing).");
            FlipJitterX = config.Bind(calibration, "FlipJitterX", false, "Flip the sign of horizontal jitter reported to FSR. Try if a still image shimmers horizontally.");
            FlipJitterY = config.Bind(calibration, "FlipJitterY", false, "Flip the sign of vertical jitter reported to FSR. Try if a still image shimmers vertically.");
            QuadViewFlipVertical = config.Bind(calibration, "QuadViewFlipVertical", false, "QuadViews: flip the inset vertically. Try if the inset shows the scene upside down.");
            QuadViewInsetDepth = config.Bind(calibration, "QuadViewInsetDepth", InsetDepthSource.CameraTarget,
                "QuadViews: where FSR reads the inset's depth. Check with DebugView: the depth panel (top right of the inset grid) should be dark up close and red only far away.");
            QuadViewDumpInset = config.Bind(calibration, "QuadViewDumpInset", false,
                "QuadViews: turn on to save what each inset camera rendered to Renderer\\BepInEx\\ResoniteFSR3-inset-eye*.png and log its camera state (also happens once automatically).");
            VrJitter = config.Bind(calibration, "VrJitter", VrJitterMode.Off,
                "Experimental. ShaderGlobals tries to jitter the VR camera (more detail from FSR). Test: set JitterScale to 30 - if the VR image visibly shakes, it works; set JitterScale back to 1.");
            HdrInput = config.Bind(calibration, "HdrInput", HdrInputMode.Auto, "Whether FSR treats the input as HDR. Auto picks based on the camera's color format.");
            AutoExposure = config.Bind(calibration, "AutoExposure", true, "Let FSR compute its own exposure for HDR input.");
            DepthSource = config.Bind(calibration, "DepthBuffer", DepthInputSource.CameraTargetDepth, "Where FSR reads scene depth from.");
            UpscaleFlipVertical = config.Bind(calibration, "UpscaleFlipVertical", true,
                "VR upscaling: flip the final image to match depth/motion vectors (Unity stores render textures bottom-up), and " +
                "flip it back when handing it to SteamVR. If upscaled VR ghosts badly or is upside down, try toggling this.");
            VerboseLogging = config.Bind(calibration, "VerboseLogging", false, "Log render sizes, formats and per-eye setup whenever FSR (re)initializes.");
        }
    }
}
