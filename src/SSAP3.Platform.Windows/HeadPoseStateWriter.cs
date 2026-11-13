using System.IO.MemoryMappedFiles;
using Microsoft.Win32;
using SSAP3.Core.Spatial;

namespace SSAP3.Platform.Windows;

/// <summary>
/// Publishes <see cref="SpatialEngineState"/> to the SSAP3 Spatial Engine
/// through the v2 shared-state record.
///
/// The writer only opens the file that Setup created in
/// %ProgramData%\SSAP3\Runtime (an admin-owned folder in which interactive
/// users may write that one file, but not create, delete or replace files).
/// It never creates folders or files itself, which closes the ProgramData
/// squatting path described in SECURITY_AUDIT.md (S-02).
/// </summary>
public sealed class HeadPoseStateWriter : IDisposable
{
    public const string SpatialRegistryPath = @"SOFTWARE\SSAP3\SpatialAudio";

    private readonly object _sync = new();
    private readonly string _path;
    private readonly byte[] _record = new byte[SharedStateLayout.RecordSize];
    private FileStream? _stream;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private int _sequence;

    public HeadPoseStateWriter(string? path = null)
    {
        _path = path ?? DefaultStatePath;
        TryOpen();
    }

    public static string DefaultStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SSAP3",
        "Runtime",
        "head-pose.bin");

    /// <summary>True when Setup registered the opt-in system-wide engine and its plug-in file exists.</summary>
    public static bool IsEngineInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SpatialRegistryPath, writable: false);
                var enabled = key?.GetValue("Enabled") is int value && value == 1;
                var pluginPath = key?.GetValue("PluginPath") as string;
                return enabled && !string.IsNullOrWhiteSpace(pluginPath) && File.Exists(pluginPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return _view is not null;
            }
        }
    }

    public string? LastError { get; private set; }

    public void Write(in SpatialEngineState state)
    {
        lock (_sync)
        {
            if (_view is null && !TryOpen())
            {
                return;
            }

            SharedStateLayout.WritePayload(_record, state, (ulong)Environment.TickCount64);
            var odd = unchecked(_sequence + 1) | 1;
            var even = unchecked(odd + 1) & ~1;
            var view = _view!;
            view.Write(SharedStateLayout.SequenceOffset, odd);
            Thread.MemoryBarrier();
            view.WriteArray(0, _record, 0, SharedStateLayout.SequenceOffset);
            view.WriteArray(
                SharedStateLayout.ModeOffset,
                _record,
                SharedStateLayout.ModeOffset,
                SharedStateLayout.RecordSize - SharedStateLayout.ModeOffset);
            Thread.MemoryBarrier();
            view.Write(SharedStateLayout.SequenceOffset, even);
            _sequence = even;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Close();
        }
    }

    private bool TryOpen()
    {
        try
        {
            if (!File.Exists(_path))
            {
                LastError = "The SSAP3 spatial engine is not installed (runtime state file missing).";
                return false;
            }

            _stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (_stream.Length < SharedStateLayout.RecordSize)
            {
                throw new InvalidDataException("The SSAP3 runtime state file is truncated.");
            }

            _mapping = MemoryMappedFile.CreateFromFile(
                _stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);
            _view = _mapping.CreateViewAccessor(0, SharedStateLayout.RecordSize, MemoryMappedFileAccess.ReadWrite);
            _sequence = _view.ReadInt32(SharedStateLayout.SequenceOffset) & ~1;
            LastError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LastError = exception.Message;
            Close();
            return false;
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _mapping?.Dispose();
        _stream?.Dispose();
        _view = null;
        _mapping = null;
        _stream = null;
    }
}
