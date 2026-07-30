using System.Diagnostics;
using SoundStage.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace SoundStage.Platform.Windows;

// Adapted from the GPL-3.0 LibrePods Windows port.
public sealed class WindowsBleAirPodsScanner : IDisposable
{
    private const ushort AppleManufacturerId = 0x004C;
    private readonly AirPodsAdvertisementParser _parser = new();
    private readonly BluetoothLEAdvertisementWatcher _watcher;

    public WindowsBleAirPodsScanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    public bool IsRunning =>
        _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public byte[]? IdentityResolvingKey { get; set; }

    public byte[]? EncryptionKey { get; set; }

    public event EventHandler<AirPodsAdvertisementStatus>? AdvertisementReceived;

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _watcher.Start();
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
        {
            throw new InvalidOperationException(
                "Windows aborted BLE scanning. Check Bluetooth and privacy settings.");
        }

        RaiseStatus("BLE scanning started. Open the AirPods case.");
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _watcher.Stop();
    }

    public void Dispose()
    {
        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
        Stop();
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
                    AdvertisementReceived?.Invoke(this, status);
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
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(
            ':',
            Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }
}
