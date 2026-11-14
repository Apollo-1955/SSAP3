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
text.AppendLine("} // namespace ssap3::hrir");
File.WriteAllText(output, text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

Console.WriteLine($"Wrote {output}: {ordered.Count} directions x 2 ears x {Taps} taps; minimum retained energy {minimumRetained:P2}; scale {scale:G6}.");
Console.WriteLine($"Right-side check at 0/90: right ear {10 * Math.Log10(Energy(side.Right) / Energy(side.Left)):0.0} dB louder, {Onset(side.Left, OnsetThreshold) - Onset(side.Right, OnsetThreshold)} samples earlier.");
Console.WriteLine($"Max onset shift: {ordered.Max(e => Math.Max(e.Shift[0], e.Shift[1]))} samples; source SHA-256 {archiveHash}.");
return 0;
