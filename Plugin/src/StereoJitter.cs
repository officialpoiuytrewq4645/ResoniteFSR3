using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ResoniteFSR3
{
    /// <summary>
    /// Experimental VR jitter for single-pass stereo. Unity 2019 refuses custom eye projections in this mode, so this
    /// writes jittered unity_StereoMatrixP / unity_StereoMatrixVP straight into the shader globals from camera command
    /// buffers, right before each scene pass. Whether Unity keeps these values (rather than re-uploading its own) has to be
    /// verified visually: with a large JitterScale the image should shake.
    /// </summary>
    internal static class StereoJitter
    {
        private static readonly CameraEvent[] InjectionEvents =
        {
            CameraEvent.BeforeGBuffer,
            CameraEvent.BeforeForwardOpaque,
            CameraEvent.BeforeSkybox,
            CameraEvent.BeforeForwardAlpha,
        };

        private sealed class CameraState
        {
            public Camera Camera;
            public CommandBuffer CommandBuffer;
        }

        private static readonly Dictionary<int, CameraState> States = new Dictionary<int, CameraState>();
        private static readonly Matrix4x4[] Projections = new Matrix4x4[2];
        private static readonly Matrix4x4[] ViewProjections = new Matrix4x4[2];
        private static readonly int StereoMatrixPId = Shader.PropertyToID("unity_StereoMatrixP");
        private static readonly int StereoMatrixVPId = Shader.PropertyToID("unity_StereoMatrixVP");

        /// <summary>Returns true if jitter was injected for this frame.</summary>
        public static bool Apply(Camera camera, Matrix4x4 jitterTranslation, bool enabled)
        {
            int cameraInstanceId = camera.GetInstanceID();
            States.TryGetValue(cameraInstanceId, out var state);
            if (!enabled)
            {
                if (state != null)
                    Remove(cameraInstanceId, state);
                return false;
            }

            if (state == null)
            {
                state = new CameraState { Camera = camera, CommandBuffer = new CommandBuffer { name = "ResoniteFSR3 stereo jitter" } };
                foreach (var cameraEvent in InjectionEvents)
                    camera.AddCommandBuffer(cameraEvent, state.CommandBuffer);
                States[cameraInstanceId] = state;
                ResoniteFsr3Plugin.Log.LogInfo("VR jitter: injecting stereo matrices via shader globals (experimental).");
            }

            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                var eye = (Camera.StereoscopicEye)eyeIndex;
                var jitteredProjection = jitterTranslation * camera.GetStereoNonJitteredProjectionMatrix(eye);
                var gpuProjection = GL.GetGPUProjectionMatrix(jitteredProjection, true);
                Projections[eyeIndex] = gpuProjection;
                ViewProjections[eyeIndex] = gpuProjection * camera.GetStereoViewMatrix(eye);
            }

            var commandBuffer = state.CommandBuffer;
            commandBuffer.Clear();
            commandBuffer.SetGlobalMatrixArray(StereoMatrixPId, Projections);
            commandBuffer.SetGlobalMatrixArray(StereoMatrixVPId, ViewProjections);
            return true;
        }

        private static void Remove(int cameraInstanceId, CameraState state)
        {
            if (state.Camera != null)
            {
                foreach (var cameraEvent in InjectionEvents)
                    state.Camera.RemoveCommandBuffer(cameraEvent, state.CommandBuffer);
            }
            state.CommandBuffer.Release();
            States.Remove(cameraInstanceId);
        }

        public static void DestroyAll()
        {
            foreach (var entry in new List<KeyValuePair<int, CameraState>>(States))
                Remove(entry.Key, entry.Value);
        }
    }
}
