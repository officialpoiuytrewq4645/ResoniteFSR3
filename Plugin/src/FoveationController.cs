using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace ResoniteFSR3
{
    /// <summary>
    /// Experimental graded foveated rendering, built as a set of switchable test pathways plus an automatic benchmark.
    ///
    /// Mask: a depth mask written at the near plane over a growing share of pixels away from the gaze point, so the
    /// scene's geometry fails the depth test there (skipping G-buffer, lighting, skybox and transparents). Two injection
    /// pathways are compared: a camera command buffer at BeforeGBuffer, and an invisible full-screen renderer drawn first
    /// in the G-buffer pass. The skipped pixels rotate every frame so FSR's history can rebuild full detail.
    ///
    /// Fill: before FSR runs, skipped pixels get color/depth/motion from unmasked neighbours (spatial) or from last
    /// frame's FSR output reprojected by motion vectors (temporal). See resonitefsr3_eye_split.compute.
    /// </summary>
    internal static class FoveationController
    {
        private static readonly int FoveaCenterLeftId = Shader.PropertyToID("FoveaCenterLeft");
        private static readonly int FoveaCenterRightId = Shader.PropertyToID("FoveaCenterRight");
        private static readonly int FoveaRadiiId = Shader.PropertyToID("FoveaRadii");
        private static readonly int FoveaFrameId = Shader.PropertyToID("FoveaFrame");
        private static readonly int FoveaTargetSizeId = Shader.PropertyToID("FoveaTargetSize");
        private static readonly int FoveaMaskDepthId = Shader.PropertyToID("FoveaMaskDepth");

        private static Material _maskMaterial;
        private static BaballoniaGazeInput _gazeInput;

        // Per-frame state shared with the eye-split pass
        public static bool ActiveThisFrame { get; private set; }
        public static FoveationFill FillThisFrame { get; private set; }
        public static bool FillsThisFrame => ActiveThisFrame && FillThisFrame != FoveationFill.None;
        public static Vector4 CenterLeft { get; private set; } = new Vector4(0.5f, 0.5f, 0, 0);
        public static Vector4 CenterRight { get; private set; } = new Vector4(0.5f, 0.5f, 0, 0);
        public static Vector4 Radii { get; private set; }
        public static int Frame { get; private set; }
        public static float MaskDepth => SystemInfo.usesReversedZBuffer ? 1f : 0f;

        // Benchmark overrides (null = use settings)
        private static FoveationMode? _modeOverride;
        private static FoveationFill? _fillOverride;

        private static FoveationMode EffectiveMode => _modeOverride ?? ResoniteFsr3Plugin.Settings.Foveation.Value;
        public static bool QuadViewsWanted => EffectiveMode == FoveationMode.QuadViews;
        private static FoveationFill EffectiveFill => _fillOverride ?? ResoniteFsr3Plugin.Settings.FoveationFillMode.Value;

        // ------------------------------------------------------------------------------------------------------
        // Per-frame setup (called from the stereo jitter configuration in OnPreCull)
        // ------------------------------------------------------------------------------------------------------

        public static void UpdateForStereoFrame(Camera camera, int eyeWidth, int eyeHeight)
        {
            var mode = EffectiveMode;
            // The depth-mask modes only work at native resolution; quad views only needs the gaze centers from here
            bool maskMode = mode == FoveationMode.MaskCommandBuffer || mode == FoveationMode.MaskRenderer;
            bool canRun = maskMode && !FsrUpscaleController.UpscalingActive && EnsureMaterial();
            ActiveThisFrame = canRun;
            FillThisFrame = EffectiveFill;

            UpdateInjection(camera, canRun ? mode : FoveationMode.Off, eyeWidth, eyeHeight);
            if (mode == FoveationMode.Off)
                return;

            var settings = ResoniteFsr3Plugin.Settings;
            Frame = Time.frameCount & 3;
            Radii = new Vector4(settings.FoveationInnerRadius.Value, settings.FoveationMidRadius.Value, settings.FoveationOuterRadius.Value,
                eyeHeight > 0 ? (float)eyeWidth / eyeHeight : 1f);

            // One shared fixation point in head space, projected into each eye, so both circles cover the same spot in
            // the world (per-eye parallel rays would only line up at infinity and drift apart for anything nearby).
            var gazeDirection = new Vector3(0, 0, -1);
            float gazeDistance = DefaultGazeDistance;

            // SteamVR hands back each eye's gaze point already in that eye's screen coordinates. When it has no valid
            // gaze, fall back to Resonite's own eye tracking so the inset keeps following the eyes.
            bool steamVrGazeValid = false;
            Vector2 steamVrLeft = default, steamVrRight = default;
            if (settings.FoveationGaze.Value == GazeSource.SteamVR)
                steamVrGazeValid = SteamVrGazeInput.TryGetCenters(out steamVrLeft, out steamVrRight);
            GazeSource networkSource = settings.FoveationGaze.Value == GazeSource.SteamVR ? GazeSource.Resonite : settings.FoveationGaze.Value;
            bool networkGaze = !steamVrGazeValid && (networkSource == GazeSource.Baballonia || networkSource == GazeSource.Resonite);
            if (networkGaze && TryGetGaze(camera, networkSource, out var trackedDirection, out var trackedDistance))
            {
                gazeDirection = trackedDirection;
                gazeDistance = trackedDistance;
            }
            if (settings.GazeDistance.Value > 0f)
                gazeDistance = settings.GazeDistance.Value;
            Vector3 fixationPointHead = gazeDirection.normalized * gazeDistance;
            Vector2 leftCenter = ProjectToEyeUv(camera, Camera.StereoscopicEye.Left, fixationPointHead);
            Vector2 rightCenter = ProjectToEyeUv(camera, Camera.StereoscopicEye.Right, fixationPointHead);

            // PerEye: each circle follows its own eye's ray (from that eye's position), no distance estimate needed
            if (settings.FoveationGazeMode.Value == GazeMode.PerEye && networkGaze
                && TryGetEyeViewDirection(networkSource, 0, out var leftDirection)
                && TryGetEyeViewDirection(networkSource, 1, out var rightDirection))
            {
                leftCenter = ProjectEyeDirectionToUv(camera, Camera.StereoscopicEye.Left, leftDirection);
                rightCenter = ProjectEyeDirectionToUv(camera, Camera.StereoscopicEye.Right, rightDirection);
            }

            if (steamVrGazeValid)
            {
                leftCenter = steamVrLeft;
                rightCenter = steamVrRight;
            }
            CenterLeft = new Vector4(leftCenter.x, leftCenter.y, 0, 0);
            CenterRight = new Vector4(rightCenter.x, rightCenter.y, 0, 0);

            // Globals for the depth-mask shader (both injection pathways)
            Shader.SetGlobalVector(FoveaCenterLeftId, CenterLeft);
            Shader.SetGlobalVector(FoveaCenterRightId, CenterRight);
            Shader.SetGlobalVector(FoveaRadiiId, Radii);
            Shader.SetGlobalInt(FoveaFrameId, Frame);
            Shader.SetGlobalVector(FoveaTargetSizeId, new Vector4(eyeWidth * 2, eyeHeight, eyeWidth, 0));
            Shader.SetGlobalFloat(FoveaMaskDepthId, MaskDepth);
        }

        /// <summary>Binds the foveation parameters for the eye-split compute shader on a command buffer.</summary>
        public static void BindComputeParameters(CommandBuffer commandBuffer, ComputeShader shader, int eyeIndex, bool countSurvival)
        {
            commandBuffer.SetComputeIntParam(shader, "FoveaEnabled", ActiveThisFrame ? 1 : 0);
            commandBuffer.SetComputeIntParam(shader, "FoveaFillMode", ActiveThisFrame ? (int)FillThisFrame : 0);
            commandBuffer.SetComputeIntParam(shader, "FoveaCount", ActiveThisFrame && countSurvival ? 1 : 0);
            commandBuffer.SetComputeIntParam(shader, "FoveaEyeIndex", eyeIndex);
            commandBuffer.SetComputeFloatParam(shader, "FoveaMaskDepth", MaskDepth);
            commandBuffer.SetComputeVectorParam(shader, "FoveaCenterLeft", CenterLeft);
            commandBuffer.SetComputeVectorParam(shader, "FoveaCenterRight", CenterRight);
            commandBuffer.SetComputeVectorParam(shader, "FoveaRadii", Radii);
            commandBuffer.SetComputeIntParam(shader, "FoveaFrame", Frame);
        }

        private static bool EnsureMaterial()
        {
            if (_maskMaterial != null)
                return true;
            var shader = ResoniteFsr3Plugin.Shaders?.FoveationMaskShader;
            if (shader == null)
                return false;
            _maskMaterial = new Material(shader) { name = "ResoniteFSR3 foveation mask", hideFlags = HideFlags.DontSave };
            return true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Gaze
        // ------------------------------------------------------------------------------------------------------

        private const float DefaultGazeDistance = 2f;
        private const float MinimumGazeDistance = 0.25f;
        private const float MaximumGazeDistance = 20f;
        private static float _smoothedGazeDistance = DefaultGazeDistance;

        /// <summary>Gaze direction (head view space) and fixation distance from eye convergence (smoothed).</summary>
        private static bool TryGetGaze(Camera camera, GazeSource source, out Vector3 viewDirection, out float fixationDistance)
        {
            viewDirection = new Vector3(0, 0, -1);
            fixationDistance = _smoothedGazeDistance;
            var settings = ResoniteFsr3Plugin.Settings;
            int port = source == GazeSource.Resonite ? settings.ResoniteGazePort.Value : settings.BaballoniaGazePort.Value;
            if (_gazeInput == null || _gazeInput.Port != port)
            {
                _gazeInput?.Dispose();
                _gazeInput = new BaballoniaGazeInput(port);
                _gazeInput.Start();
            }

            if (source == GazeSource.Resonite)
            {
                if (!_gazeInput.TryGetDirectionGaze(out float directionX, out float directionY, out float directionZ, out float reportedDistance))
                    return false;
                if (directionZ <= 0.05f)
                    return false;
                if (settings.GazeInvertYaw.Value) directionX = -directionX;
                if (settings.GazeInvertPitch.Value) directionY = -directionY;
                // Resonite head space looks down +Z, Unity view space down -Z
                viewDirection = new Vector3(directionX, directionY, -directionZ);
                float resoniteMeasuredDistance = reportedDistance > 0f
                    ? Mathf.Clamp(reportedDistance, MinimumGazeDistance, MaximumGazeDistance)
                    : _smoothedGazeDistance;
                _smoothedGazeDistance = 1f / Mathf.Lerp(1f / _smoothedGazeDistance, 1f / resoniteMeasuredDistance, 0.15f);
                fixationDistance = _smoothedGazeDistance;
                return true;
            }

            if (!_gazeInput.TryGetGaze(out float pitchDegrees, out float yawDegrees, out float vergenceDegrees))
                return false;

            // Distance at which the two eye rays cross: (IPD / 2) / tan(vergence / 2). Eye-tracker vergence is noisy, so
            // clamp and smooth it (in inverse distance, which is roughly linear in the measured angle).
            float interpupillaryDistance = Vector3.Distance(
                camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.MultiplyPoint(Vector3.zero),
                camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.MultiplyPoint(Vector3.zero));
            if (interpupillaryDistance < 0.04f || interpupillaryDistance > 0.09f)
                interpupillaryDistance = 0.063f;
            float halfVergenceTangent = Mathf.Tan(vergenceDegrees * 0.5f * Mathf.Deg2Rad);
            float measuredDistance = halfVergenceTangent > 1e-4f
                ? Mathf.Clamp(interpupillaryDistance * 0.5f / halfVergenceTangent, MinimumGazeDistance, MaximumGazeDistance)
                : MaximumGazeDistance;
            float smoothedInverse = Mathf.Lerp(1f / _smoothedGazeDistance, 1f / measuredDistance, 0.15f);
            _smoothedGazeDistance = 1f / smoothedInverse;
            fixationDistance = _smoothedGazeDistance;

            // Baballonia: pitch positive = looking down; yaw positive = looking right
            float upDegrees = settings.GazeInvertPitch.Value ? pitchDegrees : -pitchDegrees;
            float rightDegrees = settings.GazeInvertYaw.Value ? -yawDegrees : yawDegrees;
            // Unity view space looks down -Z
            viewDirection = new Vector3(Mathf.Tan(rightDegrees * Mathf.Deg2Rad), Mathf.Tan(upDegrees * Mathf.Deg2Rad), -1f);
            return true;
        }

        /// <summary>One eye's tracked direction in head view space (Unity convention, looking down -Z).</summary>
        private static bool TryGetEyeViewDirection(GazeSource source, int eyeIndex, out Vector3 viewDirection)
        {
            viewDirection = new Vector3(0, 0, -1);
            if (_gazeInput == null)
                return false;
            var settings = ResoniteFsr3Plugin.Settings;
            if (source == GazeSource.Resonite)
            {
                if (!_gazeInput.TryGetEyeDirection(eyeIndex, out float directionX, out float directionY, out float directionZ) || directionZ <= 0.05f)
                    return false;
                if (settings.GazeInvertYaw.Value) directionX = -directionX;
                if (settings.GazeInvertPitch.Value) directionY = -directionY;
                viewDirection = new Vector3(directionX, directionY, -directionZ);
                return true;
            }

            if (!_gazeInput.TryGetEyePitchYaw(eyeIndex, out float pitchDegrees, out float yawDegrees))
                return false;
            float upDegrees = settings.GazeInvertPitch.Value ? pitchDegrees : -pitchDegrees;
            float rightDegrees = settings.GazeInvertYaw.Value ? -yawDegrees : yawDegrees;
            viewDirection = new Vector3(Mathf.Tan(rightDegrees * Mathf.Deg2Rad), Mathf.Tan(upDegrees * Mathf.Deg2Rad), -1f);
            return true;
        }

        /// <summary>Projects a direction (head view space) cast from one eye's own position into that eye's UV.</summary>
        private static Vector2 ProjectEyeDirectionToUv(Camera camera, Camera.StereoscopicEye eye, Vector3 directionHeadView)
        {
            // Rotate head-space direction into the eye's view space (handles canted displays), then project a far point
            Vector3 directionWorld = camera.cameraToWorldMatrix.MultiplyVector(directionHeadView.normalized);
            Vector3 directionEyeView = camera.GetStereoViewMatrix(eye).MultiplyVector(directionWorld);
            var projection = camera.GetStereoNonJitteredProjectionMatrix(eye);
            Vector3 farPoint = directionEyeView * 100f;
            Vector4 clip = projection * new Vector4(farPoint.x, farPoint.y, farPoint.z, 1f);
            if (clip.w <= 1e-5f)
                return new Vector2(0.5f, 0.5f);
            return new Vector2(Mathf.Clamp01(clip.x / clip.w * 0.5f + 0.5f), Mathf.Clamp01(clip.y / clip.w * 0.5f + 0.5f));
        }

        /// <summary>Projects a point in head (center camera) view space into one eye's UV.</summary>
        private static Vector2 ProjectToEyeUv(Camera camera, Camera.StereoscopicEye eye, Vector3 pointHeadView)
        {
            Vector3 pointWorld = camera.cameraToWorldMatrix.MultiplyPoint(pointHeadView);
            Vector3 pointEyeView = camera.GetStereoViewMatrix(eye).MultiplyPoint(pointWorld);
            var projection = camera.GetStereoNonJitteredProjectionMatrix(eye);
            Vector4 clip = projection * new Vector4(pointEyeView.x, pointEyeView.y, pointEyeView.z, 1f);
            if (Mathf.Abs(clip.w) < 1e-5f)
                return new Vector2(0.5f, 0.5f);
            var ndc = new Vector2(clip.x / clip.w, clip.y / clip.w);
            return new Vector2(Mathf.Clamp01(ndc.x * 0.5f + 0.5f), Mathf.Clamp01(ndc.y * 0.5f + 0.5f));
        }

        // ------------------------------------------------------------------------------------------------------
        // Mask injection pathways
        // ------------------------------------------------------------------------------------------------------

        private sealed class InjectionState
        {
            public Camera Camera;
            public FoveationMode Mode;
            public int EyeWidth;
            public int EyeHeight;
            public CommandBuffer CommandBuffer;
            public GameObject MaskObject;
        }

        private static readonly Dictionary<int, InjectionState> Injections = new Dictionary<int, InjectionState>();

        private static void UpdateInjection(Camera camera, FoveationMode mode, int eyeWidth, int eyeHeight)
        {
            int cameraInstanceId = camera.GetInstanceID();
            Injections.TryGetValue(cameraInstanceId, out var state);
            if (state != null && state.Camera == camera && state.Mode == mode && state.EyeWidth == eyeWidth && state.EyeHeight == eyeHeight)
                return;

            RemoveInjection(state);
            Injections.Remove(cameraInstanceId);
            if (mode == FoveationMode.Off)
                return;

            state = new InjectionState { Camera = camera, Mode = mode, EyeWidth = eyeWidth, EyeHeight = eyeHeight };
            if (mode == FoveationMode.MaskCommandBuffer)
            {
                var commandBuffer = new CommandBuffer { name = "ResoniteFSR3 foveation depth mask" };
                // Draw one full-screen triangle over the whole double-wide target, outside single-pass stereo duplication
                commandBuffer.SetSinglePassStereo(SinglePassStereoMode.None);
                commandBuffer.SetViewport(new Rect(0, 0, eyeWidth * 2, eyeHeight));
                commandBuffer.DrawProcedural(Matrix4x4.identity, _maskMaterial, 0, MeshTopology.Triangles, 3);
                commandBuffer.SetSinglePassStereo(SinglePassStereoMode.SideBySide);
                camera.AddCommandBuffer(CameraEvent.BeforeGBuffer, commandBuffer);
                state.CommandBuffer = commandBuffer;
            }
            else if (mode == FoveationMode.MaskRenderer)
            {
                var maskObject = new GameObject("ResoniteFSR3 foveation mask") { hideFlags = HideFlags.DontSave };
                maskObject.transform.SetParent(camera.transform, false);
                maskObject.layer = FirstRenderedLayer(camera);
                var mesh = new Mesh { name = "ResoniteFSR3 full-screen triangle", hideFlags = HideFlags.DontSave };
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // never frustum-culled
                maskObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = maskObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _maskMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                state.MaskObject = maskObject;
            }

            Injections[cameraInstanceId] = state;
            ResoniteFsr3Plugin.Log.LogInfo($"Foveation mask via {mode} on camera '{camera.name}' ({eyeWidth}x{eyeHeight} per eye)");
        }

        private static int FirstRenderedLayer(Camera camera)
        {
            for (int layer = 0; layer < 32; layer++)
            {
                if ((camera.cullingMask & (1 << layer)) != 0)
                    return layer;
            }
            return 0;
        }

        private static void RemoveInjection(InjectionState state)
        {
            if (state == null)
                return;
            if (state.CommandBuffer != null)
            {
                if (state.Camera != null)
                    state.Camera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, state.CommandBuffer);
                state.CommandBuffer.Release();
            }
            if (state.MaskObject != null)
                UnityEngine.Object.Destroy(state.MaskObject);
        }

        // ------------------------------------------------------------------------------------------------------
        // Mask survival (reliability): share of masked pixels whose depth still equals the mask depth after rendering
        // ------------------------------------------------------------------------------------------------------

        private static ComputeBuffer _survivalCounters;
        private static ComputeBuffer _dummyCounters;
        private static readonly uint[] ZeroCounters = new uint[2];
        private static long _survivalMasked;
        private static long _survivalKept;

        /// <summary>Counter buffer to bind this frame (always bound, since the shader references it).</summary>
        public static ComputeBuffer GetCounterBuffer(bool countThisFrame)
        {
            if (_dummyCounters == null)
                _dummyCounters = new ComputeBuffer(2, sizeof(uint));
            if (!countThisFrame)
                return _dummyCounters;
            if (_survivalCounters == null)
                _survivalCounters = new ComputeBuffer(2, sizeof(uint));
            return _survivalCounters;
        }

        /// <summary>Whether this frame should count mask survival (every 15th frame while active).</summary>
        public static bool ShouldCountThisFrame => ActiveThisFrame && Time.frameCount % 15 == 0;

        public static void ResetCountersForFrame()
        {
            _survivalCounters?.SetData(ZeroCounters);
        }

        public static void RequestSurvivalReadback(CommandBuffer commandBuffer)
        {
            commandBuffer.RequestAsyncReadback(_survivalCounters, request =>
            {
                if (request.hasError)
                    return;
                var counters = request.GetData<uint>();
                _survivalMasked += counters[0];
                _survivalKept += counters[1];
            });
        }

        private static void ResetSurvivalStats()
        {
            _survivalMasked = 0;
            _survivalKept = 0;
        }

        private static double SurvivalPercent => _survivalMasked > 0 ? 100.0 * _survivalKept / _survivalMasked : double.NaN;

        // ------------------------------------------------------------------------------------------------------
        // Benchmark
        // ------------------------------------------------------------------------------------------------------

        private sealed class BenchmarkTest
        {
            public string Name;
            public FoveationMode Mode;
            public FoveationFill Fill;
            public readonly List<float> GpuMilliseconds = new List<float>();
            public uint DroppedFrames;
            public double SurvivalPercent = double.NaN;
            public long MaskedPixelsSampled;
            public int SkippedFrames;   // dashboard open or eye texture below full resolution
            public float Scale = 1f;    // render scale for this test (1 = native AA)
            public OuterUpscaler? Outer;   // quad views: which outer upscaler this test uses
            public readonly List<float> FrameIntervalMilliseconds = new List<float>();   // real time between frames (shows CPU limits)
        }

        private const int WarmupFrames = 120;
        private const int MeasureFrames = 450;
        private const float StartDelaySeconds = 10f;   // time to close the SteamVR dashboard after toggling the setting

        private static List<BenchmarkTest> _benchmarkTests;
        private static int _benchmarkIndex = -1;
        private static int _benchmarkFrame;
        private static bool _benchmarkTriggerWasOn;
        private static uint _lastFrameIndex;
        private static float _benchmarkStartAt;
        private static int _maximumEyeTextureWidth;

        /// <summary>Called every frame from the plugin's Update.</summary>
        public static void Tick()
        {
            bool triggerOn = ResoniteFsr3Plugin.Settings.RunFoveationBenchmark.Value;
            if (triggerOn && !_benchmarkTriggerWasOn && _benchmarkIndex < 0)
                StartBenchmark();
            _benchmarkTriggerWasOn = triggerOn;

            if (_benchmarkIndex < 0)
                return;

            if (Time.unscaledTime < _benchmarkStartAt)
                return;

            var test = _benchmarkTests[_benchmarkIndex];
            _benchmarkFrame++;
            if (_benchmarkFrame == WarmupFrames)
                ResetSurvivalStats();

            if (_benchmarkFrame > WarmupFrames)
            {
                SampleFrameTiming(test);
                test.FrameIntervalMilliseconds.Add(Time.unscaledDeltaTime * 1000f);
            }

            // Run until enough valid (full-resolution, dashboard closed) samples, with a cap so a test can't hang forever
            bool enoughSamples = test.GpuMilliseconds.Count >= MeasureFrames;
            bool tooLong = _benchmarkFrame >= WarmupFrames + MeasureFrames * 4;
            if (enoughSamples || tooLong)
            {
                test.SurvivalPercent = SurvivalPercent;
                test.MaskedPixelsSampled = _survivalMasked;
                ResoniteFsr3Plugin.Log.LogInfo($"Foveation benchmark: finished {test.Name}: {Summarize(test)}");
                _benchmarkIndex++;
                if (_benchmarkIndex >= _benchmarkTests.Count)
                    FinishBenchmark();
                else
                    BeginTest(_benchmarkTests[_benchmarkIndex]);
            }
        }

        private static void StartBenchmark()
        {
            // Plain upscaling runs at the Upscaling setting (67% if that is NativeAA); quad views at its own outer scale.
            // The useful comparison is whether quad views at a low outer scale beats plain upscaling at a scale you'd accept.
            float upscaleScale = ResoniteFsr3Plugin.Settings.RenderScale < 0.999f ? ResoniteFsr3Plugin.Settings.RenderScale : 0.667f;
            float quadScale = ResoniteFsr3Plugin.Settings.QuadViewOuterScale.Value;
            _benchmarkTests = new List<BenchmarkTest>
            {
                new BenchmarkTest { Name = "T0 native AA (100%)", Mode = FoveationMode.Off, Fill = FoveationFill.None, Scale = 1f },
                new BenchmarkTest { Name = $"T1 FSR upscaling ({upscaleScale * 100f:0}%)", Mode = FoveationMode.Off, Fill = FoveationFill.None, Scale = upscaleScale },
                new BenchmarkTest { Name = $"T2 quad views ({quadScale * 100f:0}% FSR outer + inset)", Mode = FoveationMode.QuadViews, Fill = FoveationFill.None, Scale = quadScale, Outer = OuterUpscaler.Fsr },
                new BenchmarkTest { Name = $"T3 quad views ({quadScale * 100f:0}% bilinear outer + inset)", Mode = FoveationMode.QuadViews, Fill = FoveationFill.None, Scale = quadScale, Outer = OuterUpscaler.Bilinear },
                new BenchmarkTest { Name = "T4 native AA again (drift check)", Mode = FoveationMode.Off, Fill = FoveationFill.None, Scale = 1f },
            };
            _benchmarkIndex = 0;
            _benchmarkStartAt = Time.unscaledTime + StartDelaySeconds;
            _maximumEyeTextureWidth = 0;
            ResoniteFsr3Plugin.Log.LogInfo($"Foveation benchmark starts in {StartDelaySeconds:0} s (close the SteamVR dashboard): {_benchmarkTests.Count} tests, " +
                $"~{(WarmupFrames + MeasureFrames) * _benchmarkTests.Count / 75} s at 75 Hz. Hold your head still.");
            BeginTest(_benchmarkTests[0]);
        }

        private static void BeginTest(BenchmarkTest test)
        {
            _modeOverride = test.Mode;
            _fillOverride = test.Fill;
            FsrUpscaleController.ScaleOverride = test.Scale;
            FoveatedInset.OuterUpscalerOverride = test.Outer;
            _maximumEyeTextureWidth = 0;   // each test has its own expected eye texture size
            _benchmarkFrame = 0;
            _lastFrameIndex = 0;
            ResetSurvivalStats();
            FsrTemporalPass.RequestReset();
            ResoniteFsr3Plugin.Log.LogInfo($"Foveation benchmark: running {test.Name}");
        }

        private static void SampleFrameTiming(BenchmarkTest test)
        {
            try
            {
                var compositor = OpenVR.Compositor;
                if (compositor == null)
                    return;
                var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
                if (!compositor.GetFrameTiming(ref timing, 0) || timing.m_nFrameIndex == _lastFrameIndex)
                    return;
                _lastFrameIndex = timing.m_nFrameIndex;

                // Discard frames rendered at reduced resolution (SteamVR lowers it while the dashboard is open)
                int eyeTextureWidth = UnityEngine.XR.XRSettings.eyeTextureWidth;
                _maximumEyeTextureWidth = Math.Max(_maximumEyeTextureWidth, eyeTextureWidth);
                bool dashboardOpen = OpenVR.Overlay != null && OpenVR.Overlay.IsDashboardVisible();
                if (dashboardOpen || eyeTextureWidth < _maximumEyeTextureWidth)
                {
                    test.SkippedFrames++;
                    return;
                }
                test.GpuMilliseconds.Add(timing.m_flTotalRenderGpuMs);
                test.DroppedFrames += timing.m_nNumDroppedFrames;
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"Frame timing unavailable: {exception.Message}");
            }
        }

        private static string Summarize(BenchmarkTest test)
        {
            if (test.GpuMilliseconds.Count == 0)
                return "no GPU timing samples";
            var sorted = test.GpuMilliseconds.OrderBy(value => value).ToList();
            float mean = sorted.Average();
            float median = sorted[sorted.Count / 2];
            float p90 = sorted[(int)(sorted.Count * 0.9)];
            float frameInterval = test.FrameIntervalMilliseconds.Count > 0 ? test.FrameIntervalMilliseconds.Average() : float.NaN;
            return $"GPU mean {mean:0.00} ms, median {median:0.00} ms, p90 {p90:0.00} ms ({sorted.Count} frames), frame interval {frameInterval:0.00} ms ({1000f / frameInterval:0} fps), dropped {test.DroppedFrames}, skipped {test.SkippedFrames}";
        }

        private static void FinishBenchmark()
        {
            _modeOverride = null;
            _fillOverride = null;
            FsrUpscaleController.ScaleOverride = null;
            FoveatedInset.OuterUpscalerOverride = null;
            _benchmarkIndex = -1;

            // Baseline: T0, or T6 if T0 got no valid samples
            var baseline = _benchmarkTests[0].GpuMilliseconds.Count > 0
                ? _benchmarkTests[0].GpuMilliseconds
                : _benchmarkTests[_benchmarkTests.Count - 1].GpuMilliseconds;
            float baselineMean = baseline.Count > 0 ? baseline.Average() : float.NaN;
            var report = new StringBuilder();
            report.AppendLine("test,gpu_mean_ms,gpu_median_ms,gpu_p90_ms,frames,dropped,skipped,frame_interval_mean_ms,saving_vs_baseline_ms");
            ResoniteFsr3Plugin.Log.LogInfo("===== Foveation benchmark results =====");
            foreach (var test in _benchmarkTests)
            {
                var sorted = test.GpuMilliseconds.OrderBy(value => value).ToList();
                float mean = sorted.Count > 0 ? sorted.Average() : float.NaN;
                float median = sorted.Count > 0 ? sorted[sorted.Count / 2] : float.NaN;
                float p90 = sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.9)] : float.NaN;
                float frameInterval = test.FrameIntervalMilliseconds.Count > 0 ? test.FrameIntervalMilliseconds.Average() : float.NaN;
                report.AppendLine(string.Join(",", test.Name, mean.ToString("0.000"), median.ToString("0.000"), p90.ToString("0.000"), sorted.Count,
                    test.DroppedFrames, test.SkippedFrames, frameInterval.ToString("0.000"), (baselineMean - mean).ToString("0.000")));
                ResoniteFsr3Plugin.Log.LogInfo($"  {test.Name,-36} {Summarize(test)}  | saving vs baseline {(baselineMean - mean):0.00} ms");
            }

            try
            {
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"ResoniteFSR3-foveation-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                File.WriteAllText(path, report.ToString());
                ResoniteFsr3Plugin.Log.LogInfo($"Foveation benchmark CSV: {path}. Turn RunBenchmark off again.");
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"Could not write benchmark CSV: {exception.Message}");
            }
            FsrTemporalPass.RequestReset();
        }

        public static void DestroyAll()
        {
            foreach (var state in Injections.Values)
                RemoveInjection(state);
            Injections.Clear();
            _gazeInput?.Dispose();
            _gazeInput = null;
            _survivalCounters?.Release();
            _survivalCounters = null;
            _dummyCounters?.Release();
            _dummyCounters = null;
        }
    }
}
