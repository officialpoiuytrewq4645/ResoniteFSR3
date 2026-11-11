using System;
using System.Collections.Generic;
using FidelityFX;
using FidelityFX.FSR3;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ResoniteFSR3
{
    /// <summary>
    /// Replaces Post-Processing Stack v2's TAA with FSR 3.1 (native AA: render size == output size).
    ///
    /// Frame flow, per camera using TAA:
    ///   1. OnPreCull: PPv2 asks TAA to jitter the projection -> <see cref="TryConfigureJitter"/> applies FSR's Halton jitter
    ///      as a clip-space translation (same recipe as FSR3Unity), per eye for single-pass stereo.
    ///   2. Image effects: PPv2 calls TAA.Render -> <see cref="TryRender"/> splits each eye out of the (possibly
    ///      double-wide) color/depth/motion buffers, runs a dedicated FSR context per eye, and copies results back.
    ///
    /// Resonite renders VR as single-pass double-wide, where PPv2 treats both eyes as one image (numberOfEyes == 1).
    /// Splitting per eye gives each eye its own FSR history, which Unity's own TAA never had.
    /// </summary>
    internal static class FsrTemporalPass
    {
        private sealed class EyeState : IDisposable
        {
            public Fsr3UpscalerContext Context;
            public RenderTexture Color;
            public RenderTexture Depth;
            public RenderTexture Motion;
            public RenderTexture Output;
            public RenderTexture Opaque;
            public RenderTexture Reactive;
            public int Width;
            public int Height;
            public bool HighDynamicRange;
            public bool AutoExposure;
            public RenderTextureFormat ColorFormat;
            public bool NeedsReset = true;

            public void Dispose()
            {
                Context?.Destroy();
                Context = null;
                DestroyTexture(ref Color);
                DestroyTexture(ref Depth);
                DestroyTexture(ref Motion);
                DestroyTexture(ref Output);
                DestroyTexture(ref Opaque);
                DestroyTexture(ref Reactive);
            }

            internal static void DestroyTexture(ref RenderTexture texture)
            {
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                    texture = null;
                }
            }
        }

        /// <summary>
        /// Copies the camera target right before transparent objects are drawn, giving an "opaque-only" image.
        /// Comparing it with the final image shows where transparents (UI, glass, particles) changed pixels.
        /// The command buffer is recorded for a fixed size/format and rebuilt when those change.
        /// </summary>
        private sealed class OpaqueCapture : IDisposable
        {
            public Camera Camera;
            public CommandBuffer CommandBuffer;
            public RenderTexture Texture;
            public int Width;
            public int Height;
            public RenderTextureFormat Format;
            public int CreatedFrame;

            public void Dispose()
            {
                if (Camera != null && CommandBuffer != null)
                    Camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, CommandBuffer);
                CommandBuffer?.Release();
                CommandBuffer = null;
                EyeState.DestroyTexture(ref Texture);
            }
        }

        private static readonly Dictionary<(int cameraInstanceId, int eyeIndex), EyeState> EyeStates = new Dictionary<(int, int), EyeState>();
        private static readonly Dictionary<int, OpaqueCapture> OpaqueCaptures = new Dictionary<int, OpaqueCapture>();
        private static readonly Fsr3Upscaler.GenerateReactiveDescription GenerateReactiveDescription = new Fsr3Upscaler.GenerateReactiveDescription();
        private static readonly int SourceOpaqueId = Shader.PropertyToID("SourceOpaque");
        private static readonly int EyeOpaqueId = Shader.PropertyToID("EyeOpaque");
        private static readonly Dictionary<int, Vector2> JitterPixelsByCamera = new Dictionary<int, Vector2>();
        private static readonly Fsr3Upscaler.DispatchDescription DispatchDescription = new Fsr3Upscaler.DispatchDescription();
        private static readonly int[] EyeRectParameter = new int[4];

        private static readonly int SourceColorId = Shader.PropertyToID("SourceColor");
        private static readonly int SourceDepthId = Shader.PropertyToID("SourceDepth");
        private static readonly int SourceMotionId = Shader.PropertyToID("SourceMotion");
        private static readonly int EyeColorId = Shader.PropertyToID("EyeColor");
        private static readonly int EyeDepthId = Shader.PropertyToID("EyeDepth");
        private static readonly int EyeMotionId = Shader.PropertyToID("EyeMotion");
        private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int EyeReactiveId = Shader.PropertyToID("EyeReactive");
        private static readonly int FoveaCountersId = Shader.PropertyToID("FoveaCounters");

        private static readonly Action<TemporalAntialiasing, Vector2> SetTaaJitter =
            AccessTools.MethodDelegate<Action<TemporalAntialiasing, Vector2>>(AccessTools.PropertySetter(typeof(TemporalAntialiasing), nameof(TemporalAntialiasing.jitter)));

        private static bool _resetRequested;
        private static bool _failed;
        private static bool _loggedUnsupportedStereoMode;

        private static FsrSettings Settings => ResoniteFsr3Plugin.Settings;

        /// <summary>Whether FSR should take over TAA for this context.</summary>
        public static bool IsActiveFor(PostProcessRenderContext context)
        {
            if (_failed || ResoniteFsr3Plugin.Shaders == null || !Settings.Enabled.Value)
                return false;

            if (!context.stereoActive)
                return true;

            if (!Settings.EnableInVR.Value)
                return false;

            if (context.stereoRenderingMode != PostProcessRenderContext.StereoRenderingMode.SinglePass)
            {
                if (!_loggedUnsupportedStereoMode)
                {
                    ResoniteFsr3Plugin.Log.LogWarning($"Stereo mode {context.stereoRenderingMode} is not supported yet; leaving Unity's TAA in place.");
                    _loggedUnsupportedStereoMode = true;
                }
                return false;
            }

            return true;
        }

        public static bool CanHandleVR => !_failed && ResoniteFsr3Plugin.Shaders != null && Settings.Enabled.Value && Settings.EnableInVR.Value;

        public static void RequestReset()
        {
            _resetRequested = true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Jitter
        // ------------------------------------------------------------------------------------------------------

        private static Vector2 NextJitterPixels(int renderWidth, int displayWidth)
        {
            // FSR's phase count grows with the upscale ratio (8 at native).
            int phaseCount = Fsr3Upscaler.GetJitterPhaseCount(renderWidth, displayWidth);
            Fsr3Upscaler.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, phaseCount);
            float jitterScale = Settings.JitterScale.Value;
            return new Vector2(jitterX * jitterScale, jitterY * jitterScale);
        }

        private static Matrix4x4 JitterTranslation(Vector2 jitterPixels, int renderWidth, int renderHeight)
        {
            return Matrix4x4.Translate(new Vector3(2.0f * jitterPixels.x / renderWidth, 2.0f * jitterPixels.y / renderHeight, 0.0f));
        }

        /// <summary>Mono replacement for TemporalAntialiasing.ConfigureJitteredProjectionMatrix.</summary>
        public static bool TryConfigureJitter(TemporalAntialiasing temporalAntialiasing, PostProcessRenderContext context)
        {
            if (!IsActiveFor(context))
                return false;

            var camera = context.camera;
            int renderWidth = context.width;
            int renderHeight = context.height;
            var jitterPixels = NextJitterPixels(renderWidth, renderWidth);

            // Quad-view inset cameras render with a cropped projection that PPv2 has just reset; put it back
            if (FoveatedInset.TryGetProjection(camera, out var insetProjection))
                camera.projectionMatrix = insetProjection;

            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
            camera.projectionMatrix = JitterTranslation(jitterPixels, renderWidth, renderHeight) * camera.nonJitteredProjectionMatrix;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;

            JitterPixelsByCamera[camera.GetInstanceID()] = jitterPixels;
            SetTaaJitter(temporalAntialiasing, new Vector2(jitterPixels.x / renderWidth, jitterPixels.y / renderHeight));
            return true;
        }

        /// <summary>Single-pass stereo replacement for TemporalAntialiasing.ConfigureStereoJitteredProjectionMatrices.</summary>
        public static bool TryConfigureStereoJitter(TemporalAntialiasing temporalAntialiasing, PostProcessRenderContext context)
        {
            if (!IsActiveFor(context))
                return false;

            var camera = context.camera;
            int eyeWidth = context.screenWidth;
            int eyeHeight = context.screenHeight;

            // Apply this frame's eye-texture scale; the eye textures (context.screenWidth/Height) are the render resolution
            FsrUpscaleController.UpdateForStereoFrame();
            int renderWidth = eyeWidth;
            int renderHeight = eyeHeight;
            int displayWidth = FsrUpscaleController.UpscalingActive
                ? FsrUpscaleController.GetDisplaySize(renderWidth, renderHeight).x
                : renderWidth;

            var jitterPixels = NextJitterPixels(renderWidth, displayWidth);
            var jitterTranslation = JitterTranslation(jitterPixels, renderWidth, renderHeight);

            // Unity 2019 rejects SetStereoProjectionMatrix in single-pass stereo, so the jitter either goes in through the
            // stereo shader globals (experimental) or not at all - in which case FSR must be told the jitter is zero.
            for (var eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
                camera.CopyStereoDeviceProjectionMatrixToNonJittered(eye);
            bool jitterApplied = StereoJitter.Apply(camera, jitterTranslation, Settings.VrJitter.Value == VrJitterMode.ShaderGlobals);
            if (!jitterApplied)
                jitterPixels = Vector2.zero;

            JitterPixelsByCamera[camera.GetInstanceID()] = jitterPixels;
            SetTaaJitter(temporalAntialiasing, new Vector2(jitterPixels.x / renderWidth, jitterPixels.y / renderHeight));

            // Experimental foveation: gaze, mask parameters and mask injection for this frame
            FoveationController.UpdateForStereoFrame(camera, eyeWidth, eyeHeight);
            FoveatedInset.UpdateForStereoFrame(camera);
            return true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Render
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Replacement for TemporalAntialiasing.Render. Returns false to fall back to Unity's TAA.</summary>
        public static bool TryRender(PostProcessRenderContext context)
        {
            if (!IsActiveFor(context))
                return false;

            try
            {
                Render(context);
                return true;
            }
            catch (Exception exception)
            {
                _failed = true;
                ResoniteFsr3Plugin.Log.LogError($"FSR 3.1 failed and has been disabled for this session (falling back to Unity TAA): {exception}");
                FsrUpscaleController.Reset();
                DestroyAll();
                return false;
            }
        }

        private static void Render(PostProcessRenderContext context)
        {
            var camera = context.camera;
            var commandBuffer = context.command;
            int cameraInstanceId = camera.GetInstanceID();

            bool doubleWideStereo = context.stereoActive;
            int eyeCount = doubleWideStereo ? 2 : 1;
            int eyeWidth = doubleWideStereo ? context.screenWidth : context.width;
            int eyeHeight = doubleWideStereo ? context.screenHeight : context.height;

            // The eye textures are the render resolution. In VR upscaling mode they are smaller than the display, and FSR
            // runs at the end of the frame (after post-processing) instead of here.
            int renderWidth = eyeWidth;
            int renderHeight = eyeHeight;
            bool upscaleAtEndOfFrame = doubleWideStereo && FsrUpscaleController.UpscalingActive;
            var displaySize = upscaleAtEndOfFrame ? FsrUpscaleController.GetDisplaySize(eyeWidth, eyeHeight) : new Vector2Int(eyeWidth, eyeHeight);
            int displayWidth = displaySize.x;
            int displayHeight = displaySize.y;

            bool highDynamicRange = ResolveHighDynamicRange(context.sourceFormat);
            bool autoExposure = highDynamicRange && Settings.AutoExposure.Value;
            JitterPixelsByCamera.TryGetValue(cameraInstanceId, out var jitterPixels);
            var reportedJitter = new Vector2(
                Settings.FlipJitterX.Value ? -jitterPixels.x : jitterPixels.x,
                Settings.FlipJitterY.Value ? -jitterPixels.y : jitterPixels.y);

            var library = ResoniteFsr3Plugin.Shaders;
            var splitShader = library.EyeSplitShader;
            int splitKernel = library.EyeSplitKernel;

            RenderTargetIdentifier depthIdentifier;
            var depthSubElement = RenderTextureSubElement.Default;
            switch (Settings.DepthSource.Value)
            {
                case DepthInputSource.BuiltinDepth:
                    depthIdentifier = new RenderTargetIdentifier(BuiltinRenderTextureType.Depth);
                    break;
                case DepthInputSource.CameraDepthTextureGlobal:
                    depthIdentifier = new RenderTargetIdentifier(CameraDepthTextureId);
                    break;
                default:
                    depthIdentifier = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
                    depthSubElement = RenderTextureSubElement.Depth;
                    break;
            }

            // Quad-view inset cameras render into their own texture; their depth comes from a separate setting
            if (FoveatedInset.TryGetInsetTarget(camera, out var insetTarget))
            {
                switch (Settings.QuadViewInsetDepth.Value)
                {
                    case InsetDepthSource.CameraDepthTexture:
                        depthIdentifier = new RenderTargetIdentifier(CameraDepthTextureId);
                        depthSubElement = RenderTextureSubElement.Default;
                        break;
                    case InsetDepthSource.InsetTarget:
                        depthIdentifier = new RenderTargetIdentifier(insetTarget);
                        depthSubElement = RenderTextureSubElement.Depth;
                        break;
                }
            }

            bool resetThisFrame = _resetRequested || Settings.DisableHistory.Value;
            var opaqueCapture = UpdateOpaqueCapture(camera, context.width, context.height, context.sourceFormat);
            bool generateReactive = opaqueCapture != null && opaqueCapture.CreatedFrame < Time.frameCount;
            if (generateReactive)
                splitKernel = library.EyeSplitWithOpaqueKernel;

            bool countSurvivalThisFrame = doubleWideStereo && FoveationController.ShouldCountThisFrame;
            if (countSurvivalThisFrame)
                FoveationController.ResetCountersForFrame();

            for (int eyeIndex = 0; eyeIndex < eyeCount; eyeIndex++)
            {
                var eyeState = GetOrCreateEyeState(cameraInstanceId, eyeIndex, eyeWidth, eyeHeight, highDynamicRange, autoExposure, context);
                int eyeOffsetX = eyeIndex * eyeWidth;

                // 1. Split this eye's region into standalone textures
                commandBuffer.BeginSample("ResoniteFSR3 split eye");
                if (generateReactive)
                {
                    commandBuffer.SetComputeTextureParam(splitShader, splitKernel, SourceOpaqueId, opaqueCapture.Texture);
                    commandBuffer.SetComputeTextureParam(splitShader, splitKernel, EyeOpaqueId, eyeState.Opaque);
                }
                EyeRectParameter[0] = eyeIndex * renderWidth;
                EyeRectParameter[1] = 0;
                EyeRectParameter[2] = renderWidth;
                EyeRectParameter[3] = renderHeight;
                commandBuffer.SetComputeIntParams(splitShader, "EyeRect", EyeRectParameter);
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, SourceColorId, context.source);
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, SourceDepthId, depthIdentifier, 0, depthSubElement);
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, SourceMotionId, new RenderTargetIdentifier(BuiltinRenderTextureType.MotionVectors));
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, EyeColorId, eyeState.Color);
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, EyeDepthId, eyeState.Depth);
                commandBuffer.SetComputeTextureParam(splitShader, splitKernel, EyeMotionId, eyeState.Motion);

                // Foveation fill/survival parameters (the shader always references these resources, so always bind them)
                bool foveated = doubleWideStereo && FoveationController.ActiveThisFrame && !upscaleAtEndOfFrame;
                bool countSurvival = foveated && countSurvivalThisFrame;
                if (foveated)
                    FoveationController.BindComputeParameters(commandBuffer, splitShader, eyeIndex, countSurvival);
                else
                    commandBuffer.SetComputeIntParam(splitShader, "FoveaEnabled", 0);
                commandBuffer.SetComputeBufferParam(splitShader, splitKernel, FoveaCountersId, FoveationController.GetCounterBuffer(countSurvival));

                commandBuffer.DispatchCompute(splitShader, splitKernel, (renderWidth + 7) / 8, (renderHeight + 7) / 8, 1);
                commandBuffer.EndSample("ResoniteFSR3 split eye");
                if (countSurvival && eyeIndex == eyeCount - 1)
                    FoveationController.RequestSurvivalReadback(commandBuffer);

                // 1b. Reactive mask: mark pixels changed by transparents so FSR trusts the current frame there
                if (generateReactive)
                {
                    var reactiveDescription = GenerateReactiveDescription;
                    reactiveDescription.ColorOpaqueOnly = new ResourceView(eyeState.Opaque);
                    reactiveDescription.ColorPreUpscale = new ResourceView(eyeState.Color);
                    reactiveDescription.OutReactive = new ResourceView(eyeState.Reactive);
                    reactiveDescription.RenderSize = new Vector2Int(renderWidth, renderHeight);
                    reactiveDescription.Scale = Settings.ReactiveScale.Value;
                    reactiveDescription.CutoffThreshold = Settings.ReactiveThreshold.Value;
                    reactiveDescription.BinaryValue = Settings.ReactiveBinaryValue.Value;
                    reactiveDescription.Flags = Fsr3Upscaler.GenerateReactiveFlags.ApplyTonemap
                                                | Fsr3Upscaler.GenerateReactiveFlags.ApplyThreshold
                                                | Fsr3Upscaler.GenerateReactiveFlags.UseComponentsMax;
                    eyeState.Context.GenerateReactiveMask(reactiveDescription, commandBuffer);
                }

                // 1c. Foveation: raise the reactive value on filled pixels so FSR doesn't build history from the fill
                float maskedReactive = Settings.FoveationMaskedReactive.Value;
                bool markFoveaReactive = foveated && FoveationController.FillsThisFrame && maskedReactive > 0f;
                if (markFoveaReactive)
                {
                    int markKernel = library.MarkFoveaReactiveKernel;
                    commandBuffer.SetComputeTextureParam(splitShader, markKernel, EyeReactiveId, eyeState.Reactive);
                    commandBuffer.SetComputeIntParam(splitShader, "ReactiveHasBase", generateReactive ? 1 : 0);
                    commandBuffer.SetComputeFloatParam(splitShader, "FoveaReactiveValue", maskedReactive);
                    commandBuffer.DispatchCompute(splitShader, markKernel, (renderWidth + 7) / 8, (renderHeight + 7) / 8, 1);
                }

                if (upscaleAtEndOfFrame)
                {
                    // Upscaling mode: inputs (depth, motion, reactive) are captured above; the color passes through
                    // untouched so bloom/tonemapping run as normal, and FSR upscales the final image at the end of the frame.
                    commandBuffer.CopyTexture(eyeState.Color, 0, 0, 0, 0, eyeWidth, eyeHeight, context.destination, 0, 0, eyeOffsetX, 0);
                    continue;
                }

                // 2. FSR 3.1 for this eye (native AA)
                var dispatch = FillDispatch(camera, doubleWideStereo, eyeIndex, eyeState.Color, eyeState, generateReactive || markFoveaReactive,
                    eyeState.Output, reportedJitter, renderWidth, renderHeight, eyeWidth, eyeHeight, resetThisFrame || eyeState.NeedsReset);
                eyeState.Context.Dispatch(dispatch, commandBuffer);
                eyeState.NeedsReset = false;

                // 3. Copy the result into this eye's region of PPv2's TAA target
                commandBuffer.CopyTexture(eyeState.Output, 0, 0, 0, 0, eyeWidth, eyeHeight, context.destination, 0, 0, eyeOffsetX, 0);
            }

            if (upscaleAtEndOfFrame)
                RecordEndOfFrameUpscale(camera, cameraInstanceId, eyeWidth, eyeHeight, displayWidth, displayHeight, generateReactive, reportedJitter, resetThisFrame);
            else
                RemoveEndOfFrameUpscale(cameraInstanceId);

            _resetRequested = false;
        }

        private static Fsr3Upscaler.DispatchDescription FillDispatch(Camera camera, bool stereo, int eyeIndex, RenderTexture color, EyeState inputs,
            bool useReactive, RenderTexture output, Vector2 jitter, int renderWidth, int renderHeight, int displayWidth, int displayHeight, bool reset)
        {
            var projection = stereo
                ? camera.GetStereoNonJitteredProjectionMatrix((Camera.StereoscopicEye)eyeIndex)
                : camera.nonJitteredProjectionMatrix;

            var dispatch = DispatchDescription;
            dispatch.Color = new ResourceView(color);
            dispatch.Depth = new ResourceView(inputs.Depth);
            dispatch.MotionVectors = new ResourceView(inputs.Motion);
            dispatch.Exposure = ResourceView.Unassigned;
            dispatch.Reactive = useReactive ? new ResourceView(inputs.Reactive) : ResourceView.Unassigned;
            dispatch.TransparencyAndComposition = ResourceView.Unassigned;
            dispatch.Output = new ResourceView(output);
            dispatch.JitterOffset = jitter;
            dispatch.MotionVectorScale = new Vector2(-renderWidth * Settings.MotionVectorScaleX.Value, -renderHeight * Settings.MotionVectorScaleY.Value);
            dispatch.RenderSize = new Vector2Int(renderWidth, renderHeight);
            dispatch.UpscaleSize = new Vector2Int(displayWidth, displayHeight);
            dispatch.EnableSharpening = Settings.Sharpening.Value;
            dispatch.Sharpness = Settings.Sharpness.Value;
            dispatch.FrameTimeDelta = Time.unscaledDeltaTime;
            dispatch.PreExposure = 1.0f;
            dispatch.Reset = reset;
            dispatch.CameraNear = camera.nearClipPlane;
            dispatch.CameraFar = camera.farClipPlane;
            if (SystemInfo.usesReversedZBuffer)
            {
                // FSR expects near/far swapped when depth is inverted
                (dispatch.CameraNear, dispatch.CameraFar) = (dispatch.CameraFar, dispatch.CameraNear);
            }
            dispatch.CameraFovAngleVertical = 2.0f * Mathf.Atan(1.0f / projection.m11);
            dispatch.ViewSpaceToMetersFactor = 1.0f;
            dispatch.VelocityFactor = Settings.VelocityFactor.Value;
            dispatch.Flags = Settings.DebugView.Value ? Fsr3Upscaler.DispatchFlags.DrawDebugView : 0;
            dispatch.UseTextureArrays = false;
            dispatch.EnableAutoReactive = false;
            return dispatch;
        }

        // ------------------------------------------------------------------------------------------------------
        // End-of-frame upscaling (VR, eye textures below display resolution)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Per-eye FSR context for end-of-frame upscaling: LDR (post-tonemap) input, display-size output.</summary>
        private sealed class UpscaleState : IDisposable
        {
            public int EyeIndex;
            public Fsr3UpscalerContext Context;
            public RenderTexture FinalColor;
            public RenderTexture Output;
            public int RenderWidth;
            public int RenderHeight;
            public int DisplayWidth;
            public int DisplayHeight;
            public bool NeedsReset = true;

            public void Dispose()
            {
                FsrUpscaleController.SetOverride(EyeIndex, null);
                Context?.Destroy();
                Context = null;
                EyeState.DestroyTexture(ref FinalColor);
                EyeState.DestroyTexture(ref Output);
            }
        }

        private sealed class EndOfFrameCapture
        {
            public Camera Camera;
            public CommandBuffer CommandBuffer;
        }

        private static readonly Dictionary<(int cameraInstanceId, int eyeIndex), UpscaleState> UpscaleStates = new Dictionary<(int, int), UpscaleState>();
        private static readonly Dictionary<int, EndOfFrameCapture> EndOfFrameCaptures = new Dictionary<int, EndOfFrameCapture>();

        private static void RecordEndOfFrameUpscale(Camera camera, int cameraInstanceId, int renderWidth, int renderHeight,
            int displayWidth, int displayHeight, bool useReactive, Vector2 jitter, bool reset)
        {
            if (!EndOfFrameCaptures.TryGetValue(cameraInstanceId, out var capture) || capture.Camera != camera)
            {
                RemoveEndOfFrameUpscale(cameraInstanceId);
                capture = new EndOfFrameCapture
                {
                    Camera = camera,
                    CommandBuffer = new CommandBuffer { name = "ResoniteFSR3 end-of-frame upscale" },
                };
                camera.AddCommandBuffer(CameraEvent.AfterImageEffects, capture.CommandBuffer);
                EndOfFrameCaptures[cameraInstanceId] = capture;
                ResoniteFsr3Plugin.Log.LogInfo($"FSR 3.1 VR upscaling active for camera '{camera.name}': {renderWidth}x{renderHeight} -> {displayWidth}x{displayHeight} per eye");
            }

            var commandBuffer = capture.CommandBuffer;
            commandBuffer.Clear();

            var library = ResoniteFsr3Plugin.Shaders;
            var splitShader = library.EyeSplitShader;
            int colorOnlyKernel = library.EyeSplitColorOnlyKernel;

            // The final eye texture is top-down while depth/motion come from bottom-up render textures: flip the color
            // to match, and have the submit hook flip the result back for SteamVR.
            bool flipVertical = Settings.UpscaleFlipVertical.Value;
            commandBuffer.SetComputeIntParam(splitShader, "FlipY", flipVertical ? 1 : 0);
            FsrUpscaleController.SetSubmitFlipVertical(flipVertical);

            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                if (!EyeStates.TryGetValue((cameraInstanceId, eyeIndex), out var inputs))
                    continue;
                var upscale = GetOrCreateUpscaleState(cameraInstanceId, eyeIndex, renderWidth, renderHeight, displayWidth, displayHeight);

                // Final (post-processed) color of this eye from the double-wide eye texture
                EyeRectParameter[0] = eyeIndex * renderWidth;
                EyeRectParameter[1] = 0;
                EyeRectParameter[2] = renderWidth;
                EyeRectParameter[3] = renderHeight;
                commandBuffer.SetComputeIntParams(splitShader, "EyeRect", EyeRectParameter);
                commandBuffer.SetComputeTextureParam(splitShader, colorOnlyKernel, SourceColorId, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                commandBuffer.SetComputeTextureParam(splitShader, colorOnlyKernel, EyeColorId, upscale.FinalColor);
                commandBuffer.DispatchCompute(splitShader, colorOnlyKernel, (renderWidth + 7) / 8, (renderHeight + 7) / 8, 1);

                if (FoveatedInset.OuterBilinear)
                {
                    // Quad views with the cheap outer path: stretch the low-resolution eye to display size, no FSR
                    commandBuffer.SetSinglePassStereo(SinglePassStereoMode.None);
                    commandBuffer.Blit(upscale.FinalColor, upscale.Output);
                    commandBuffer.SetSinglePassStereo(SinglePassStereoMode.SideBySide);
                    commandBuffer.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                    upscale.NeedsReset = true;   // FSR history is stale if the user switches back
                }
                else
                {
                    var dispatch = FillDispatch(camera, true, eyeIndex, upscale.FinalColor, inputs, useReactive, upscale.Output, jitter,
                        renderWidth, renderHeight, displayWidth, displayHeight, reset || upscale.NeedsReset);
                    upscale.Context.Dispatch(dispatch, commandBuffer);
                    upscale.NeedsReset = false;
                }

                // Quad views: blend this eye's full-resolution gaze inset over the upscaled image
                FoveatedInset.RecordComposite(commandBuffer, camera, eyeIndex, upscale.Output, flipVertical);

                FsrUpscaleController.SetOverride(eyeIndex, upscale.Output);
            }
        }

        private static void RemoveEndOfFrameUpscale(int cameraInstanceId)
        {
            if (EndOfFrameCaptures.TryGetValue(cameraInstanceId, out var capture))
            {
                if (capture.Camera != null)
                    capture.Camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, capture.CommandBuffer);
                capture.CommandBuffer.Release();
                EndOfFrameCaptures.Remove(cameraInstanceId);
            }

            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                if (UpscaleStates.TryGetValue((cameraInstanceId, eyeIndex), out var upscale))
                {
                    upscale.Dispose();
                    UpscaleStates.Remove((cameraInstanceId, eyeIndex));
                }
            }
        }

        private static UpscaleState GetOrCreateUpscaleState(int cameraInstanceId, int eyeIndex, int renderWidth, int renderHeight, int displayWidth, int displayHeight)
        {
            var key = (cameraInstanceId, eyeIndex);
            if (UpscaleStates.TryGetValue(key, out var existing))
            {
                if (existing.RenderWidth == renderWidth && existing.RenderHeight == renderHeight
                    && existing.DisplayWidth == displayWidth && existing.DisplayHeight == displayHeight)
                    return existing;
                existing.Dispose();
                UpscaleStates.Remove(key);
            }

            var upscale = new UpscaleState
            {
                EyeIndex = eyeIndex,
                RenderWidth = renderWidth,
                RenderHeight = renderHeight,
                DisplayWidth = displayWidth,
                DisplayHeight = displayHeight,
                FinalColor = CreateTexture(renderWidth, renderHeight, RenderTextureFormat.ARGBHalf, $"ResoniteFSR3 eye{eyeIndex} final color"),
                Output = CreateTexture(displayWidth, displayHeight, RenderTextureFormat.ARGBHalf, $"ResoniteFSR3 eye{eyeIndex} upscaled"),
            };
            // Post-tonemap input: LDR shaders, no auto-exposure
            upscale.Context = Fsr3Upscaler.CreateContext(new Vector2Int(displayWidth, displayHeight), new Vector2Int(renderWidth, renderHeight),
                ResoniteFsr3Plugin.Shaders.GetShaders(false), 0);
            UpscaleStates[key] = upscale;
            return upscale;
        }

        private static bool ResolveHighDynamicRange(RenderTextureFormat sourceFormat)
        {
            switch (Settings.HdrInput.Value)
            {
                case HdrInputMode.ForceOn: return true;
                case HdrInputMode.ForceOff: return false;
                default:
                    return sourceFormat == RenderTextureFormat.ARGBHalf
                        || sourceFormat == RenderTextureFormat.ARGBFloat
                        || sourceFormat == RenderTextureFormat.RGB111110Float
                        || sourceFormat == RenderTextureFormat.DefaultHDR;
            }
        }

        private static EyeState GetOrCreateEyeState(int cameraInstanceId, int eyeIndex, int width, int height, bool highDynamicRange, bool autoExposure, PostProcessRenderContext context)
        {
            var key = (cameraInstanceId, eyeIndex);
            var colorFormat = context.sourceFormat;

            if (EyeStates.TryGetValue(key, out var existing))
            {
                if (existing.Width == width && existing.Height == height && existing.HighDynamicRange == highDynamicRange
                    && existing.AutoExposure == autoExposure && existing.ColorFormat == colorFormat)
                {
                    return existing;
                }

                existing.Dispose();
                EyeStates.Remove(key);
            }

            var flags = (Fsr3Upscaler.InitializationFlags)0;
            if (highDynamicRange) flags |= Fsr3Upscaler.InitializationFlags.EnableHighDynamicRange;
            if (autoExposure) flags |= Fsr3Upscaler.InitializationFlags.EnableAutoExposure;

            var eyeState = new EyeState
            {
                Width = width,
                Height = height,
                HighDynamicRange = highDynamicRange,
                AutoExposure = autoExposure,
                ColorFormat = colorFormat,
                Color = CreateTexture(width, height, colorFormat, $"ResoniteFSR3 eye{eyeIndex} color"),
                Depth = CreateTexture(width, height, RenderTextureFormat.RFloat, $"ResoniteFSR3 eye{eyeIndex} depth"),
                Motion = CreateTexture(width, height, RenderTextureFormat.RGHalf, $"ResoniteFSR3 eye{eyeIndex} motion"),
                Output = CreateTexture(width, height, colorFormat, $"ResoniteFSR3 eye{eyeIndex} output"),
                Opaque = CreateTexture(width, height, colorFormat, $"ResoniteFSR3 eye{eyeIndex} opaque"),
                Reactive = CreateTexture(width, height, RenderTextureFormat.R8, $"ResoniteFSR3 eye{eyeIndex} reactive"),
            };
            var size = new Vector2Int(width, height);
            eyeState.Context = Fsr3Upscaler.CreateContext(size, size, ResoniteFsr3Plugin.Shaders.GetShaders(highDynamicRange), flags);
            EyeStates[key] = eyeState;

            if (Settings.VerboseLogging.Value)
            {
                ResoniteFsr3Plugin.Log.LogInfo(
                    $"FSR 3.1 context for camera '{context.camera.name}' eye {eyeIndex}: {width}x{height}, format {colorFormat}, HDR {highDynamicRange}, " +
                    $"auto-exposure {autoExposure}, stereo {context.stereoActive} ({context.stereoRenderingMode}), context size {context.width}x{context.height}, " +
                    $"screen size {context.screenWidth}x{context.screenHeight}, renderingPath {context.camera.actualRenderingPath}, allowHDR {context.camera.allowHDR}, " +
                    $"reversedZ {SystemInfo.usesReversedZBuffer}, colorSpace {QualitySettings.activeColorSpace}");
            }

            return eyeState;
        }

        private static RenderTexture CreateTexture(int width, int height, RenderTextureFormat format, string name)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.Create();
            return texture;
        }

        /// <summary>
        /// Keeps the opaque-only capture for this camera in sync with the reactive mask setting and frame size.
        /// Returns null when reactive masking is off or unavailable.
        /// </summary>
        private static OpaqueCapture UpdateOpaqueCapture(Camera camera, int width, int height, RenderTextureFormat format)
        {
            int cameraInstanceId = camera.GetInstanceID();
            OpaqueCaptures.TryGetValue(cameraInstanceId, out var capture);

            if (!Settings.ReactiveMask.Value)
            {
                if (capture != null)
                {
                    capture.Dispose();
                    OpaqueCaptures.Remove(cameraInstanceId);
                }
                return null;
            }

            if (capture != null && capture.Camera == camera && capture.Width == width && capture.Height == height && capture.Format == format)
                return capture;

            capture?.Dispose();

            capture = new OpaqueCapture
            {
                Camera = camera,
                Width = width,
                Height = height,
                Format = format,
                CreatedFrame = Time.frameCount,
                Texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
                {
                    name = "ResoniteFSR3 opaque-only",
                    useMipMap = false,
                    autoGenerateMips = false,
                },
                CommandBuffer = new CommandBuffer { name = "ResoniteFSR3 capture opaque-only" },
            };
            capture.Texture.Create();
            capture.CommandBuffer.CopyTexture(BuiltinRenderTextureType.CameraTarget, capture.Texture);
            camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, capture.CommandBuffer);
            OpaqueCaptures[cameraInstanceId] = capture;

            if (Settings.VerboseLogging.Value)
                ResoniteFsr3Plugin.Log.LogInfo($"Opaque-only capture for camera '{camera.name}': {width}x{height} {format} (reactive mask active from next frame)");

            return capture;
        }

        /// <summary>Frees the FSR state kept for a camera that no longer exists.</summary>
        public static void ReleaseCamera(int cameraInstanceId)
        {
            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                if (EyeStates.TryGetValue((cameraInstanceId, eyeIndex), out var eyeState))
                {
                    eyeState.Dispose();
                    EyeStates.Remove((cameraInstanceId, eyeIndex));
                }
            }
            if (OpaqueCaptures.TryGetValue(cameraInstanceId, out var capture))
            {
                capture.Dispose();
                OpaqueCaptures.Remove(cameraInstanceId);
            }
            JitterPixelsByCamera.Remove(cameraInstanceId);
        }

        public static void DestroyAll()
        {
            FoveatedInset.DestroyAll();
            foreach (var eyeState in EyeStates.Values)
                eyeState.Dispose();
            EyeStates.Clear();
            foreach (var capture in OpaqueCaptures.Values)
                capture.Dispose();
            OpaqueCaptures.Clear();
            foreach (var cameraInstanceId in new List<int>(EndOfFrameCaptures.Keys))
                RemoveEndOfFrameUpscale(cameraInstanceId);
            foreach (var upscale in UpscaleStates.Values)
                upscale.Dispose();
            UpscaleStates.Clear();
            JitterPixelsByCamera.Clear();
            StereoJitter.DestroyAll();
        }
    }
}
