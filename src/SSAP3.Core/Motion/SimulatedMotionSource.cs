using System.Numerics;

namespace SSAP3.Core.Motion;

public enum SimulatedHeadMotion
{
    /// <summary>Slow looking around, small pitch and roll, and a quick head turn every 9 s.</summary>
    Natural,

    /// <summary>Yaw sweeps between -60° and +60°.</summary>
    YawSweep,

    /// <summary>No movement (noise only).</summary>
    Still,

    /// <summary>Angles from <see cref="SimulatedMotionOptions.Script"/>.</summary>
    Scripted,
}

public sealed record SimulatedMotionOptions
{
    public double RateHz { get; init; } = 50;

    public double NoiseDegrees { get; init; } = 0.25;

    public double JitterMilliseconds { get; init; } = 4;

    /// <summary>Fixed transport delay added to every sample.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.FromMilliseconds(8);

    public int Seed { get; init; } = 20261113;

    public SimulatedHeadMotion Pattern { get; init; } = SimulatedHeadMotion.Natural;

    public Func<TimeSpan, (float Yaw, float Pitch, float Roll)>? Script { get; init; }

    /// <summary>Disconnect for <see cref="DropoutLength"/> every <see cref="DropoutEvery"/> (zero disables).</summary>
    public TimeSpan DropoutEvery { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DropoutLength { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Stop producing (stay connected) for <see cref="StallLength"/> every <see cref="StallEvery"/> (zero disables).</summary>
    public TimeSpan StallEvery { get; init; } = TimeSpan.FromSeconds(17);

    public TimeSpan StallLength { get; init; } = TimeSpan.FromSeconds(1.2);
}

/// <summary>
/// Development and test source. It simulates AirPods Pro motion end to end:
/// head angles go through an arbitrary ear-mounting tilt and an arbitrary world
/// heading into the same sensor-to-world Q15 report the AirPods send, and then
/// through the real <see cref="DeviceMotionOrientationDecoder"/>. It adds
/// noise, timing jitter, transport delay, stalls, disconnections with
/// reconnection, and nodding while a calibration runs.
/// <see cref="IsSimulated"/> is always true, and the UI labels it as simulated.
/// Its samples are never presented as AirPods tracking.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Deterministic simulation noise for development and tests; never used for anything security-related.")]
public sealed class SimulatedMotionSource : IMotionSource
{
    private static readonly TimeSpan MaximumCatchUp = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallDetection = TimeSpan.FromSeconds(1);

    private readonly SimulatedMotionOptions _options;
    private readonly Random _random;
    private readonly Quaternion _mounting;
    private readonly Quaternion _worldHeading;
    private DeviceMotionOrientationDecoder _decoder;
    private bool _running;
    private TimeSpan _origin;
    private TimeSpan _nextSample;
    private TimeSpan _lastArrival;
    private TimeSpan? _calibrationStart;
    private long _received;
    private double _rateHz;

    public SimulatedMotionSource(SimulatedMotionOptions? options = null, bool invertYaw = false, bool invertPitch = false, Vector3? savedCalibration = null)
    {
        _options = options ?? new SimulatedMotionOptions();
        if (!(_options.RateHz is >= 1 and <= 400))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RateHz must be 1-400.");
        }

        _random = new Random(_options.Seed);
        _mounting = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 0.3f, 0.1f)), Radians(20 + (_random.NextDouble() * 30))) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(-25 + (_random.NextDouble() * 50))));
        _worldHeading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(_random.NextDouble() * 360));
        _decoder = new DeviceMotionOrientationDecoder(invertYaw, invertPitch, savedCalibration);
    }

    public MotionSourceKind Kind => MotionSourceKind.Simulated;

    public bool IsSimulated => true;

    public Vector3? SavedCalibration => _decoder.LateralAxisInSensorFrame;

    public HeadAxisCalibrationState CalibrationState => _decoder.CalibrationState;

    public void Start(TimeSpan now)
    {
        _running = true;
        _origin = now;
        _nextSample = now;
        _lastArrival = TimeSpan.Zero;
        _received = 0;
        _rateHz = 0;
        _decoder.Reset();
    }

    public void Stop() => _running = false;

    public bool BeginCalibration()
    {
        _decoder.BeginCalibration();
        _calibrationStart = _nextSample;
        return true;
    }

    /// <summary>The simulated listener's true head angles at <paramref name="time"/> (for tests).</summary>
    public (float Yaw, float Pitch, float Roll) TrueAngles(TimeSpan time)
    {
        var t = time - _origin;
        if (_calibrationStart is { } start && time >= start && time - start <= DeviceMotionOrientationDecoder.CalibrationWindow + TimeSpan.FromMilliseconds(500))
        {
            // Guided nod: look up first, about 0.9 Hz, ±22°.
            var tau = (time - start).TotalSeconds;
            return (0f, (float)(22 * Math.Sin(2 * Math.PI * 0.9 * tau)), 0f);
        }

        return _options.Pattern switch
        {
            SimulatedHeadMotion.Still => (0f, 0f, 0f),
            SimulatedHeadMotion.YawSweep => ((float)(60 * Math.Sin(2 * Math.PI * 0.1 * t.TotalSeconds)), 0f, 0f),
            SimulatedHeadMotion.Scripted when _options.Script is not null => _options.Script(t),
            _ => Natural(t.TotalSeconds),
        };
    }

    public void Poll(TimeSpan now, ICollection<OrientationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!_running)
        {
            return;
        }

        if (now - _nextSample > MaximumCatchUp)
        {
            _nextSample = now - MaximumCatchUp;
        }

        var period = TimeSpan.FromSeconds(1.0 / _options.RateHz);
        while (_nextSample <= now)
        {
            var time = _nextSample;
            _nextSample += period;
            if (InWindow(time, _options.DropoutEvery, _options.DropoutLength) || InWindow(time, _options.StallEvery, _options.StallLength))
            {
                continue;
            }

            var (yaw, pitch, roll) = TrueAngles(time);
            var noise = _options.NoiseDegrees;
            var head = QuaternionMath.FromYawPitchRollDegrees(
                yaw + (float)(Gaussian() * noise),
                pitch + (float)(Gaussian() * noise),
                roll + (float)(Gaussian() * noise));
            var attitude = Quaternion.Normalize(_worldHeading * head * _mounting);
            var report = ToReport(attitude, time);
            if (!_decoder.TryDecode(new HeadTrackingFrame(SensorDataWx.ServiceDeviceMotion, report, 81), out var orientation))
            {
                continue;
            }

            var jitter = TimeSpan.FromMilliseconds(Math.Abs(Gaussian()) * _options.JitterMilliseconds);
            var arrival = time + _options.Latency + jitter;
            if (arrival <= _lastArrival)
            {
                arrival = _lastArrival + TimeSpan.FromTicks(2500);
            }

            if (_received > 0)
            {
                var interval = (arrival - _lastArrival).TotalSeconds;
                if (interval > 0 && interval < 1)
                {
                    _rateHz = _rateHz <= 0 ? 1 / interval : _rateHz + (0.05 * ((1 / interval) - _rateHz));
                }
            }

            _lastArrival = arrival;
            _received++;
            samples.Add(new OrientationSample(arrival, orientation, _decoder.ProvidesPitchRoll));
        }
    }

    public MotionSourceHealth GetHealth(TimeSpan now)
    {
        var dropout = _running && InWindow(now, _options.DropoutEvery, _options.DropoutLength);
        var stale = _running && !dropout && _received > 0 && now - _lastArrival > StallDetection;
        var state = !_running ? MotionSourceState.Stopped
            : dropout ? MotionSourceState.Reconnecting
            : stale ? MotionSourceState.Stalled
            : _received == 0 ? MotionSourceState.WaitingForData
            : MotionSourceState.Streaming;
        return new MotionSourceHealth(
            Kind,
            state,
            IsSimulated: true,
            "Simulated head motion (development only, not real AirPods tracking)",
            state switch
            {
                MotionSourceState.Reconnecting => "Simulated disconnection; reconnecting.",
                MotionSourceState.Stalled => "Simulated stale data.",
                _ => null,
            },
            _rateHz,
            _received > 0 ? now - _lastArrival : null,
            _received,
            SamplesRejected: 0,
            _decoder.ProvidesPitchRoll,
            _decoder.CalibrationState,
            _options.Latency.TotalMilliseconds + (_options.JitterMilliseconds * 0.8));
    }

    public void Dispose() => Stop();

    private static DeviceMotionReport ToReport(Quaternion attitude, TimeSpan time)
    {
        if (attitude.W < 0)
        {
            attitude = Quaternion.Negate(attitude);
        }

        static short Q15(float value) => (short)Math.Clamp(MathF.Round(value * DeviceMotionReport.QuaternionScale), short.MinValue, short.MaxValue);
        var x = Q15(attitude.X);
        var y = Q15(attitude.Y);
        var z = Q15(attitude.Z);
        if (x == 0 && y == 0 && z == 0)
        {
            x = 1;
        }

        return new DeviceMotionReport((ulong)time.Ticks * 100UL, x, y, z, 0, 0, 0, 0, 0, 0, 0, 0, -1000);
    }

    private static (float Yaw, float Pitch, float Roll) Natural(double t)
    {
        var yaw = 35 * Math.Sin(2 * Math.PI * 0.07 * t);

        // A quick look to the side and back every 9 s (0.35 s each way).
        var phase = t % 9.0;
        var side = ((int)(t / 9.0) % 2 == 0) ? 50.0 : -50.0;
        var turn = phase switch
        {
            < 6.0 => 0.0,
            < 6.35 => SmoothStep((phase - 6.0) / 0.35),
            < 8.0 => 1.0,
            < 8.35 => 1.0 - SmoothStep((phase - 8.0) / 0.35),
            _ => 0.0,
        };
        yaw += side * turn;
        var pitch = 8 * Math.Sin((2 * Math.PI * 0.11 * t) + 1);
        var roll = 4 * Math.Sin((2 * Math.PI * 0.05 * t) + 2);
        return ((float)yaw, (float)pitch, (float)roll);
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - (2 * x));
    }

    private static float Radians(double degrees) => (float)(degrees * Math.PI / 180);

    private bool InWindow(TimeSpan time, TimeSpan every, TimeSpan length)
    {
        if (every <= TimeSpan.Zero || length <= TimeSpan.Zero)
        {
            return false;
        }

        var offset = (time - _origin).Ticks % every.Ticks;
        return time - _origin >= every && offset < length.Ticks;
    }

    private double Gaussian()
    {
        // Box-Muller (deterministic for a seed; for simulation only).
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
