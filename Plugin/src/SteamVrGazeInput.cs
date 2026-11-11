using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace ResoniteFSR3
{
    /// <summary>
    /// Gaze from SteamVR itself: IVRSystem::GetEyeTrackedFoveationCenter (OpenVR 2.15.6+, IVRSystem_026), which returns
    /// each eye's gaze point in that eye's NDC. Any eye tracker with a SteamVR driver feeds it (Bigscreen's own eye
    /// tracking, Pimax, Project Babble's SteamVR driver, Steam Link headsets), and it is read directly in the renderer
    /// right before rendering, so nothing else sits in between.
    ///
    /// The renderer's bundled C# OpenVR bindings predate this call, so it is invoked through the interface's function
    /// table (slot 35 of IVRSystem_026).
    /// </summary>
    internal static class SteamVrGazeInput
    {
        private const string FunctionTableName = "FnTable:IVRSystem_026";
        private const int GetEyeTrackedFoveationCenterSlot = 35;

        [StructLayout(LayoutKind.Sequential)]
        private struct NormalizedDeviceCoordinates
        {
            public float X;
            public float Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetEyeTrackedFoveationCenterDelegate(ref NormalizedDeviceCoordinates left, ref NormalizedDeviceCoordinates right);

        private static GetEyeTrackedFoveationCenterDelegate _getFoveationCenter;
        private static bool _initialized;
        private static bool _loggedFirstValid;
        private static bool _loggedInvalid;

        public static bool Available
        {
            get
            {
                EnsureInitialized();
                return _getFoveationCenter != null;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;
            try
            {
                var error = EVRInitError.None;
                IntPtr functionTable = OpenVR.GetGenericInterface(FunctionTableName, ref error);
                if (functionTable == IntPtr.Zero || error != EVRInitError.None)
                {
                    ResoniteFsr3Plugin.Log.LogWarning($"SteamVR gaze unavailable: this SteamVR does not provide {FunctionTableName} ({error}). Update SteamVR, or use another Gaze source.");
                    return;
                }
                IntPtr function = Marshal.ReadIntPtr(functionTable, GetEyeTrackedFoveationCenterSlot * IntPtr.Size);
                if (function == IntPtr.Zero)
                    return;
                _getFoveationCenter = (GetEyeTrackedFoveationCenterDelegate)Marshal.GetDelegateForFunctionPointer(function, typeof(GetEyeTrackedFoveationCenterDelegate));
                ResoniteFsr3Plugin.Log.LogInfo("SteamVR gaze: bound IVRSystem::GetEyeTrackedFoveationCenter.");
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"SteamVR gaze unavailable: {exception.Message}");
            }
        }

        /// <summary>
        /// Each eye's gaze point in eye UV (0..1, y up, matching Unity's eye projection). Returns false when SteamVR has
        /// no valid gaze (no eye-tracking driver feeding it, or tracking lost).
        /// </summary>
        public static bool TryGetCenters(out Vector2 leftUv, out Vector2 rightUv)
        {
            leftUv = rightUv = new Vector2(0.5f, 0.5f);
            if (!Available)
                return false;

            var left = new NormalizedDeviceCoordinates();
            var right = new NormalizedDeviceCoordinates();
            bool valid;
            try
            {
                valid = _getFoveationCenter(ref left, ref right);
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"SteamVR gaze call failed, disabling it: {exception.Message}");
                _getFoveationCenter = null;
                return false;
            }

            if (!valid)
            {
                if (!_loggedInvalid)
                {
                    _loggedInvalid = true;
                    ResoniteFsr3Plugin.Log.LogInfo("SteamVR gaze: SteamVR reports no valid eye tracking (is an eye-tracking SteamVR driver running?). Using Resonite's eye tracking until it does.");
                }
                return false;
            }

            leftUv = new Vector2(Mathf.Clamp01(left.X * 0.5f + 0.5f), Mathf.Clamp01(left.Y * 0.5f + 0.5f));
            rightUv = new Vector2(Mathf.Clamp01(right.X * 0.5f + 0.5f), Mathf.Clamp01(right.Y * 0.5f + 0.5f));
            if (!_loggedFirstValid)
            {
                _loggedFirstValid = true;
                ResoniteFsr3Plugin.Log.LogInfo($"SteamVR gaze: receiving eye tracking (left NDC {left.X:0.000},{left.Y:0.000}, right NDC {right.X:0.000},{right.Y:0.000}).");
            }
            return true;
        }
    }
}
