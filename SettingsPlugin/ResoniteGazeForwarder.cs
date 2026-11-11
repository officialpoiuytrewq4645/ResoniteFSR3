using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Elements.Core;
using FrooxEngine;

namespace ResoniteFSR3
{
    /// <summary>
    /// Passes the eye tracking Resonite already receives (Babble on 8888, or any other supported tracker) to the renderer
    /// process, which can't see engine input. Reads InputInterface.ActiveEyes and sends the combined gaze direction plus
    /// fixation distance as one small OSC message to 127.0.0.1:ResoniteGazePort, only while foveation uses this source.
    /// </summary>
    internal sealed class ResoniteGazeForwarder : IDisposable
    {
        private const int SendIntervalMilliseconds = 8;   // ~120 Hz
        private const float AssumedInterpupillaryDistance = 0.063f;

        private readonly FsrSettings _settings;
        private readonly ManualLogSource _log;
        private readonly UdpClient _client = new UdpClient();
        private readonly byte[] _packet;
        private readonly int _floatOffset;
        private Thread _thread;
        private volatile bool _running;
        private bool _loggedFirstSend;

        public ResoniteGazeForwarder(FsrSettings settings, ManualLogSource log)
        {
            _settings = settings;
            _log = log;

            // Fixed-layout OSC message: address, type tags, 10 big-endian floats:
            // combined direction xyz, fixation distance, left eye direction xyz, right eye direction xyz
            var packet = new System.Collections.Generic.List<byte>();
            AppendOscString(packet, "/resonitefsr3/gaze");
            AppendOscString(packet, ",ffffffffff");
            _floatOffset = packet.Count;
            packet.AddRange(new byte[40]);
            _packet = packet.ToArray();
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(SendLoop) { IsBackground = true, Name = "ResoniteFSR3 gaze forwarder" };
            _thread.Start();
        }

        private void SendLoop()
        {
            while (_running)
            {
                try
                {
                    // Also sent for Gaze = SteamVR, where it is the fallback while SteamVR has no valid gaze
                    var gazeSource = _settings.FoveationGaze.Value;
                    if (_settings.Foveation.Value != FoveationMode.Off && (gazeSource == GazeSource.Resonite || gazeSource == GazeSource.SteamVR))
                        SendCurrentGaze();
                }
                catch (Exception exception)
                {
                    _log.LogWarning($"Gaze forward failed: {exception.Message}");
                    Thread.Sleep(1000);
                }
                Thread.Sleep(SendIntervalMilliseconds);
            }
        }

        private Eyes _lastEyes;
        private DateTime _lastDiagnosticUtc = DateTime.MinValue;

        /// <summary>The eye device that is actually tracking: ActiveEyes if it is, otherwise any other tracking one.</summary>
        private static Eyes PickTrackingEyes()
        {
            var input = Engine.Current?.InputInterface;
            if (input == null)
                return null;
            var active = input.ActiveEyes;
            if (active != null && active.IsEyeTrackingActive && active.IsTracking)
                return active;
            var candidates = input.GetDevices<Eyes>(candidate => candidate.IsEyeTrackingActive && candidate.IsTracking);
            return candidates != null && candidates.Count > 0 ? candidates[0] : null;
        }

        private void SendCurrentGaze()
        {
            var eyes = PickTrackingEyes();
            if (eyes == null)
                return;
            if (!ReferenceEquals(eyes, _lastEyes))
            {
                _lastEyes = eyes;
                _log.LogInfo($"Gaze source: eye device '{eyes.Name}' ({eyes.GetType().Name}).");
            }

            // Drivers like Babble only set the per-eye directions (head space: +X right, +Y up, +Z forward), so average
            // those; fall back to the combined eye if a driver only provides that.
            float3 left = eyes.LeftEye.Direction;
            float3 right = eyes.RightEye.Direction;
            float3 direction;
            if (Length(left) > 0.5f && Length(right) > 0.5f)
                direction = new float3((left.x + right.x) * 0.5f, (left.y + right.y) * 0.5f, (left.z + right.z) * 0.5f);
            else
                direction = eyes.CombinedEye.Direction;
            if (Length(direction) < 0.5f)
                return;

            float distance = eyes.ConvergenceDistance;
            if (!(distance > 0f) || float.IsInfinity(distance))
                distance = DistanceFromEyeDirections(left, right);

            WriteFloat(_floatOffset, direction.x);
            WriteFloat(_floatOffset + 4, direction.y);
            WriteFloat(_floatOffset + 8, direction.z);
            WriteFloat(_floatOffset + 12, distance);
            // Per-eye directions (zero if a driver only provides the combined eye)
            bool perEyeValid = Length(left) > 0.5f && Length(right) > 0.5f;
            WriteFloat(_floatOffset + 16, perEyeValid ? left.x : 0f);
            WriteFloat(_floatOffset + 20, perEyeValid ? left.y : 0f);
            WriteFloat(_floatOffset + 24, perEyeValid ? left.z : 0f);
            WriteFloat(_floatOffset + 28, perEyeValid ? right.x : 0f);
            WriteFloat(_floatOffset + 32, perEyeValid ? right.y : 0f);
            WriteFloat(_floatOffset + 36, perEyeValid ? right.z : 0f);
            _client.Send(_packet, _packet.Length, new IPEndPoint(IPAddress.Loopback, _settings.ResoniteGazePort.Value));

            bool diagnosticDue = (DateTime.UtcNow - _lastDiagnosticUtc).TotalSeconds >= 10 && _settings.VerboseLogging.Value;
            if (!_loggedFirstSend || diagnosticDue)
            {
                _loggedFirstSend = true;
                _lastDiagnosticUtc = DateTime.UtcNow;
                _log.LogInfo($"Gaze to renderer port {_settings.ResoniteGazePort.Value}: direction {direction.x:0.00},{direction.y:0.00},{direction.z:0.00} " +
                             $"(left {left.x:0.00},{left.y:0.00},{left.z:0.00} right {right.x:0.00},{right.y:0.00},{right.z:0.00}), distance {distance:0.00} m");
            }
        }

        private static float Length(float3 vector) => MathF.Sqrt(vector.x * vector.x + vector.y * vector.y + vector.z * vector.z);

        /// <summary>Fixation distance where the two eye rays cross, or 0 if they are (near) parallel.</summary>
        private static float DistanceFromEyeDirections(float3 left, float3 right)
        {
            float leftLength = MathF.Sqrt(left.x * left.x + left.y * left.y + left.z * left.z);
            float rightLength = MathF.Sqrt(right.x * right.x + right.y * right.y + right.z * right.z);
            if (leftLength < 1e-4f || rightLength < 1e-4f)
                return 0f;
            float cosine = (left.x * right.x + left.y * right.y + left.z * right.z) / (leftLength * rightLength);
            float vergence = MathF.Acos(Math.Clamp(cosine, -1f, 1f));
            float halfTangent = MathF.Tan(vergence * 0.5f);
            return halfTangent > 1e-4f ? AssumedInterpupillaryDistance * 0.5f / halfTangent : 0f;
        }

        private void WriteFloat(int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, _packet, offset, 4);
        }

        private static void AppendOscString(System.Collections.Generic.List<byte> packet, string value)
        {
            packet.AddRange(Encoding.ASCII.GetBytes(value));
            int padding = 4 - (value.Length % 4);
            for (int index = 0; index < padding; index++)
                packet.Add(0);
        }

        public void Dispose()
        {
            _running = false;
            _client.Dispose();
        }
    }
}
