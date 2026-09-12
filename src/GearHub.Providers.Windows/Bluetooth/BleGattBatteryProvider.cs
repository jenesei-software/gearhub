using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace GearHub.Providers.Windows.Bluetooth;

/// <summary>
/// Bluetooth-устройства с Battery Service (GATT 0x180F / characteristic 0x2A19).
/// Так отдают заряд многие BLE-мыши, клавиатуры и часть наушников.
/// Устройства без этой службы показываются без процента — это не ошибка.
/// </summary>
public sealed class BleGattBatteryProvider : IGearProvider, IDisposable
{
    private readonly Dictionary<string, BluetoothLEDevice> _deviceCache = new(StringComparer.Ordinal);

    public string ProviderName => "Bluetooth LE";

    public async Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var result = new List<GearObservation>();

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            return result;
        }

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var pairedDevices = await DeviceInformation.FindAllAsync(selector);

        foreach (var info in pairedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var device = await GetOrCreateDeviceAsync(info.Id);
                if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    continue;
                }

                var name = FirstNonEmpty(info.Name, device.Name) ?? "Bluetooth-устройство";
                var probe = await TryReadBatteryAsync(device);

                result.Add(new GearObservation
                {
                    DeviceId = "ble:" + info.Id,
                    Name = name,
                    Source = "Bluetooth LE",
                    Kind = GearKindClassifier.Classify(name),
                    IsConnected = true,
                    HasFault = probe.HasFault,
                    Battery = probe.Battery,
                    Detail = probe.Detail,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Одно проблемное устройство не должно ломать весь скан.
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var device in _deviceCache.Values)
        {
            try
            {
                device.Dispose();
            }
            catch
            {
                // Ignore.
            }
        }

        _deviceCache.Clear();
    }

    private async Task<BluetoothLEDevice?> GetOrCreateDeviceAsync(string deviceId)
    {
        if (_deviceCache.TryGetValue(deviceId, out var cached))
        {
            return cached;
        }

        var device = await BluetoothLEDevice.FromIdAsync(deviceId);
        if (device is not null)
        {
            _deviceCache[deviceId] = device;
        }

        return device;
    }

    private static async Task<BatteryProbe> TryReadBatteryAsync(BluetoothLEDevice device)
    {
        GattDeviceServicesResult services;
        try
        {
            services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
        }
        catch
        {
            // Классический Bluetooth (не LE) или GATT недоступен — данных о заряде просто нет.
            return BatteryProbe.NoData;
        }

        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            return BatteryProbe.NoData;
        }

        try
        {
            var service = services.Services[0];
            var characteristics = await service.GetCharacteristicsForUuidAsync(
                GattCharacteristicUuids.BatteryLevel,
                BluetoothCacheMode.Cached);

            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
            {
                return BatteryProbe.NoData;
            }

            var read = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            var level = DataReader.FromBuffer(read.Value).ReadByte();
            return new BatteryProbe(new BatteryReading { Percent = Math.Clamp((int)level, 0, 100) }, HasFault: false, Detail: null);
        }
        catch (Exception ex)
        {
            return BatteryProbe.Faulted($"Ошибка GATT: {ex.Message}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private readonly record struct BatteryProbe(BatteryReading Battery, bool HasFault, string? Detail)
    {
        public static BatteryProbe NoData { get; } = new(BatteryReading.Unknown, HasFault: false, Detail: null);

        public static BatteryProbe Faulted(string detail) => new(BatteryReading.Unknown, HasFault: true, detail);
    }
}
