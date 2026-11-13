namespace SSAP3.Core.Conversation;

/// <summary>
/// Lightweight, local voice-activity detector for a computer microphone.
///
/// Each frame (10-30 ms of mono PCM) is classified from its energy relative
/// to an adaptive noise floor and its zero-crossing rate. It needs no model
/// and no network. Only per-frame statistics are kept; audio is never stored,
/// buffered beyond one frame, or written anywhere.
/// </summary>
public sealed class VoiceActivityDetector
{
    private double _noiseFloorDb = -70;
    private bool _floorInitialised;
    private int _speechFrames;
    private int _hangoverRemaining;

    public VoiceActivityDetector(int sampleRate, double sensitivity = 0.5)
    {
        if (sampleRate is < 8000 or > 192000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        SampleRate = sampleRate;
        Sensitivity = sensitivity;
    }

    public int SampleRate { get; }

    /// <summary>0 = least sensitive (needs loud speech), 1 = most sensitive.</summary>
    public double Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
    }

    private double _sensitivity;

    public double NoiseFloorDb => _noiseFloorDb;

    public double LastFrameDb { get; private set; } = -120;

    /// <summary>Margin above the noise floor needed to count as speech: 18 dB (least sensitive) to 6 dB (most).</summary>
    public double ThresholdDb => 18 - (12 * Sensitivity);

    public bool IsSpeech { get; private set; }

    public bool ProcessFrame(ReadOnlySpan<float> samples)
    {
        if (samples.Length < SampleRate / 200 || samples.Length > SampleRate / 10)
        {
            throw new ArgumentException("Frames must be 5-100 ms long.", nameof(samples));
        }

        double energy = 0;
        var crossings = 0;
        var previous = 0f;
        var valid = 0;
        foreach (var raw in samples)
        {
            var sample = float.IsFinite(raw) ? Math.Clamp(raw, -1f, 1f) : 0f;
            energy += sample * (double)sample;
            if (valid > 0 && (sample >= 0) != (previous >= 0))
            {
                crossings++;
            }

            previous = sample;
            valid++;
        }

        var rms = Math.Sqrt(energy / Math.Max(1, valid));
        var db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        var zcr = crossings / (double)Math.Max(1, valid - 1);
        LastFrameDb = db;

        if (!_floorInitialised)
        {
            _noiseFloorDb = db;
            _floorInitialised = true;
        }

        var candidate =
            db > _noiseFloorDb + ThresholdDb &&
            db > -60 &&
            zcr is > 0.005 and < 0.40;

        // Noise floor: falls fast, rises slowly, and does not rise during speech.
        if (db < _noiseFloorDb)
        {
            _noiseFloorDb += 0.3 * (db - _noiseFloorDb);
        }
        else if (!candidate)
        {
            _noiseFloorDb += 0.01 * (db - _noiseFloorDb);
        }

        _speechFrames = candidate ? _speechFrames + 1 : 0;
        if (_speechFrames >= 2)
        {
            _hangoverRemaining = Math.Max(1, (int)(0.25 * SampleRate / samples.Length));
        }
        else if (_hangoverRemaining > 0)
        {
            _hangoverRemaining--;
        }

        IsSpeech = _speechFrames >= 2 || _hangoverRemaining > 0;
        return IsSpeech;
    }

    public void Reset()
    {
        _noiseFloorDb = -70;
        _floorInitialised = false;
        _speechFrames = 0;
        _hangoverRemaining = 0;
        IsSpeech = false;
    }
}
