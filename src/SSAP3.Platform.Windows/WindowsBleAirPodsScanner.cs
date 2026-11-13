using System.Diagnostics;
using SSAP3.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace SSAP3.Platform.Windows;

// Adapted from the GPL-3.0 LibrePods Windows port.
public sealed class WindowsBleAirPodsScanner : IDisposable
{
    private const ushort AppleManufacturerId = 0x004C;
    private readonly AirPodsAdvertisementParser _parser = new();
    private readonly object _sync = new();
    private BluetoothLEAdvertisementWatcher _watcher;
    private bool _disposed;

    public WindowsBleAirPodsScanner()
    {
        _watcher = CreateWatcher(BluetoothLEScanningMode.Active);
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;
            }
        }
    }

    public byte[]? IdentityResolvingKey { get; set; }

    public byte[]? EncryptionKey { get; set; }

    public event EventHandler<AirPodsAdvertisementReceivedEventArgs>? AdvertisementReceived;

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Starts scanning. Returns false (and raises a status message) instead of
    /// throwing when the radio is off or unavailable, so callers can retry later.
    /// </summary>
    public bool Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                return true;
            }

            try
            {
                if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Aborted or
                    BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    ReplaceWatcher(BluetoothLEScanningMode.Active, stopPrevious: false);
                }

                _watcher.Start();
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                {
                    ReplaceWatcher(BluetoothLEScanningMode.Passive, stopPrevious: false);
                    _watcher.Start();
                }
            }
            catch (System.Runtime.InteropServices.COMException exception)
            {
                ReplaceWatcher(BluetoothLEScanningMode.Active, stopPrevious: false);
                RaiseStatus($"Bluetooth LE is unavailable (radio off?): 0x{exception.HResult:X8}");
                return false;
            }

            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
            {
                RaiseStatus("Windows aborted BLE scanning. Check Bluetooth and privacy settings.");
                return false;
            }
        }

        RaiseStatus($"BLE {_watcher.ScanningMode.ToString().ToLowerInvariant()} scanning started.");
        return true;
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed ||
                _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                return;
            }

            _watcher.Stop();
        }
    }

    public Task<bool> RestartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReplaceWatcher(BluetoothLEScanningMode.Active, stopPrevious: true);
        }

        return Task.FromResult(Start());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnWatcherStopped;
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
            }
        }
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = FormatAddress(args.BluetoothAddress);
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != AppleManufacturerId)
            {
                continue;
            }

            try
            {
                var bytes = ReadBuffer(section.Data);
                if (_parser.TryParse(
                        address,
                        bytes,
                        IdentityResolvingKey,
                        EncryptionKey,
                        out var status) &&
                    status is not null)
                {
                    AdvertisementReceived?.Invoke(
                        this,
                        new AirPodsAdvertisementReceivedEventArgs(status, args.RawSignalStrengthInDBm));
                }
            }
            catch (Exception exception)
            {
                RaiseStatus($"BLE {address}: {exception.Message}");
            }
        }
    }

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (TryStartPassiveFallback(sender, args.Error))
        {
            return;
        }

        RaiseStatus(args.Error switch
        {
            BluetoothError.Success => "BLE scanning stopped.",
            BluetoothError.RadioNotAvailable => "The Bluetooth adapter is unavailable.",
            BluetoothError.DisabledByUser => "Bluetooth is disabled in Windows.",
            BluetoothError.DisabledByPolicy => "Bluetooth is blocked by Windows policy.",
            BluetoothError.ConsentRequired => "Windows requires Bluetooth/location consent.",
            BluetoothError.NotSupported => "BLE scanning is not supported.",
            BluetoothError.ResourceInUse => "The Bluetooth adapter is busy.",
            _ => $"BLE stopped: {args.Error}",
        });
    }

    private bool TryStartPassiveFallback(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothError error)
    {
        if (error is not (BluetoothError.NotSupported or BluetoothError.ResourceInUse))
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed ||
                !ReferenceEquals(sender, _watcher) ||
                sender.ScanningMode != BluetoothLEScanningMode.Active)
            {
                return false;
            }

            ReplaceWatcher(BluetoothLEScanningMode.Passive, stopPrevious: false);
            _watcher.Start();
        }

        RaiseStatus("Active BLE scanning was unavailable; passive fallback started.");
        return true;
    }

    private BluetoothLEAdvertisementWatcher CreateWatcher(
        BluetoothLEScanningMode mode)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = mode,
        };
        watcher.Received += OnAdvertisementReceived;
        watcher.Stopped += OnWatcherStopped;
        return watcher;
    }

    private void ReplaceWatcher(
        BluetoothLEScanningMode mode,
        bool stopPrevious)
    {
        var previous = _watcher;
        previous.Received -= OnAdvertisementReceived;
        previous.Stopped -= OnWatcherStopped;
        if (stopPrevious &&
            previous.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            previous.Stop();
        }

        _watcher = CreateWatcher(mode);
    }

    private void RaiseStatus(string message)
    {
        try
        {
            StatusChanged?.Invoke(this, message);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        // Manufacturer data comes from any nearby advertiser: bound it before copying.
        if (buffer.Length > 255)
        {
            throw new InvalidDataException("Oversized manufacturer data section.");
        }

        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(
            ':',
            Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }
}

public sealed class AirPodsAdvertisementReceivedEventArgs : EventArgs
{
    public AirPodsAdvertisementReceivedEventArgs(AirPodsAdvertisementStatus status, short rssi)
    {
        Status = status;
        Rssi = rssi;
    }

    public AirPodsAdvertisementStatus Status { get; }

    /// <summary>Received signal strength in dBm.</summary>
    public short Rssi { get; }
}
