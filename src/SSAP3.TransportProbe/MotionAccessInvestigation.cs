using System.Globalization;
using System.Text;
using SSAP3.Core;
using SSAP3.Platform.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Storage.Streams;

namespace SSAP3.TransportProbe;

/// <summary>
/// Read-only check of every documented Windows interface that could carry
/// AirPods motion data without SSAP3's kernel driver. Nothing is written to the
/// AirPods: it reads cached SDP records, enumerates RFCOMM services and GATT
/// services/characteristics (no values are read), lists HID and sensor devices,
/// and tries to create a user-mode L2CAP socket. Output is redacted.
/// </summary>
internal static class MotionAccessInvestigation
{
    private const string HidInterfaceClass = "{4D1E55B2-F16F-11CF-88CB-001111000030}";
    private const string AapServiceUuid = "74ec2172-0bad-4d01-8f77-997b2be0722a";

    public static async Task<int> RunAsync(string? requestedAddress)
    {
        var remembered = WindowsRememberedAirPodsFinder.FindBest();
        var address = requestedAddress ?? remembered?.Address;
        var names = remembered is null ? [] : new[] { remembered.Name };
        string R(string text) => DiagnosticRedactor.Redact(text, names);

        Console.WriteLine("SSAP3 motion-access investigation (read-only)");
        Console.WriteLine($"Windows {Environment.OSVersion.Version}; AirPods classic address: {R(address ?? "not found")}");
        if (address is null || !BluetoothCryptography.TryParseMacAddress(address, out var octets))
        {
            Console.WriteLine("No paired AirPods found. Pair them in Windows Settings first.");
            return 2;
        }

        var bluetoothAddress = octets.Aggregate(0UL, (value, octet) => (value << 8) | octet);

        Section("1. User-mode L2CAP socket (Winsock AF_BTH, BTHPROTO_L2CAP)");
        var l2cap = WindowsL2CapAacpClient.TryConnect(address);
        Console.WriteLine($"   supported={l2cap.Supported} error={l2cap.ErrorCode}: {R(l2cap.Message)}");
        l2cap.Dispose();

        Section("2. Classic Bluetooth device and its SDP service records");
        var device = await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress);
        if (device is null)
        {
            Console.WriteLine("   Windows returned no BluetoothDevice for this address.");
        }
        else
        {
            using (device)
            {
                Console.WriteLine($"   connection={device.ConnectionStatus} class=0x{device.ClassOfDevice.RawValue:X6} sdpRecords={device.SdpRecords.Count}");
                foreach (var record in device.SdpRecords)
                {
                    Console.WriteLine("   " + SdpRecordSummary.Describe(ToArray(record)));
                }

                Section("3. RFCOMM services (uncached SDP query)");
                try
                {
                    var rfcomm = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                    Console.WriteLine($"   error={rfcomm.Error} services={rfcomm.Services.Count}");
                    foreach (var service in rfcomm.Services)
                    {
                        Console.WriteLine($"   - {service.ServiceId.Uuid} ({service.ConnectionServiceName})");
                        service.Dispose();
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    Console.WriteLine($"   query failed: {exception.Message}");
                }
            }
        }

        Section("4. Bluetooth LE: paired LE devices named AirPods, GATT services and characteristics");
        var leDevices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        var airPodsLe = leDevices.Where(info => info.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"   paired LE devices: {leDevices.Count}; named AirPods: {airPodsLe.Count}");
        foreach (var info in airPodsLe)
        {
            using var le = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (le is null)
            {
                Console.WriteLine("   - could not open the LE device");
                continue;
            }

            var services = await le.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            Console.WriteLine($"   - LE device: connection={le.ConnectionStatus} gatt={services.Status} services={services.Services.Count}");
            foreach (var service in services.Services)
            {
                using (service)
                {
                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    Console.WriteLine($"     service {service.Uuid} ({characteristics.Status}, {characteristics.Characteristics.Count} characteristics)");
                    foreach (var characteristic in characteristics.Characteristics)
                    {
                        Console.WriteLine($"       characteristic {characteristic.Uuid} [{characteristic.CharacteristicProperties}]");
                    }
                }
            }
        }

        Section("5. HID interfaces belonging to Bluetooth devices");
        var hid = await DeviceInformation.FindAllAsync(
            $"System.Devices.InterfaceClassGuid:=\"{HidInterfaceClass}\"",
            ["System.Devices.DeviceInstanceId"]);
        var bluetoothHid = hid
            .Select(info => info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id) ? id as string ?? string.Empty : string.Empty)
            .Where(id => id.Contains("BTH", StringComparison.OrdinalIgnoreCase) || id.Contains("004C", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"   HID interfaces: {hid.Count}; from Bluetooth or VID 004C: {bluetoothHid.Count}");
        foreach (var id in bluetoothHid)
        {
            Console.WriteLine($"   - {R(id)}");
        }

        Section("6. Windows sensor platform (accelerometer, gyrometer, orientation)");
        await ListSensors("accelerometer", Accelerometer.GetDeviceSelector(AccelerometerReadingType.Standard), R);
        await ListSensors("gyrometer", Gyrometer.GetDeviceSelector(), R);
        await ListSensors("orientation", OrientationSensor.GetDeviceSelector(SensorReadingType.Absolute), R);

        Section("7. Device interfaces on the AirPods' AAP service node");
        var aap = await DeviceInformation.FindAllAsync(
            $"System.Devices.DeviceInstanceId:~~\"{AapServiceUuid}\"",
            ["System.Devices.DeviceInstanceId"],
            DeviceInformationKind.DeviceInterface);
        Console.WriteLine($"   device interfaces exposed by any driver on the AAP node: {aap.Count}");
        Console.WriteLine();
        Console.WriteLine("Done. Nothing was sent to the AirPods.");
        return 0;
    }

    private static async Task ListSensors(string kind, string selector, Func<string, string> redact)
    {
        var sensors = await DeviceInformation.FindAllAsync(selector, ["System.Devices.DeviceInstanceId"]);
        var ids = sensors
            .Select(info => info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id) ? id as string ?? "?" : "?")
            .ToList();
        var bluetooth = ids.Count(id => id.Contains("BTH", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"   {kind}: {ids.Count} sensor(s), {bluetooth} from Bluetooth{(ids.Count > 0 ? ": " + string.Join(", ", ids.Select(redact)) : string.Empty)}");
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    private static byte[] ToArray(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

/// <summary>Minimal bounded SDP data-element reader: service classes, protocols and the service name.</summary>
internal static class SdpRecordSummary
{
    private const int MaximumDepth = 8;

    public static string Describe(byte[] record)
    {
        try
        {
            var offset = 0;
            if (!TryRead(record, ref offset, 0, out var root) || root is not List<object?> attributes)
            {
                return $"record ({record.Length} B): not a valid SDP sequence";
            }

            var classes = new List<string>();
            var protocols = new List<string>();
            var name = string.Empty;
            for (var index = 0; index + 1 < attributes.Count; index += 2)
            {
                if (attributes[index] is not ulong id)
                {
                    continue;
                }

                switch (id)
                {
                    case 0x0001 when attributes[index + 1] is List<object?> list:
                        classes.AddRange(list.OfType<Guid>().Select(Short));
                        break;
                    case 0x0004 when attributes[index + 1] is List<object?> stack:
                        foreach (var layer in stack.OfType<List<object?>>())
                        {
                            var uuid = layer.OfType<Guid>().FirstOrDefault();
                            var parameter = layer.OfType<ulong>().Select(value => $" 0x{value:X}").FirstOrDefault() ?? string.Empty;
                            protocols.Add(Short(uuid) switch
                            {
                                "0100" => "L2CAP" + (parameter.Length > 0 ? " PSM" + parameter : string.Empty),
                                "0003" => "RFCOMM" + (parameter.Length > 0 ? " channel" + parameter : string.Empty),
                                "0019" => "AVDTP",
                                "0017" => "AVCTP",
                                var other => other + parameter,
                            });
                        }

                        break;
                    case 0x0100 when attributes[index + 1] is string text:
                        name = text;
                        break;
                }
            }

            return $"service [{string.Join(", ", classes)}] over [{string.Join(" / ", protocols)}]{(name.Length > 0 ? $" \"{name}\"" : string.Empty)}";
        }
        catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return $"record ({record.Length} B): malformed ({exception.GetType().Name})";
        }
    }

    private static string Short(Guid uuid)
    {
        var text = uuid.ToString();
        return text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) && text.StartsWith("0000", StringComparison.Ordinal)
            ? text.Substring(4, 4).ToUpperInvariant()
            : text;
    }

    private static bool TryRead(byte[] data, ref int offset, int depth, out object? value)
    {
        value = null;
        if (depth > MaximumDepth || offset >= data.Length)
        {
            return false;
        }

        var header = data[offset++];
        var type = header >> 3;
        var sizeIndex = header & 0x07;
        int length;
        if (type == 0)
        {
            return true;
        }

        if (sizeIndex <= 4)
        {
            length = sizeIndex switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 16 };
        }
        else
        {
            var lengthBytes = sizeIndex switch { 5 => 1, 6 => 2, _ => 4 };
            if (offset + lengthBytes > data.Length)
            {
                return false;
            }

            length = 0;
            for (var index = 0; index < lengthBytes; index++)
            {
                length = checked((length << 8) | data[offset++]);
            }
        }

        if (length < 0 || offset + length > data.Length)
        {
            return false;
        }

        var body = data.AsSpan(offset, length);
        var end = offset + length;
        switch (type)
        {
            case 1:
            case 2:
                ulong number = 0;
                foreach (var b in body[..Math.Min(8, body.Length)])
                {
                    number = (number << 8) | b;
                }

                value = number;
                break;
            case 3:
                value = length switch
                {
                    2 => new Guid($"0000{body[0]:x2}{body[1]:x2}-0000-1000-8000-00805f9b34fb"),
                    4 => new Guid($"{body[0]:x2}{body[1]:x2}{body[2]:x2}{body[3]:x2}-0000-1000-8000-00805f9b34fb"),
                    16 => new Guid(Convert.ToHexString(body).Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-")),
                    _ => null,
                };
                break;
            case 4:
            case 8:
                value = Encoding.UTF8.GetString(body).TrimEnd('\0');
                break;
            case 5:
                value = body.Length > 0 && body[0] != 0;
                break;
            case 6:
            case 7:
                var items = new List<object?>();
                var inner = offset;
                while (inner < end)
                {
                    if (!TryRead(data[..end], ref inner, depth + 1, out var item))
                    {
                        return false;
                    }

                    items.Add(item);
                }

                value = items;
                break;
            default:
                value = $"type{type.ToString(CultureInfo.InvariantCulture)}";
                break;
        }

        offset = end;
        return true;
    }
}
