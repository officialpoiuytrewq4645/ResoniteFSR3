using System;
using System.IO;
using AmplifyOcclusion;
using HarmonyLib;
using Renderite.Shared;
using Renderite.Unity;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;

namespace ResoniteFSR3
{
    /// <summary>
    /// Experimental "quad views" foveated rendering.
    ///
    /// The normal VR camera renders both eyes at a reduced resolution and the outer view is upscaled (bilinear or FSR).
    /// On top of that, one extra camera per eye renders a small region around the gaze point at full display pixel
    /// density. Each inset camera sits at its eye's position and is rotated to look along that eye's gaze, with an
    /// ordinary symmetric projection: the camera's default projection then is exactly what it renders with, so PPv2's
    /// projection resets, Unity's motion vectors and FSR's jitter all behave as on any normal camera. (An earlier version
    /// cropped the eye's off-centre projection instead; PPv2 reset it after every render, which corrupted the motion
    /// vectors and made FSR discard its history.) The inset is then blended over the upscaled eye image through a
    /// perspective-correct quad (see the InsetComposite shader).
    ///
    /// The insets are rendered from the main camera's pre-cull callback, so they use exactly the same head pose and
    /// Resonite render context as the main view.
    /// </summary>
    internal static class FoveatedInset
    {
        private sealed class EyeInset
        {
            public GameObject GameObject;
            public Camera Camera;
            public RenderTexture Target;
            public Material CompositeMaterial;
            public int Size;
            public Vector2 GazePixel;           // gaze point the inset is centred on, in native eye pixels (y up)
            public bool HasGaze;
            public int RenderedFrame = -1;
            public int RenderCount;
            // This frame's inset camera basis in the eye's view space, and its tangent half-size
            public Vector3 Forward;
            public Vector3 Right;
            public Vector3 Up;
            public float TangentHalfSize;
            public PostProcessLayer PostProcessLayer;
            public AmplifyOcclusionEffect AmbientOcclusion;
        }

        /// <summary>
        /// The cropped projection an inset camera must render with. PPv2 resets a camera's projection matrix when TAA is
        /// active, so the jitter setup re-applies this first.
        /// </summary>
        /// <summary>The render texture an inset camera draws into (null if the camera is not an inset).</summary>
        public static bool TryGetInsetTarget(Camera camera, out RenderTexture target)
        {
            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                var inset = Insets[eyeIndex];
                if (inset != null && inset.Camera == camera && inset.Target != null)
                {
                    target = inset.Target;
                    return true;
                }
            }
            target = null;
            return false;
        }

        /// <summary>Insets use their camera's own (symmetric) projection; nothing needs re-applying.</summary>
        public static bool TryGetProjection(Camera camera, out Matrix4x4 projection)
        {
            projection = Matrix4x4.identity;
            return false;
        }

        /// <summary>
        /// Copies the main camera's current post-processing state (anti-aliasing, bloom, SSR, ambient occlusion) onto an
        /// inset. Resonite does not keep its graphics settings after applying them, so the main camera is the source.
        /// </summary>
        private static void SyncPostProcessing(Camera mainCamera, EyeInset inset)
        {
            var mainLayer = mainCamera.GetComponent<PostProcessLayer>();
            var insetLayer = inset.PostProcessLayer;
            if (mainLayer != null && insetLayer != null)
            {
                insetLayer.enabled = mainLayer.enabled;
                insetLayer.antialiasingMode = mainLayer.antialiasingMode;
                insetLayer.finalBlitToCameraTarget = mainLayer.finalBlitToCameraTarget;
                insetLayer.subpixelMorphologicalAntialiasing.quality = mainLayer.subpixelMorphologicalAntialiasing.quality;

                var mainProfile = mainLayer.defaultProfile;
                var insetProfile = insetLayer.defaultProfile;
                if (mainProfile != null && insetProfile != null)
                {
                    if (mainProfile.TryGetSettings(out Bloom mainBloom) && insetProfile.TryGetSettings(out Bloom insetBloom))
                    {
                        insetBloom.enabled.value = mainBloom.enabled.value;
                        insetBloom.intensity.value = mainBloom.intensity.value;
                    }
                    if (mainProfile.TryGetSettings(out ScreenSpaceReflections mainReflections) && insetProfile.TryGetSettings(out ScreenSpaceReflections insetReflections))
                        insetReflections.enabled.value = mainReflections.enabled.value;
                    if (insetProfile.TryGetSettings(out MotionBlur insetMotionBlur))
                        insetMotionBlur.enabled.value = false;   // the main VR view never has motion blur
                }
            }

            var mainOcclusion = mainCamera.GetComponent<AmplifyOcclusionEffect>();
            if (mainOcclusion != null && inset.AmbientOcclusion != null)
            {
                inset.AmbientOcclusion.enabled = mainOcclusion.enabled;
                inset.AmbientOcclusion.Intensity = mainOcclusion.Intensity;
            }
        }

        private static readonly EyeInset[] Insets = new EyeInset[2];
        private static readonly Camera.CameraCallback PreCullHandler = OnCameraPreCull;
        private static readonly Camera.CameraCallback PostRenderHandler = OnCameraPostRender;
        private static readonly int InsetTexId = Shader.PropertyToID("_InsetTex");
        private static readonly int CornerPixelId = Shader.PropertyToID("_CornerPixel");
        private static readonly int InsetTargetId = Shader.PropertyToID("_InsetTarget");
        private static readonly int InsetFeatherId = Shader.PropertyToID("_InsetFeather");
        private static readonly Vector4[] CornerPixels = new Vector4[4];

        private static Camera _mainCamera;
        private static bool _hooked;
        private static bool _rendering;
        private static bool _failed;
        private static int _rectFrame = -1;
        private static bool _dumpSettingWasOn;
        private const int AutomaticDumpAfterRenders = 90;
        private static Vector2Int _nativeSize;

        private static FsrSettings Settings => ResoniteFsr3Plugin.Settings;

        /// <summary>Quad views needs the outer view to be upscaled (otherwise there is nothing to save).</summary>
        public static bool Wanted =>
            !_failed && FoveationController.QuadViewsWanted && FsrUpscaleController.UpscalingActive
            && ResoniteFsr3Plugin.Shaders?.InsetCompositeShader != null;

        /// <summary>Benchmark override for the outer upscaler (null = use the setting).</summary>
        public static OuterUpscaler? OuterUpscalerOverride;

        /// <summary>True when the outer view should skip FSR and be stretched bilinearly instead.</summary>
        public static bool OuterBilinear => Wanted && (OuterUpscalerOverride ?? Settings.QuadViewOuterUpscaler.Value) == OuterUpscaler.Bilinear;

        public static bool ActiveThisFrame => Insets[0] != null && Insets[0].RenderedFrame == Time.frameCount;

        /// <summary>Called for every stereo frame from the main camera's jitter setup.</summary>
        public static void UpdateForStereoFrame(Camera mainCamera)
        {
            _mainCamera = mainCamera;
            if (!Wanted)
            {
                if (_hooked || Insets[0] != null || Insets[1] != null)
                    DestroyAll();
                return;
            }

            // Re-append every frame so these run after Resonite's own camera callbacks
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, PreCullHandler);
            Camera.onPreCull = (Camera.CameraCallback)Delegate.Combine(Camera.onPreCull, PreCullHandler);
            Camera.onPostRender = (Camera.CameraCallback)Delegate.Remove(Camera.onPostRender, PostRenderHandler);
            Camera.onPostRender = (Camera.CameraCallback)Delegate.Combine(Camera.onPostRender, PostRenderHandler);
            _hooked = true;
        }

        private static int InsetIndexOf(Camera camera)
        {
            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                if (Insets[eyeIndex] != null && Insets[eyeIndex].Camera == camera)
                    return eyeIndex;
            }
            return -1;
        }

        /// <summary>
        /// The inset cameras are ordinary enabled cameras that Unity renders itself, just before the main camera (lower
        /// depth). Rendering them manually with Camera.Render() left Unity's per-camera "previous frame" matrices stale,
        /// so their motion vectors were garbage and FSR could not use its history.
        /// </summary>
        private static void OnCameraPreCull(Camera camera)
        {
            if (_rendering || !Wanted || _mainCamera == null)
                return;

            _rendering = true;
            try
            {
                int insetIndex = InsetIndexOf(camera);
                if (insetIndex >= 0)
                {
                    // Same Resonite render context the main view uses (Resonite only opens it for its own camera)
                    RenderContextHelper.BeginRenderContext(RenderingContext.UserView);
                    ConfigureInsetCamera(_mainCamera, insetIndex);
                }
                else if (camera == _mainCamera)
                {
                    // Create / resize the insets (they render from the next frame) and run diagnostics
                    EnsureFrameRects(_mainCamera);
                    bool dumpSettingOn = Settings.QuadViewDumpInset.Value;
                    bool dumpNow = Insets[0] != null && ((dumpSettingOn && !_dumpSettingWasOn) || Insets[0].RenderCount == AutomaticDumpAfterRenders);
                    _dumpSettingWasOn = dumpSettingOn;
                    if (dumpNow)
                    {
                        for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
                            DumpDiagnostics(_mainCamera, eyeIndex);
                    }
                }
            }
            catch (Exception exception)
            {
                _failed = true;
                ResoniteFsr3Plugin.Log.LogError($"Quad views failed and has been disabled for this session: {exception}");
                DestroyAll();
            }
            finally
            {
                _rendering = false;
            }
        }

        private static void OnCameraPostRender(Camera camera)
        {
            int insetIndex = InsetIndexOf(camera);
            if (insetIndex < 0)
                return;
            var inset = Insets[insetIndex];
            inset.RenderedFrame = Time.frameCount;
            inset.RenderCount++;
            RenderContextHelper.EndCurrentRenderContext();
        }

        /// <summary>
        /// Decides this frame's inset size, gaze point and camera orientation for both eyes. Shared by the inset cameras
        /// and the composite, whichever runs first in the frame, so all use the same values.
        /// </summary>
        private static bool EnsureFrameRects(Camera mainCamera)
        {
            if (_rectFrame == Time.frameCount)
                return _nativeSize.x > 0;
            _rectFrame = Time.frameCount;

            _nativeSize = FsrUpscaleController.GetDisplaySize(XRSettings.eyeTextureWidth, XRSettings.eyeTextureHeight);
            if (_nativeSize.x < 64 || _nativeSize.y < 64)
            {
                _nativeSize = Vector2Int.zero;
                return false;
            }

            int maximumSize = Mathf.Min(_nativeSize.x, _nativeSize.y);
            int size = Mathf.Clamp(Mathf.RoundToInt(_nativeSize.y * Settings.QuadViewInsetSize.Value), 128, maximumSize) & ~7;
            float deadZonePixels = size * Settings.QuadViewDeadZone.Value;

            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                var inset = EnsureInset(eyeIndex, mainCamera, size);
                Vector4 center = eyeIndex == 0 ? FoveationController.CenterLeft : FoveationController.CenterRight;
                var gazePixel = new Vector2(Mathf.Clamp01(center.x) * _nativeSize.x, Mathf.Clamp01(center.y) * _nativeSize.y);
                if (!inset.HasGaze || (gazePixel - inset.GazePixel).magnitude > deadZonePixels)
                {
                    inset.GazePixel = gazePixel;
                    inset.HasGaze = true;
                }

                // Gaze ray in the eye's view space (looking down -Z): invert the eye projection on the z = -1 plane
                Matrix4x4 projection = mainCamera.GetStereoProjectionMatrix((Camera.StereoscopicEye)eyeIndex);
                float gazeNdcX = inset.GazePixel.x / _nativeSize.x * 2f - 1f;
                float gazeNdcY = inset.GazePixel.y / _nativeSize.y * 2f - 1f;
                Vector3 forward = new Vector3((gazeNdcX + projection.m02) / projection.m00, (gazeNdcY + projection.m12) / projection.m11, -1f).normalized;
                Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
                Vector3 up = Vector3.Cross(right, forward);
                inset.Forward = forward;
                inset.Right = right;
                inset.Up = up;

                // Field of view matching the eye image's pixel density at its centre. Kept fixed (not adjusted per gaze
                // angle) so the projection PPv2 and FSR see never changes between frames.
                float tangentX = size / (_nativeSize.x * projection.m00);
                float tangentY = size / (_nativeSize.y * projection.m11);
                inset.TangentHalfSize = 0.5f * (tangentX + tangentY);
            }
            return true;
        }

        /// <summary>Points an inset camera along its eye's gaze for this frame (called from that camera's pre-cull).</summary>
        private static void ConfigureInsetCamera(Camera mainCamera, int eyeIndex)
        {
            if (!EnsureFrameRects(mainCamera))
                return;

            var inset = Insets[eyeIndex];
            var eye = (Camera.StereoscopicEye)eyeIndex;
            var insetCamera = inset.Camera;

            // Keep per-frame camera state in step with the main camera
            insetCamera.nearClipPlane = mainCamera.nearClipPlane;
            insetCamera.farClipPlane = mainCamera.farClipPlane;
            insetCamera.cullingMask = mainCamera.cullingMask;
            insetCamera.clearFlags = mainCamera.clearFlags;
            insetCamera.backgroundColor = mainCamera.backgroundColor;
            insetCamera.aspect = 1f;
            float fieldOfView = 2f * Mathf.Atan(inset.TangentHalfSize) * Mathf.Rad2Deg;
            if (Mathf.Abs(insetCamera.fieldOfView - fieldOfView) > 1e-4f)
                insetCamera.fieldOfView = fieldOfView;

            // World -> inset view: the eye's view, then rotated so the inset looks along the gaze.
            // Rows are the inset's right, up and back axes expressed in the eye's view space.
            Matrix4x4 eyeView = mainCamera.GetStereoViewMatrix(eye);
            Matrix4x4 rotation = Matrix4x4.identity;
            rotation.SetRow(0, new Vector4(inset.Right.x, inset.Right.y, inset.Right.z, 0f));
            rotation.SetRow(1, new Vector4(inset.Up.x, inset.Up.y, inset.Up.z, 0f));
            rotation.SetRow(2, new Vector4(-inset.Forward.x, -inset.Forward.y, -inset.Forward.z, 0f));
            Matrix4x4 insetView = rotation * eyeView;

            Matrix4x4 eyeToWorld = eyeView.inverse;
            Vector3 worldForward = eyeToWorld.MultiplyVector(inset.Forward);
            Vector3 worldUp = eyeToWorld.MultiplyVector(inset.Up);
            if (worldForward.sqrMagnitude > 1e-12f && worldUp.sqrMagnitude > 1e-12f)
                inset.GameObject.transform.SetPositionAndRotation(eyeToWorld.MultiplyPoint(Vector3.zero), Quaternion.LookRotation(worldForward, worldUp));
            insetCamera.worldToCameraMatrix = insetView;
            SyncPostProcessing(mainCamera, inset);
        }

        /// <summary>Saves the inset camera's image and logs its state, to check it against the main view.</summary>
        private static void DumpDiagnostics(Camera mainCamera, int eyeIndex)
        {
            var inset = Insets[eyeIndex];
            var insetCamera = inset.Camera;
            var eye = (Camera.StereoscopicEye)eyeIndex;
            var log = ResoniteFsr3Plugin.Log;
            try
            {
                var postProcessLayer = inset.GameObject.GetComponent<PostProcessLayer>();
                var profile = postProcessLayer != null ? postProcessLayer.defaultProfile : null;
                MotionBlur motionBlur = null;
                Bloom bloom = null;
                if (profile != null)
                {
                    profile.TryGetSettings(out motionBlur);
                    profile.TryGetSettings(out bloom);
                }
                Vector4 center = eyeIndex == 0 ? FoveationController.CenterLeft : FoveationController.CenterRight;
                log.LogInfo($"Inset eye {eyeIndex}: gaze pixel {inset.GazePixel}, size {inset.Size}, native {_nativeSize}, gaze uv ({center.x:0.000},{center.y:0.000}), " +
                            $"forward {inset.Forward}, fov {inset.Camera.fieldOfView:0.00} deg, renders {inset.RenderCount}");
                log.LogInfo($"Inset eye {eyeIndex}: main pos {mainCamera.transform.position}, inset pos {inset.GameObject.transform.position}, " +
                            $"near {insetCamera.nearClipPlane} far {insetCamera.farClipPlane}, physical {insetCamera.usePhysicalProperties}, hdr {insetCamera.allowHDR}, " +
                            $"path {insetCamera.actualRenderingPath}, mask 0x{insetCamera.cullingMask:X} (main 0x{mainCamera.cullingMask:X}), clear {insetCamera.clearFlags}, " +
                            $"depthMode {insetCamera.depthTextureMode}, context {RenderContextHelper.CurrentRenderingContext?.ToString() ?? "none"}");
                var mainPostProcessLayer = mainCamera.GetComponent<PostProcessLayer>();
                log.LogInfo($"Inset eye {eyeIndex}: main post layer {(mainPostProcessLayer != null ? $"enabled {mainPostProcessLayer.enabled}, AA {mainPostProcessLayer.antialiasingMode}" : "MISSING")}");
                log.LogInfo($"Inset eye {eyeIndex}: post layer {(postProcessLayer != null ? $"enabled {postProcessLayer.enabled}, AA {postProcessLayer.antialiasingMode}" : "MISSING")}, " +
                            $"motion blur {(motionBlur != null ? motionBlur.enabled.value.ToString() : "n/a")}, bloom {(bloom != null ? $"{bloom.enabled.value} x{bloom.intensity.value}" : "n/a")}");
                log.LogInfo($"Inset eye {eyeIndex}: main stereo view\n{mainCamera.GetStereoViewMatrix(eye)}main stereo projection\n{mainCamera.GetStereoProjectionMatrix(eye)}" +
                            $"inset view\n{insetCamera.worldToCameraMatrix}inset projection (after render)\n{insetCamera.projectionMatrix}inset non-jittered\n{insetCamera.nonJitteredProjectionMatrix}");

                // Convert the half-float inset to 8-bit sRGB and save it
                var temporary = RenderTexture.GetTemporary(inset.Size, inset.Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(inset.Target, temporary);
                var previousActive = RenderTexture.active;
                RenderTexture.active = temporary;
                var readback = new Texture2D(inset.Size, inset.Size, UnityEngine.TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, inset.Size, inset.Size), 0, 0);
                readback.Apply();
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(temporary);
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"ResoniteFSR3-inset-eye{eyeIndex}.png");
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(readback));
                UnityEngine.Object.Destroy(readback);
                log.LogInfo($"Inset eye {eyeIndex}: image saved to {path}");
            }
            catch (Exception exception)
            {
                log.LogWarning($"Inset diagnostics failed: {exception}");
            }
        }

        private static EyeInset EnsureInset(int eyeIndex, Camera mainCamera, int size)
        {
            var inset = Insets[eyeIndex];
            if (inset != null && inset.Camera == null)
            {
                DestroyInset(eyeIndex);
                inset = null;
            }

            if (inset == null)
            {
                inset = new EyeInset();
                inset.GameObject = new GameObject($"ResoniteFSR3 quad-view inset {(eyeIndex == 0 ? "left" : "right")}") { hideFlags = HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(inset.GameObject);

                var insetCamera = inset.GameObject.AddComponent<Camera>();
                insetCamera.CopyFrom(mainCamera);
                insetCamera.depth = mainCamera.depth - 1f;         // Unity renders it itself, just before the main camera
                insetCamera.enabled = true;
                insetCamera.stereoTargetEye = StereoTargetEyeMask.None;
                insetCamera.rect = new Rect(0, 0, 1, 1);
                insetCamera.allowHDR = true;
                insetCamera.allowMSAA = false;
                insetCamera.usePhysicalProperties = false;
                insetCamera.ResetProjectionMatrix();       // ordinary symmetric projection from fieldOfView/aspect
                inset.Camera = insetCamera;

                // Same setup Resonite gives its primary VR camera (IsVR also keeps motion blur off, like the main view)
                var renderingManager = RenderingManager.Instance;
                renderingManager.CameraInitializer.RegisterCamera(insetCamera);
                var postProcessingManager = inset.GameObject.AddComponent<CameraPostprocessingManager>();
                postProcessingManager.Initialize(insetCamera, new CameraSettings
                {
                    SetupPostProcessing = true,
                    IsPrimary = true,
                    IsVR = true,
                });
                // Current settings (anti-aliasing, bloom, ...) are copied from the main camera every frame in SyncPostProcessing
                inset.PostProcessLayer = inset.GameObject.GetComponent<PostProcessLayer>();
                inset.AmbientOcclusion = inset.GameObject.GetComponent<AmplifyOcclusionEffect>();

                inset.CompositeMaterial = new Material(ResoniteFsr3Plugin.Shaders.InsetCompositeShader)
                {
                    name = "ResoniteFSR3 inset composite",
                    hideFlags = HideFlags.DontSave,
                };
                Insets[eyeIndex] = inset;
                ResoniteFsr3Plugin.Log.LogInfo($"Quad views: created inset camera for eye {eyeIndex} ({size}x{size}, native eye {_nativeSize.x}x{_nativeSize.y}).");
            }

            if (inset.Target == null || inset.Size != size)
            {
                if (inset.Target != null)
                {
                    inset.Camera.targetTexture = null;
                    inset.Target.Release();
                    UnityEngine.Object.Destroy(inset.Target);
                }
                inset.Target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = $"ResoniteFSR3 inset eye{eyeIndex}",
                    useMipMap = false,
                    hideFlags = HideFlags.DontSave,
                };
                inset.Target.Create();
                inset.Camera.targetTexture = inset.Target;
                inset.Size = size;
                inset.HasGaze = false;
            }
            return inset;
        }

        /// <summary>
        /// Appends the blend of this eye's inset over the upscaled eye image to the end-of-frame command buffer.
        /// targetIsBottomUp: the upscaled image is stored like a Unity render texture (memory row 0 = image bottom).
        /// </summary>
        public static void RecordComposite(CommandBuffer commandBuffer, Camera mainCamera, int eyeIndex, RenderTexture upscaledOutput, bool targetIsBottomUp)
        {
            if (!Wanted || mainCamera != _mainCamera)
                return;
            var inset = Insets[eyeIndex];
            // The insets are rendered at pre-cull of this same frame; allow for either callback order
            if (inset == null || inset.Target == null || Time.frameCount - inset.RenderedFrame > 1)
                return;
            if (!EnsureFrameRects(mainCamera) || upscaledOutput.width != _nativeSize.x || upscaledOutput.height != _nativeSize.y)
                return;

            // Project the inset's four corner rays into the eye image. Unity render textures keep memory row 0 = image
            // bottom, so a bottom-up target uses the y-up pixel row directly.
            Matrix4x4 eyeProjection = mainCamera.GetStereoProjectionMatrix((Camera.StereoscopicEye)eyeIndex);
            for (int corner = 0; corner < 4; corner++)
            {
                float signX = (corner & 1) != 0 ? 1f : -1f;
                float signY = (corner & 2) != 0 ? 1f : -1f;
                Vector3 ray = inset.Forward + inset.Right * (signX * inset.TangentHalfSize) + inset.Up * (signY * inset.TangentHalfSize);
                Vector4 clip = eyeProjection * new Vector4(ray.x, ray.y, ray.z, 1f);
                float clipW = Mathf.Max(clip.w, 1e-4f);
                float pixelX = (clip.x / clipW * 0.5f + 0.5f) * _nativeSize.x;
                float pixelY = (clip.y / clipW * 0.5f + 0.5f) * _nativeSize.y;
                float memoryY = targetIsBottomUp ? pixelY : _nativeSize.y - pixelY;
                CornerPixels[corner] = new Vector4(pixelX, memoryY, clipW, 0f);
            }

            bool readFlipped = !targetIsBottomUp;
            if (Settings.QuadViewFlipVertical.Value)
                readFlipped = !readFlipped;

            var material = inset.CompositeMaterial;
            material.SetTexture(InsetTexId, inset.Target);
            material.SetVectorArray(CornerPixelId, CornerPixels);
            material.SetVector(InsetTargetId, new Vector4(_nativeSize.x, _nativeSize.y, readFlipped ? 1f : 0f, Settings.QuadViewShowBorder.Value ? 1f : 0f));
            material.SetFloat(InsetFeatherId, Settings.QuadViewFeather.Value);

            commandBuffer.SetSinglePassStereo(SinglePassStereoMode.None);
            commandBuffer.SetRenderTarget(upscaledOutput);
            commandBuffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 6);
            commandBuffer.SetSinglePassStereo(SinglePassStereoMode.SideBySide);
            commandBuffer.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
        }

        private static void DestroyInset(int eyeIndex)
        {
            var inset = Insets[eyeIndex];
            if (inset == null)
                return;
            Insets[eyeIndex] = null;

            int cameraInstanceId = inset.Camera != null ? inset.Camera.GetInstanceID() : 0;
            if (inset.Camera != null)
                inset.Camera.targetTexture = null;
            if (inset.GameObject != null)
                UnityEngine.Object.Destroy(inset.GameObject);   // CameraPostprocessingManager.OnDestroy removes its post-processing
            if (inset.Target != null)
            {
                inset.Target.Release();
                UnityEngine.Object.Destroy(inset.Target);
            }
            if (inset.CompositeMaterial != null)
                UnityEngine.Object.Destroy(inset.CompositeMaterial);
            if (cameraInstanceId != 0)
                FsrTemporalPass.ReleaseCamera(cameraInstanceId);
        }

        public static void DestroyAll()
        {
            if (_hooked)
            {
                Camera.onPreCull = (Camera.CameraCallback)Delegate.Remove(Camera.onPreCull, PreCullHandler);
                Camera.onPostRender = (Camera.CameraCallback)Delegate.Remove(Camera.onPostRender, PostRenderHandler);
                _hooked = false;
            }
            DestroyInset(0);
            DestroyInset(1);
        }
    }
}
