using System.IO.MemoryMappedFiles;
using Microsoft.Win32;

namespace SSAP3.Platform.Windows;

public enum Ssap3SpatialEngineMode
{
    Off = 0,
    Fixed = 1,
    Tracked = 2,
}

public sealed class HeadTrackingAudioStateWriter : IDisposable
{
    private const uint StateMagic = 0x54485353; // "SSHT"
    private const uint StateVersion = 1;
    private const long StateCapacity = 4096;
    private const string SpatialRegistryPath = @"SOFTWARE\SSAP3\SpatialAudio";

    private readonly object _syncRoot = new();
    private FileStream? _stateStream;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private int _sequence;
    private Ssap3SpatialEngineMode _mode;

    public HeadTrackingAudioStateWriter()
    {
        TryOpen();
    }

    public static string StatePath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "SSAP3",
            "head-tracking-state.bin");

    public static bool IsEngineInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    SpatialRegistryPath,
                    writable: false);
                var enabled = key?.GetValue("Enabled");
                var endpointCount = key?.GetValue("EndpointCount");
                return Convert.ToInt32(enabled) == 1 &&
                    Convert.ToInt32(endpointCount) > 0 &&
                    File.Exists(
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.ProgramFiles),
                            "EqualizerAPO",
                            "VSTPlugins",
                            "SSAP3Spatializer.dll"));
            }
            catch
            {
                return false;
            }
        }
    }

    public string? LastError { get; private set; }

    public bool IsAvailable => _view is not null;

    public void SetMode(Ssap3SpatialEngineMode mode)
    {
        _mode = mode;
        WriteState(mode, 0, 0, 0);
    }

    public void UpdateMotion(float yaw, float pitch, float roll = 0)
    {
        if (_mode != Ssap3SpatialEngineMode.Tracked)
        {
            return;
        }

        WriteState(_mode, yaw, pitch, roll);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _view?.Dispose();
            _mapping?.Dispose();
            _stateStream?.Dispose();
            _view = null;
            _mapping = null;
            _stateStream = null;
        }
    }

    private void TryOpen()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            _stateStream = new FileStream(
                StatePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            if (_stateStream.Length < StateCapacity)
            {
                _stateStream.SetLength(StateCapacity);
            }

            _mapping = MemoryMappedFile.CreateFromFile(
                _stateStream,
                mapName: null,
                capacity: StateCapacity,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);
            _view = _mapping.CreateViewAccessor(
                0,
                StateCapacity,
                MemoryMappedFileAccess.ReadWrite);
            WriteState(Ssap3SpatialEngineMode.Off, 0, 0, 0);
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            _view?.Dispose();
            _mapping?.Dispose();
            _stateStream?.Dispose();
            _view = null;
            _mapping = null;
            _stateStream = null;
        }
    }

    private void WriteState(
        Ssap3SpatialEngineMode mode,
        float yaw,
        float pitch,
        float roll)
    {
        lock (_syncRoot)
        {
            if (_view is null)
            {
                TryOpen();
            }

            if (_view is null)
            {
                return;
            }

            var oddSequence = unchecked(_sequence + 1) | 1;
            var evenSequence = unchecked(oddSequence + 1) & ~1;
            _view.Write(8, oddSequence);
            Thread.MemoryBarrier();
            _view.Write(0, StateMagic);
            _view.Write(4, StateVersion);
            _view.Write(12, (int)mode);
            _view.Write(16, yaw);
            _view.Write(20, pitch);
            _view.Write(24, roll);
            _view.Write(28, 0f);
            _view.Write(
                32,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Thread.MemoryBarrier();
            _view.Write(8, evenSequence);
            _sequence = evenSequence;
        }
    }
}
