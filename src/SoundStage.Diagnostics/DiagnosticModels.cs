using SoundStage.Core;

namespace SoundStage.Diagnostics;

public sealed record WindowsInfo(
    string ProductName,
    string DisplayVersion,
    int CurrentBuild,
    int UpdateBuildRevision,
    string Architecture,
    uint CodeIntegrityOptions,
    bool TestSigningActive,
    bool? SecureBootEnabled);

public sealed record BluetoothRadioInfo(
    string Name,
    string Address,
    uint ClassOfDevice,
    ushort Manufacturer,
    ushort LmpSubversion);

public sealed record BluetoothDeviceInfo(
    string Name,
    string Address,
    bool Connected,
    bool Remembered,
    bool Authenticated,
    DateTime? LastSeen,
    DateTime? LastUsed,
    IReadOnlyList<string> InstalledServiceGuids);

public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    string Flow,
    uint State,
    IReadOnlyList<string> DefaultRoles);

public sealed record CommandResult(
    string FileName,
    string Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record DiagnosticReport(
    DateTimeOffset CollectedAt,
    WindowsInfo Windows,
    IReadOnlyList<BluetoothRadioInfo> BluetoothRadios,
    IReadOnlyList<BluetoothDeviceInfo> BluetoothDevices,
    IReadOnlyList<AudioEndpointInfo> AudioEndpoints,
    AudioRouteRecommendation Recommendation,
    IReadOnlyList<CommandResult> Commands,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Errors);
