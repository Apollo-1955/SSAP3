using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;

namespace SSAP3.Core.Motion.Companion;

/// <summary>
/// Motion from a paired companion device over the SSAP3 companion protocol.
/// SSAP3 connects out to the companion (never listens). The I/O thread
/// connects, authenticates both sides, decrypts records and queues samples.
/// <see cref="Poll"/> drains them on the motion thread.
///
/// <para>Timestamps. The companion's clock is mapped onto SSAP3's monotonic
/// clock using the fastest-arriving packet: offset = min(arrival − sent),
/// allowed to drift upward by 1 ms per second. Network jitter therefore does
/// not reach the estimator. TransportDelayMs reports the mean delay beyond that
/// fastest packet.</para>
///
/// <para>Recovery. Any error closes the connection. The source then reconnects
/// with exponential backoff (1, 2, 4, 8, 15 s), and a session that streamed for
/// more than 10 s resets the backoff.</para>
/// </summary>
public sealed class CompanionMotionSource : IMotionSource
{
    private const int MaximumPending = 256;
    private const int MaximumRecordsPerSecond = 400;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GiveUpStalledAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _pairingKey;
    private readonly Func<TimeSpan> _clock;
    private readonly object _sync = new();
    private readonly List<OrientationSample> _pending = [];
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly bool _invertYaw;
    private readonly bool _invertPitch;
    private DeviceMotionOrientationDecoder _decoder;
    private Thread? _thread;
    private Socket? _socket;
    private volatile bool _stopping;

    private MotionSourceState _state = MotionSourceState.Stopped;
    private string? _problem;
    private TimeSpan _lastArrival;
    private TimeSpan _lastMapped;
    private TimeSpan? _offset;
    private TimeSpan _offsetUpdatedAt;
    private ulong _lastSenderTimestamp;
    private long _received;
    private long _rejected;
    private double _rateHz;
    private double _delayMs;
    private bool _senderSimulated;
    private bool _senderPitchRoll;

    public CompanionMotionSource(string host, int port, byte[] pairingKey, Func<TimeSpan> clock, bool invertYaw = false, bool invertPitch = false, Vector3? savedCalibration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(pairingKey);
        ArgumentNullException.ThrowIfNull(clock);
        if (pairingKey.Length != CompanionProtocol.KeyLength)
        {
            throw new ArgumentException("The pairing key must be 32 bytes.", nameof(pairingKey));
        }

        _host = host.Trim();
        _port = port;
        _pairingKey = (byte[])pairingKey.Clone();
        _clock = clock;
        _invertYaw = invertYaw;
        _invertPitch = invertPitch;
        _decoder = new DeviceMotionOrientationDecoder(invertYaw, invertPitch, savedCalibration);
    }

    public MotionSourceKind Kind => MotionSourceKind.Companion;

    /// <summary>True when the companion marks its data as simulated.</summary>
    public bool IsSimulated => _senderSimulated;

    public Vector3? SavedCalibration => _decoder.LateralAxisInSensorFrame;

    public void Start(TimeSpan now)
    {
        if (_thread is not null)
        {
            return;
        }

        _stopping = false;
        _stopSignal.Reset();
        SetState(MotionSourceState.Connecting, null);
        _thread = new Thread(Run) { IsBackground = true, Name = "SSAP3 Companion" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _stopSignal.Set();
        CloseSocket();
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }

        _thread = null;
        SetState(MotionSourceState.Stopped, null);
    }

    public void Poll(TimeSpan now, ICollection<OrientationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        lock (_sync)
        {
            foreach (var sample in _pending)
            {
                samples.Add(sample);
            }

            _pending.Clear();
        }
    }

    public bool BeginCalibration()
    {
        // Head-orientation companions (Apple's API) already deliver pitch and roll.
        lock (_sync)
        {
            _decoder.BeginCalibration();
        }

        return true;
    }

    public MotionSourceHealth GetHealth(TimeSpan now)
    {
        lock (_sync)
        {
            var state = _state;
            if (state == MotionSourceState.Streaming && now - _lastArrival > StallAfter)
            {
                state = MotionSourceState.Stalled;
            }

            return new MotionSourceHealth(
                Kind,
                state,
                _senderSimulated,
                _senderSimulated
                    ? $"Companion {_host}:{_port} (the companion reports SIMULATED data)"
                    : $"Companion {_host}:{_port}",
                state == MotionSourceState.Stalled ? _problem ?? "No samples from the companion." : _problem,
                _rateHz,
                _received > 0 ? now - _lastArrival : null,
                _received,
                _rejected,
                _senderPitchRoll || _decoder.ProvidesPitchRoll,
                _senderPitchRoll ? HeadAxisCalibrationState.Calibrated : _decoder.CalibrationState,
                _received > 0 ? _delayMs : null);
        }
    }

    public void Dispose()
    {
        Stop();
        _stopSignal.Dispose();
        CryptographicOperations.ZeroMemory(_pairingKey);
    }

    private void Run()
    {
        var attempt = 0;
        while (!_stopping)
        {
            var streamedSince = TimeSpan.Zero;
            try
            {
                SetState(attempt == 0 ? MotionSourceState.Connecting : MotionSourceState.Reconnecting, _problem);
                using var socket = Connect();
                _socket = socket;
                using var aes = Handshake(socket);
                SetState(MotionSourceState.WaitingForData, null);
                streamedSince = _clock();
                ReadRecords(socket, aes);
            }
            catch (Exception exception) when (exception is SocketException or IOException or CompanionProtocolException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stopping)
                {
                    break;
                }

                lock (_sync)
                {
                    _problem = exception is CompanionProtocolException protocol
                        ? protocol.Message
                        : $"Connection to the companion failed: {exception.Message}";
                }
            }
            finally
            {
                CloseSocket();
            }

            if (_stopping)
            {
                break;
            }

            if (streamedSince != TimeSpan.Zero && _clock() - streamedSince > TimeSpan.FromSeconds(10))
            {
                attempt = 0;
            }

            SetState(MotionSourceState.Reconnecting, _problem);
            _stopSignal.Wait(Backoff[Math.Min(attempt, Backoff.Length - 1)]);
            attempt++;
        }
    }

    private Socket Connect()
    {
        var addresses = IPAddress.TryParse(_host, out var literal)
            ? [literal]
            : Dns.GetHostAddresses(_host);
        var address = addresses.FirstOrDefault(CompanionProtocol.IsAllowedAddress)
            ?? throw new CompanionProtocolException(
                $"{_host} is not on this PC, the local network or a private overlay. SSAP3 only connects to local companions.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            socket.ConnectAsync(new IPEndPoint(address, _port), timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private AesGcm Handshake(Socket socket)
    {
        var clientNonce = CompanionProtocol.CreateNonce();
        SendAll(socket, CompanionProtocol.CreateClientHello(clientNonce));
        var hello = ReceiveExactly(socket, CompanionProtocol.ServerHelloLength);
        var serverNonce = new byte[CompanionProtocol.NonceLength];
        if (!CompanionProtocol.TryVerifyServerHello(_pairingKey, clientNonce, hello, serverNonce))
        {
            throw new CompanionProtocolException("The companion did not prove the pairing key. Pair it again.");
        }

        SendAll(socket, CompanionProtocol.CreateClientProof(_pairingKey, clientNonce, serverNonce));
        var sessionKey = CompanionProtocol.DeriveSessionKey(_pairingKey, clientNonce, serverNonce);
        try
        {
            return new AesGcm(sessionKey, 16);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private void ReadRecords(Socket socket, AesGcm aes)
    {
        socket.ReceiveTimeout = (int)ReadTimeout.TotalMilliseconds;
        var buffer = new byte[CompanionProtocol.RecordLength];
        ulong lastCounter = 0;
        var stalledSince = TimeSpan.Zero;
        var windowStart = _clock();
        var windowCount = 0;
        while (!_stopping)
        {
            try
            {
                ReceiveExactly(socket, buffer);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
            {
                var now = _clock();
                stalledSince = stalledSince == TimeSpan.Zero ? now : stalledSince;
                SetState(MotionSourceState.Stalled, "No samples from the companion.");
                if (now - stalledSince > GiveUpStalledAfter)
                {
                    throw new CompanionProtocolException("The companion stopped sending samples.");
                }

                continue;
            }

            stalledSince = TimeSpan.Zero;
            var arrival = _clock();
            if (!CompanionProtocol.TryOpenRecord(aes, buffer, out var counter, out var sample))
            {
                throw new CompanionProtocolException("A record failed authentication or validation; the connection was closed.");
            }

            if (counter <= lastCounter)
            {
                throw new CompanionProtocolException("A record was replayed or reordered; the connection was closed.");
            }

            lastCounter = counter;
            windowCount++;
            if (arrival - windowStart >= TimeSpan.FromSeconds(1))
            {
                windowStart = arrival;
                windowCount = 1;
            }
            else if (windowCount > MaximumRecordsPerSecond)
            {
                throw new CompanionProtocolException("The companion sent samples faster than 400 per second.");
            }

            Accept(sample, arrival);
        }
    }

    private void Accept(in CompanionSample sample, TimeSpan arrival)
    {
        lock (_sync)
        {
            if (sample.TimestampMicroseconds < _lastSenderTimestamp)
            {
                // The companion restarted its clock: re-learn the offset.
                _offset = null;
            }

            _lastSenderTimestamp = sample.TimestampMicroseconds;
            var sent = TimeSpan.FromTicks((long)Math.Min(sample.TimestampMicroseconds, (ulong)(long.MaxValue / 10)) * 10);
            var candidate = arrival - sent;
            if (_offset is not { } offset || candidate < offset)
            {
                _offset = candidate;
            }
            else
            {
                var drift = TimeSpan.FromTicks(Math.Max(0, (arrival - _offsetUpdatedAt).Ticks / 1000));
                _offset = offset + (candidate - offset < drift ? candidate - offset : drift);
            }

            _offsetUpdatedAt = arrival;
            var mapped = sent + _offset.Value;
            if (mapped > arrival)
            {
                mapped = arrival;
            }

            if (mapped <= _lastMapped)
            {
                mapped = _lastMapped + TimeSpan.FromTicks(2500);
            }

            _delayMs = _received == 0 ? (arrival - mapped).TotalMilliseconds : _delayMs + (0.05 * ((arrival - mapped).TotalMilliseconds - _delayMs));
            if (_received > 0)
            {
                var interval = (arrival - _lastArrival).TotalSeconds;
                if (interval is > 0 and < 1)
                {
                    _rateHz = _rateHz <= 0 ? 1 / interval : _rateHz + (0.05 * ((1 / interval) - _rateHz));
                }
            }

            _lastArrival = arrival;
            _lastMapped = mapped;
            _senderSimulated = (sample.Flags & CompanionSampleFlags.Simulated) != 0;
            _senderPitchRoll = sample.Kind == CompanionSampleKind.HeadOrientation && (sample.Flags & CompanionSampleFlags.PitchRoll) != 0;

            if (!TryToHeadOrientation(sample, out var orientation, out var hasRoll))
            {
                _rejected++;
                return;
            }

            _received++;
            _state = MotionSourceState.Streaming;
            _problem = null;
            if (_pending.Count >= MaximumPending)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(new OrientationSample(mapped, orientation, hasRoll));
        }
    }

    private bool TryToHeadOrientation(in CompanionSample sample, out Quaternion orientation, out bool hasRoll)
    {
        if (sample.Kind == CompanionSampleKind.AirPodsAttitude)
        {
            var report = ToReport(sample);
            hasRoll = _decoder.ProvidesPitchRoll;
            var ok = _decoder.TryDecode(new HeadTrackingFrame(0, report, CompanionProtocol.RecordLength), out orientation);
            hasRoll = _decoder.ProvidesPitchRoll;
            return ok;
        }

        hasRoll = (sample.Flags & CompanionSampleFlags.PitchRoll) != 0;
        var (yaw, pitch, roll) = QuaternionMath.ToYawPitchRollDegrees(sample.Orientation);
        if (!hasRoll)
        {
            pitch = 0;
            roll = 0;
        }

        orientation = QuaternionMath.FromYawPitchRollDegrees(_invertYaw ? -yaw : yaw, _invertPitch ? -pitch : pitch, roll);
        return QuaternionMath.IsFinite(orientation);
    }

    private static DeviceMotionReport ToReport(in CompanionSample sample)
    {
        var q = sample.Orientation;
        if (q.W < 0)
        {
            q = Quaternion.Negate(q);
        }

        static short Q15(float value) => (short)Math.Clamp(MathF.Round(value * DeviceMotionReport.QuaternionScale), short.MinValue, short.MaxValue);
        var rate = (short)Math.Clamp(sample.RotationRateDegreesPerSecond / DeviceMotionReport.DegreesPerSecondPerCount, 0, short.MaxValue);
        return new DeviceMotionReport(sample.TimestampMicroseconds * 1000UL, Q15(q.X), Q15(q.Y), Q15(q.Z), 0, 0, rate, 0, 0, 0, 0, 0, -1000);
    }

    private void SetState(MotionSourceState state, string? problem)
    {
        lock (_sync)
        {
            _state = state;
            _problem = problem;
        }
    }

    private void CloseSocket()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        try
        {
            socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void SendAll(Socket socket, byte[] data)
    {
        var sent = 0;
        while (sent < data.Length)
        {
            var count = socket.Send(data, sent, data.Length - sent, SocketFlags.None);
            if (count <= 0)
            {
                throw new IOException("The companion closed the connection.");
            }

            sent += count;
        }
    }

    private static byte[] ReceiveExactly(Socket socket, int length)
    {
        var buffer = new byte[length];
        ReceiveExactly(socket, buffer);
        return buffer;
    }

    private static void ReceiveExactly(Socket socket, byte[] buffer)
    {
        var received = 0;
        while (received < buffer.Length)
        {
            int count;
            try
            {
                count = socket.Receive(buffer, received, buffer.Length - received, SocketFlags.None);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut && received > 0)
            {
                // A timeout inside a record would misalign the stream: drop the connection.
                throw new IOException("The companion sent an incomplete record.", exception);
            }

            if (count <= 0)
            {
                throw new IOException("The companion closed the connection.");
            }

            received += count;
        }
    }
}

public sealed class CompanionProtocolException : Exception
{
    public CompanionProtocolException()
    {
    }

    public CompanionProtocolException(string message)
        : base(message)
    {
    }

    public CompanionProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
