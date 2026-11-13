using Windows.Devices.Radios;

namespace SSAP3.Platform.Windows;

public enum BluetoothRadioState
{
    Unknown,
    On,
    Off,
    NotPresent,
}

/// <summary>Read-only Bluetooth radio state. SSAP3 never toggles the radio itself.</summary>
public static class WindowsBluetoothRadio
{
    /// <summary>Blocking query; call it from a background thread only.</summary>
    public static BluetoothRadioState Query()
    {
        try
        {
            var radios = Radio.GetRadiosAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var bluetooth = radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToList();
            if (bluetooth.Count == 0)
            {
                return BluetoothRadioState.NotPresent;
            }

            return bluetooth.Any(radio => radio.State == RadioState.On) ? BluetoothRadioState.On : BluetoothRadioState.Off;
        }
        catch (Exception exception) when (exception is TimeoutException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return BluetoothRadioState.Unknown;
        }
    }
}
