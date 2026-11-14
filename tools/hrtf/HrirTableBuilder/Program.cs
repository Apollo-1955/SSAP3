using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Converts the MIT KEMAR diffuse-field equalized HRTFs (Gardner & Martin, MIT
// Media Lab, 1994) into a compact C++ table for the SSAP3 spatial engine.
//
// Usage: HrirTableBuilder <unpacked diffuse folder> <diffuse.zip> <output header>
//
// For each of the 368 measured directions (azimuth 0-180°; the other half is
// the documented KEMAR symmetry) and each ear, it:
//   1. validates the WAV (PCM, 2 channels, 16-bit, 44.1 kHz, 128 frames);
//   2. finds the onset (first sample at 15% of the ear's peak) and removes it,
//      keeping 2 samples of lead-in, so neighbouring directions can be
//      interpolated without comb filtering; the removed integer shift is
//      stored separately and restored at run time (aligned taps + shift
//      reproduce the original response exactly, except for the truncation);
//   3. keeps 96 taps (checks the retained energy);
// and writes one diffuse-field power normalisation constant.

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: HrirTableBuilder <unpacked diffuse folder> <diffuse.zip> <output header>");
    return 64;
}

const int SourceRate = 44100;
const int SourceFrames = 128;
const int Taps = 96;
const int LeadIn = 2;
const double OnsetThreshold = 0.15;

var folder = args[0];
var archive = args[1];
var output = args[2];
var name = new Regex(@"^H(-?\d+)e(\d{3})a\.wav$", RegexOptions.CultureInvariant);
var entries = new List<(int Elevation, int Azimuth, short[] Left, short[] Right)>();

foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
{
    var match = name.Match(Path.GetFileName(file));
    if (!match.Success)
    {
        throw new InvalidDataException($"Unexpected file name: {Path.GetFileName(file)}");
    }

    var bytes = File.ReadAllBytes(file);
    if (bytes.Length != 44 + (SourceFrames * 4) ||
        Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" ||
        Encoding.ASCII.GetString(bytes, 12, 4) != "fmt " || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)) != 1 ||
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) != SourceRate ||
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)) != 16 || Encoding.ASCII.GetString(bytes, 36, 4) != "data" ||
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)) != SourceFrames * 4)
    {
        throw new InvalidDataException($"Not a 44.1 kHz 16-bit stereo 128-frame WAV: {file}");
    }

    var left = new short[SourceFrames];
    var right = new short[SourceFrames];
    for (var frame = 0; frame < SourceFrames; frame++)
    {
        left[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (frame * 4)));
        right[frame] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46 + (frame * 4)));
    }

    entries.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), left, right));
}

var rings = entries.GroupBy(entry => entry.Elevation).OrderBy(group => group.Key).ToList();
var expectedElevations = Enumerable.Range(0, 14).Select(index => -40 + (index * 10)).ToArray();
if (!rings.Select(ring => ring.Key).SequenceEqual(expectedElevations) || entries.Count != 368)
{
    throw new InvalidDataException($"Expected 368 directions on rings -40..90; found {entries.Count} on {string.Join(",", rings.Select(r => r.Key))}.");
}

static double Energy(ReadOnlySpan<short> values)
{
    double sum = 0;
    foreach (var value in values)
    {
        sum += (double)value * value;
    }

    return sum;
}

static int Onset(short[] response, double threshold)
{
    var peak = response.Max(value => Math.Abs((int)value));
    for (var index = 0; index < response.Length; index++)
    {
        if (Math.Abs((int)response[index]) >= threshold * peak)
        {
            return index;
        }
    }

    return 0;
}

// Convention check from the data itself: at azimuth 90 the right ear must be the louder, earlier ear.
var side = entries.Single(entry => entry.Elevation == 0 && entry.Azimuth == 90);
var rightLouder = Energy(side.Right) > 4 * Energy(side.Left);
var rightEarlier = Onset(side.Right, OnsetThreshold) < Onset(side.Left, OnsetThreshold);
if (!rightLouder || !rightEarlier)
{
    throw new InvalidDataException("Azimuth 90 is not the right side in this data set; refusing to guess the convention.");
}

var ordered = new List<(int Elevation, int Azimuth, short[][] Aligned, int[] Shift)>();
var minimumRetained = 1.0;
foreach (var ring in rings)
{
    foreach (var entry in ring.OrderBy(entry => entry.Azimuth))
    {
        var aligned = new short[2][];
        var shift = new int[2];
        var ears = new[] { entry.Left, entry.Right };
        for (var ear = 0; ear < 2; ear++)
        {
            var response = ears[ear];
            shift[ear] = Math.Max(0, Onset(response, OnsetThreshold) - LeadIn);
            aligned[ear] = new short[Taps];
            var count = Math.Min(Taps, SourceFrames - shift[ear]);
            Array.Copy(response, shift[ear], aligned[ear], 0, count);
            var total = Energy(response);
            minimumRetained = Math.Min(minimumRetained, total <= 0 ? 1 : Energy(aligned[ear]) / total);
        }

        ordered.Add((entry.Elevation, entry.Azimuth, aligned, shift));
    }
}

if (minimumRetained < 0.97)
{
    throw new InvalidDataException($"Truncation to {Taps} taps keeps only {minimumRetained:P2} of some response's energy.");
}

// Diffuse-field power normalisation: the mean energy over all directions and ears becomes 1.
var meanEnergy = ordered.Average(entry => (Energy(entry.Aligned[0]) + Energy(entry.Aligned[1])) / 2);
var scale = 1.0 / Math.Sqrt(meanEnergy);
var archiveHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));

// Stage timbre correction (see StageEq below): fitted to the default +-30 degree
// stage at elevation 0. By symmetry each ear gets the near response of one
// speaker and the far response of the other: entry (0, 30), right ear = near.
var stage = ordered.Single(entry => entry.Elevation == 0 && entry.Azimuth == 30);
var stageEq = StageEq.Fit(
    stage.Aligned[1].Select(value => value * scale).ToArray(),
    stage.Aligned[0].Select(value => value * scale).ToArray(),
    SourceRate);

var text = new StringBuilder();
text.AppendLine("// Generated by tools/hrtf/HrirTableBuilder. Do not edit by hand.");
text.AppendLine("//");
text.AppendLine("// Source: MIT KEMAR HRTF measurements, diffuse-field equalized set (diffuse.zip),");
text.AppendLine("// W. G. Gardner and K. D. Martin, MIT Media Lab, 1994.");
text.AppendLine("// https://sound.media.mit.edu/resources/KEMAR.html");
text.AppendLine("// \"This data is Copyright 1994 by the MIT Media Laboratory. It is provided free with");
text.AppendLine("// no restrictions on use, provided the authors are cited when the data is used in any");
text.AppendLine("// research or commercial application.\"");
text.AppendLine($"// diffuse.zip SHA-256: {archiveHash}");
text.AppendLine("//");
text.AppendLine($"// {ordered.Count} measured directions (azimuth 0-180 degrees, positive = listener's RIGHT, checked");
text.AppendLine("// from the data); the left half is the KEMAR symmetry (swap ears at 360 - azimuth).");
text.AppendLine($"// Each ear is onset-aligned (15% of peak, {LeadIn} samples lead-in) and truncated to {Taps} taps");
text.AppendLine($"// (minimum retained energy {minimumRetained:P2}); kOnset holds the removed shift in 44.1 kHz samples.");
text.AppendLine("// Taps are the original 16-bit values; multiply by kScale for diffuse-field unit power.");
text.AppendLine("#pragma once");
text.AppendLine();
text.AppendLine("#include <cstdint>");
text.AppendLine();
text.AppendLine("namespace ssap3::hrir");
text.AppendLine("{");
text.AppendLine($"inline constexpr int kSourceRate = {SourceRate};");
text.AppendLine($"inline constexpr int kTaps = {Taps};");
text.AppendLine($"inline constexpr int kRingCount = {rings.Count};");
text.AppendLine($"inline constexpr int kEntryCount = {ordered.Count};");
text.AppendLine($"inline constexpr float kScale = {(scale / 1.0).ToString("R", CultureInfo.InvariantCulture)}f;");
text.AppendLine($"inline constexpr std::int16_t kRingElevation[kRingCount] = {{{string.Join(", ", rings.Select(r => r.Key))}}};");
var starts = new List<int>();
var start = 0;
foreach (var ring in rings)
{
    starts.Add(start);
    start += ring.Count();
}

starts.Add(start);
text.AppendLine($"inline constexpr std::int16_t kRingStart[kRingCount + 1] = {{{string.Join(", ", starts)}}};");
text.AppendLine($"inline constexpr std::int16_t kAzimuth[kEntryCount] = {{{string.Join(", ", ordered.Select(e => e.Azimuth))}}};");
text.AppendLine("// [entry][ear]: 0 = left ear, 1 = right ear.");
text.AppendLine($"inline constexpr std::uint8_t kOnset[kEntryCount][2] = {{{string.Join(", ", ordered.Select(e => $"{{{e.Shift[0]}, {e.Shift[1]}}}"))}}};");
text.AppendLine("inline constexpr std::int16_t kResponse[kEntryCount][2][kTaps] = {");
foreach (var entry in ordered)
{
    text.AppendLine($"    {{{{{string.Join(",", entry.Aligned[0])}}},{{{string.Join(",", entry.Aligned[1])}}}}}, // {entry.Elevation} {entry.Azimuth}");
}

text.AppendLine("};");
text.AppendLine();
text.AppendLine("// Stage timbre correction, applied to the direct path of both ears alike (so");
text.AppendLine("// interaural differences are unchanged). Fitted to the +-30 degree stage's power");
text.AppendLine("// response, 1/3-octave smoothed, flattened within " +
    $"{StageEq.MinCorrectionDb:+0;-0} to {StageEq.MaxCorrectionDb:+0;-0} dB (boosts above 300 Hz at most {StageEq.MaxTrebleBoostDb:+0} dB),");
text.AppendLine($"// with no correction above {StageEq.FadeEndHz / 1000:0} kHz.");
text.AppendLine("// This also restores the bass that the 128-tap measurements lose (the diffuse-field");
text.AppendLine("// average of the whole set falls about 9 dB from 300 Hz to 20 Hz).");
text.AppendLine($"// Fit residual: {stageEq.ResidualDb:0.00} dB RMS over 25 Hz - {StageEq.FadeEndHz / 1000:0} kHz (at {StageEq.FitRate / 1000:0} kHz).");
text.AppendLine("// {type (0 = low shelf, 1 = peaking), frequency in Hz, Q, gain in dB}; the engine designs");
text.AppendLine("// each biquad at the device rate (RBJ cookbook) and skips any above 0.45 x the rate.");
text.AppendLine($"inline constexpr int kStageEqCount = {stageEq.Filters.Count};");
text.AppendLine("inline constexpr float kStageEq[kStageEqCount][4] = {");
foreach (var filter in stageEq.Filters)
{
    text.AppendLine(string.Format(CultureInfo.InvariantCulture, "    {{{0}.0f, {1:0.0}f, {2:0.000}f, {3:0.00}f}},", filter.Type, filter.Frequency, filter.Q, filter.GainDb));
}

text.AppendLine("};");
text.AppendLine("} // namespace ssap3::hrir");
File.WriteAllText(output, text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

Console.WriteLine($"Wrote {output}: {ordered.Count} directions x 2 ears x {Taps} taps; minimum retained energy {minimumRetained:P2}; scale {scale:G6}.");
Console.WriteLine($"Right-side check at 0/90: right ear {10 * Math.Log10(Energy(side.Right) / Energy(side.Left)):0.0} dB louder, {Onset(side.Left, OnsetThreshold) - Onset(side.Right, OnsetThreshold)} samples earlier.");
Console.WriteLine($"Max onset shift: {ordered.Max(e => Math.Max(e.Shift[0], e.Shift[1]))} samples; source SHA-256 {archiveHash}.");
Console.WriteLine($"Stage EQ: {stageEq.Filters.Count} biquads, residual {stageEq.ResidualDb:0.00} dB RMS.");
foreach (var filter in stageEq.Filters)
{
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} {1,8:0.0} Hz  Q {2:0.000}  {3,6:+0.00;-0.00} dB", filter.Type == 0 ? "low shelf" : "peaking  ", filter.Frequency, filter.Q, filter.GainDb));
}

Console.WriteLine("  f (Hz)   stage before   correction target   fitted   after");
for (var index = 0; index < stageEq.Frequencies.Length; index += 8)
{
    var fitted = StageEq.ResponseDb(stageEq.Filters, stageEq.Frequencies[index]);
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,7:0}   {1,8:+0.0;-0.0}   {2,8:+0.0;-0.0}   {3,8:+0.0;-0.0}   {4,8:+0.0;-0.0}",
        stageEq.Frequencies[index], stageEq.Before[index], stageEq.Target[index], fitted, stageEq.Before[index] + fitted));
}

return 0;

/// <summary>
/// Fits a short chain of biquads (one low shelf, then peaking filters) that
/// flattens the smoothed power response of the virtual-speaker stage. Fully
/// deterministic: a greedy placement followed by coordinate descent.
/// </summary>
internal static class StageEq
{
    public const double FitRate = 48000;     // the usual AirPods endpoint rate; the engine redesigns per rate
    public const double MinCorrectionDb = -6;
    public const double MaxCorrectionDb = 9;      // bass: restores the measurement loss
    public const double MaxTrebleBoostDb = 6;     // above 300 Hz: notches move with direction, so fill them only partly
    private const double TrebleFromHz = 300;
    public const double FadeStartHz = 14000;
    public const double FadeEndHz = 16000;
    private const int MaxPeaks = 7;
    private const double GoodEnoughDb = 0.6;

    public sealed record Filter(int Type, double Frequency, double Q, double GainDb);

    public sealed record Result(List<Filter> Filters, double ResidualDb, double[] Frequencies, double[] Before, double[] Target);

    public static Result Fit(double[] near, double[] far, int sourceRate)
    {
        var grid = new List<double>();
        for (var value = 20.0; value <= 20000.0; value *= Math.Pow(2, 1.0 / 24))
        {
            grid.Add(value);
        }

        var frequencies = grid.ToArray();
        var power = frequencies.Select(f => (Power(near, f, sourceRate) + Power(far, f, sourceRate)) / 2).ToArray();
        var smooth = new double[frequencies.Length];
        for (var i = 0; i < frequencies.Length; i++)
        {
            var band = Enumerable.Range(0, frequencies.Length).Where(j => Math.Abs(Math.Log2(frequencies[j] / frequencies[i])) <= 1.0 / 6).ToArray();
            smooth[i] = band.Average(j => power[j]);
        }

        var reference = Enumerable.Range(0, frequencies.Length).Where(i => frequencies[i] >= 200 && frequencies[i] <= 8000).Average(i => smooth[i]);
        var before = smooth.Select(p => 10 * Math.Log10(p / reference)).ToArray();
        var hold = Array.FindIndex(frequencies, f => f >= 30);
        var target = new double[frequencies.Length];
        for (var i = 0; i < frequencies.Length; i++)
        {
            var correction = Math.Clamp(-before[Math.Max(i, hold)], MinCorrectionDb,
                frequencies[i] < TrebleFromHz ? MaxCorrectionDb : MaxTrebleBoostDb);
            if (frequencies[i] >= FadeEndHz)
            {
                correction = 0;
            }
            else if (frequencies[i] > FadeStartHz)
            {
                correction *= (FadeEndHz - frequencies[i]) / (FadeEndHz - FadeStartHz);
            }

            target[i] = correction;
        }

        var fitBand = Enumerable.Range(0, frequencies.Length).Where(i => frequencies[i] >= 25 && frequencies[i] <= FadeEndHz).ToArray();
        var filters = new List<Filter> { new(0, 150, 0.707, target[Array.FindIndex(frequencies, f => f >= 40)]) };
        Optimize(filters, frequencies, target, fitBand);
        while (filters.Count <= MaxPeaks && Rms(filters, frequencies, target, fitBand) > GoodEnoughDb)
        {
            var worst = fitBand.Where(i => frequencies[i] >= 40 && frequencies[i] <= FadeStartHz)
                .OrderByDescending(i => Math.Abs(target[i] - ResponseDb(filters, frequencies[i])))
                .First();
            filters.Add(new Filter(1, frequencies[worst], 1.4, target[worst] - ResponseDb(filters, frequencies[worst])));
            Optimize(filters, frequencies, target, fitBand);
        }

        return new Result(filters, Rms(filters, frequencies, target, fitBand), frequencies, before, target);
    }

    public static double ResponseDb(IEnumerable<Filter> filters, double frequency) =>
        filters.Sum(filter => FilterDb(filter, frequency));

    private static double FilterDb(Filter filter, double frequency) => FilterDb(Coefficients(filter, FitRate), frequency);

    private static double FilterDb((double B0, double B1, double B2, double A0, double A1, double A2) c, double frequency)
    {
        var w = 2 * Math.PI * frequency / FitRate;
        var z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var response = (c.B0 + (c.B1 * z1) + (c.B2 * z2)) / (c.A0 + (c.A1 * z1) + (c.A2 * z2));
        return 20 * Math.Log10(response.Magnitude);
    }

    private static double[] FilterCurve(Filter filter, double[] frequencies, int[] band)
    {
        var coefficients = Coefficients(filter, FitRate);
        var curve = new double[frequencies.Length];
        foreach (var i in band)
        {
            curve[i] = FilterDb(coefficients, frequencies[i]);
        }

        return curve;
    }

    // RBJ Audio EQ Cookbook. The engine uses the same formulas.
    public static (double B0, double B1, double B2, double A0, double A1, double A2) Coefficients(Filter filter, double rate)
    {
        var a = Math.Pow(10, filter.GainDb / 40);
        var w0 = 2 * Math.PI * filter.Frequency / rate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * filter.Q);
        if (filter.Type == 0)
        {
            var root = 2 * Math.Sqrt(a) * alpha;
            return (
                a * ((a + 1) - ((a - 1) * cos) + root),
                2 * a * ((a - 1) - ((a + 1) * cos)),
                a * ((a + 1) - ((a - 1) * cos) - root),
                (a + 1) + ((a - 1) * cos) + root,
                -2 * ((a - 1) + ((a + 1) * cos)),
                (a + 1) + ((a - 1) * cos) - root);
        }

        return (1 + (alpha * a), -2 * cos, 1 - (alpha * a), 1 + (alpha / a), -2 * cos, 1 - (alpha / a));
    }

    private static double Power(double[] response, double frequency, int rate)
    {
        double real = 0, imaginary = 0;
        for (var k = 0; k < response.Length; k++)
        {
            var phase = 2 * Math.PI * frequency * k / rate;
            real += response[k] * Math.Cos(phase);
            imaginary -= response[k] * Math.Sin(phase);
        }

        return (real * real) + (imaginary * imaginary);
    }

    private static double Rms(List<Filter> filters, double[] frequencies, double[] target, int[] band) =>
        Math.Sqrt(band.Average(i => Math.Pow(target[i] - ResponseDb(filters, frequencies[i]), 2)));

    private static Filter Clamp(Filter filter) => filter with
    {
        Frequency = Math.Clamp(filter.Frequency, 20, FadeStartHz),
        Q = filter.Type == 0 ? Math.Clamp(filter.Q, 0.5, 1.0) : Math.Clamp(filter.Q, 0.4, 4.0),
        GainDb = filter.Type == 0
            ? Math.Clamp(filter.GainDb, 0, 12)
            : Math.Clamp(filter.GainDb, -9, filter.Frequency < TrebleFromHz ? MaxCorrectionDb : MaxTrebleBoostDb),
    };

    private static void Optimize(List<Filter> filters, double[] frequencies, double[] target, int[] band)
    {
        // Each filter's curve is cached; a trial step recomputes only that filter.
        var curves = filters.Select(filter => FilterCurve(filter, frequencies, band)).ToList();
        var sum = new double[frequencies.Length];
        foreach (var curve in curves)
        {
            foreach (var i in band)
            {
                sum[i] += curve[i];
            }
        }

        double Error(double[] total) => Math.Sqrt(band.Average(i => Math.Pow(target[i] - total[i], 2)));

        var steps = new[] { 0.1, 0.1, 0.5 }; // octaves of frequency, log2 of Q, dB of gain
        var best = Error(sum);
        var trial = new double[frequencies.Length];
        for (var round = 0; round < 400 && steps[2] > 0.01; round++)
        {
            var improved = false;
            for (var index = 0; index < filters.Count; index++)
            {
                for (var parameter = 0; parameter < 3; parameter++)
                {
                    foreach (var direction in new[] { 1.0, -1.0 })
                    {
                        var current = filters[index];
                        var step = steps[parameter] * direction;
                        var candidate = Clamp(parameter switch
                        {
                            0 => current with { Frequency = current.Frequency * Math.Pow(2, step) },
                            1 => current with { Q = current.Q * Math.Pow(2, step) },
                            _ => current with { GainDb = current.GainDb + step },
                        });
                        var candidateCurve = FilterCurve(candidate, frequencies, band);
                        foreach (var i in band)
                        {
                            trial[i] = sum[i] - curves[index][i] + candidateCurve[i];
                        }

                        var error = Error(trial);
                        if (error < best - 1e-9)
                        {
                            best = error;
                            improved = true;
                            filters[index] = candidate;
                            curves[index] = candidateCurve;
                            foreach (var i in band)
                            {
                                sum[i] = trial[i];
                            }
                        }
                    }
                }
            }

            if (!improved)
            {
                for (var s = 0; s < steps.Length; s++)
                {
                    steps[s] /= 2;
                }
            }
        }
    }
}
