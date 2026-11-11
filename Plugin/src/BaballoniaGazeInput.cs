using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ResoniteFSR3
{
    /// <summary>
    /// Test gaze source: listens for Baballonia's DFR output (enable "Use DFR" in Baballonia), which sends VRChat-style
    /// OSC to 127.0.0.1:9020:
    ///   /tracking/eye/LeftRightPitchYaw  float×4  (degrees, ±45; pitch positive = down)
    /// Baballonia's argument order puts the two yaw values in each other's slots (slot 2 = right yaw, slot 4 = left yaw).
    /// Exposes the averaged (cyclopean) direction plus the vergence angle between the eyes, which gives the fixation
    /// distance. This is a testing path; a faster, direct source can replace it.
    /// </summary>
    internal sealed class BaballoniaGazeInput : IDisposable
    {
        private const string PitchYawAddress = "/tracking/eye/LeftRightPitchYaw";
        // Sent by the engine-side plugin: combined gaze direction (Resonite head space: +X right, +Y up, +Z forward)
        // and fixation distance in metres (0 = unknown)
        public const string ResoniteGazeAddress = "/resonitefsr3/gaze";

        private UdpClient _client;
        private Thread _thread;
        private volatile bool _running;
        private readonly object _lock = new object();
        private float _pitchDegrees;
        private float _yawDegrees;
        private float _vergenceDegrees;
        private float _directionX, _directionY, _directionZ = 1f, _fixationDistance;
        // Per eye (index 0 = left, 1 = right). Resonite source: direction xyz in Resonite head space (zero = unavailable).
        // Baballonia source: pitch/yaw in degrees.
        private readonly float[] _eyeDirectionX = new float[2], _eyeDirectionY = new float[2], _eyeDirectionZ = new float[2];
        private readonly float[] _eyePitchDegrees = new float[2], _eyeYawDegrees = new float[2];
        private DateTime _lastReceivedUtc = DateTime.MinValue;
        private long _packetsReceived;

        public int Port { get; }

        public BaballoniaGazeInput(int port)
        {
            Port = port;
        }

        public void Start()
        {
            if (_running)
                return;
            try
            {
                _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                _running = true;
                _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ResoniteFSR3 Baballonia gaze" };
                _thread.Start();
                ResoniteFsr3Plugin.Log.LogInfo($"Listening for Baballonia DFR gaze on 127.0.0.1:{Port}");
            }
            catch (Exception exception)
            {
                ResoniteFsr3Plugin.Log.LogWarning($"Could not listen for Baballonia gaze on port {Port}: {exception.Message}");
            }
        }

        /// <summary>
        /// Latest averaged gaze and vergence (degrees; vergence = how far the eyes turn toward each other, larger = nearer).
        /// Returns false if nothing was received in the last half second.
        /// </summary>
        public bool TryGetGaze(out float pitchDegrees, out float yawDegrees, out float vergenceDegrees)
        {
            lock (_lock)
            {
                pitchDegrees = _pitchDegrees;
                yawDegrees = _yawDegrees;
                vergenceDegrees = _vergenceDegrees;
                return (DateTime.UtcNow - _lastReceivedUtc).TotalSeconds < 0.5;
            }
        }

        /// <summary>Latest direction gaze from the engine-side plugin. Returns false if nothing arrived in the last half second.</summary>
        public bool TryGetDirectionGaze(out float directionX, out float directionY, out float directionZ, out float fixationDistance)
        {
            lock (_lock)
            {
                directionX = _directionX;
                directionY = _directionY;
                directionZ = _directionZ;
                fixationDistance = _fixationDistance;
                return (DateTime.UtcNow - _lastReceivedUtc).TotalSeconds < 0.5;
            }
        }

        /// <summary>Per-eye direction from the engine plugin (Resonite head space). False if stale or not provided.</summary>
        public bool TryGetEyeDirection(int eyeIndex, out float directionX, out float directionY, out float directionZ)
        {
            lock (_lock)
            {
                directionX = _eyeDirectionX[eyeIndex];
                directionY = _eyeDirectionY[eyeIndex];
                directionZ = _eyeDirectionZ[eyeIndex];
                bool provided = directionX * directionX + directionY * directionY + directionZ * directionZ > 0.25f;
                return provided && (DateTime.UtcNow - _lastReceivedUtc).TotalSeconds < 0.5;
            }
        }

        /// <summary>Per-eye pitch/yaw from Baballonia (degrees; pitch positive = down). False if stale.</summary>
        public bool TryGetEyePitchYaw(int eyeIndex, out float pitchDegrees, out float yawDegrees)
        {
            lock (_lock)
            {
                pitchDegrees = _eyePitchDegrees[eyeIndex];
                yawDegrees = _eyeYawDegrees[eyeIndex];
                return (DateTime.UtcNow - _lastReceivedUtc).TotalSeconds < 0.5;
            }
        }

        public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

        private void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] packet = _client.Receive(ref remote);
                    HandlePacket(packet, 0, packet.Length);
                }
                catch (SocketException)
                {
                    if (!_running) return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ResoniteFsr3Plugin.Log.LogWarning($"Gaze receive error: {exception.Message}");
                }
            }
        }

        private void HandlePacket(byte[] data, int offset, int length)
        {
            string address = ReadOscString(data, ref offset, offset + length);
            if (address == "#bundle")
            {
                offset += 8; // time tag
                int end = offset + length;
                while (offset + 4 <= data.Length && offset < end)
                {
                    int elementSize = ReadInt32BigEndian(data, offset);
                    offset += 4;
                    if (elementSize <= 0 || offset + elementSize > data.Length) return;
                    HandlePacket(data, offset, elementSize);
                    offset += elementSize;
                }
                return;
            }

            if (address == ResoniteGazeAddress)
            {
                string directionTypeTags = ReadOscString(data, ref offset, data.Length);
                if (directionTypeTags == null || !directionTypeTags.StartsWith(",ffff"))
                    return;
                bool hasPerEye = directionTypeTags.StartsWith(",ffffffffff");
                lock (_lock)
                {
                    _directionX = ReadFloatBigEndian(data, offset);
                    _directionY = ReadFloatBigEndian(data, offset + 4);
                    _directionZ = ReadFloatBigEndian(data, offset + 8);
                    _fixationDistance = ReadFloatBigEndian(data, offset + 12);
                    for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
                    {
                        int eyeOffset = offset + 16 + eyeIndex * 12;
                        _eyeDirectionX[eyeIndex] = hasPerEye ? ReadFloatBigEndian(data, eyeOffset) : 0f;
                        _eyeDirectionY[eyeIndex] = hasPerEye ? ReadFloatBigEndian(data, eyeOffset + 4) : 0f;
                        _eyeDirectionZ[eyeIndex] = hasPerEye ? ReadFloatBigEndian(data, eyeOffset + 8) : 0f;
                    }
                    _lastReceivedUtc = DateTime.UtcNow;
                }
                if (Interlocked.Increment(ref _packetsReceived) == 1)
                    ResoniteFsr3Plugin.Log.LogInfo("Receiving Resonite eye tracking from the engine plugin.");
                return;
            }

            if (address != PitchYawAddress)
                return;

            string typeTags = ReadOscString(data, ref offset, data.Length);
            if (typeTags == null || !typeTags.StartsWith(",ffff"))
                return;

            float leftPitch = ReadFloatBigEndian(data, offset);
            float rightYaw = ReadFloatBigEndian(data, offset + 4);   // slots swapped by Baballonia
            float rightPitch = ReadFloatBigEndian(data, offset + 8);
            float leftYaw = ReadFloatBigEndian(data, offset + 12);

            lock (_lock)
            {
                _pitchDegrees = (leftPitch + rightPitch) * 0.5f;
                _yawDegrees = (leftYaw + rightYaw) * 0.5f;
                // Converging: left eye turns right (+), right eye turns left (-). Absolute value in case the slot order differs.
                _vergenceDegrees = Math.Abs(leftYaw - rightYaw);
                _eyePitchDegrees[0] = leftPitch;
                _eyeYawDegrees[0] = leftYaw;
                _eyePitchDegrees[1] = rightPitch;
                _eyeYawDegrees[1] = rightYaw;
                _lastReceivedUtc = DateTime.UtcNow;
            }
            Interlocked.Increment(ref _packetsReceived);
        }

        private static string ReadOscString(byte[] data, ref int offset, int limit)
        {
            int start = offset;
            while (offset < limit && offset < data.Length && data[offset] != 0)
                offset++;
            if (offset >= data.Length)
                return null;
            string value = Encoding.ASCII.GetString(data, start, offset - start);
            offset = (offset + 4) & ~3; // skip terminator and pad to 4 bytes
            return value;
        }

        private static int ReadInt32BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static float ReadFloatBigEndian(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
                return 0f;
            var bytes = new[] { data[offset + 3], data[offset + 2], data[offset + 1], data[offset] };
            return BitConverter.ToSingle(bytes, 0);
        }

        public void Dispose()
        {
            _running = false;
            try { _client?.Close(); } catch { }
            _client = null;
        }
    }
}
