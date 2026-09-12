using GearHub.Core.Abstractions;
using GearHub.Core.Models;

namespace GearHub.Providers.Windows.XInput;

/// <summary>
/// Xbox-геймпады: подключение и заряд через XInput.
/// XInput отдаёт только огрублённый уровень (пусто/низкий/средний/полный), без процентов.
/// </summary>
public sealed class XInputGearProvider : IGearProvider
{
    public string ProviderName => "XInput (Xbox)";

    public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GearObservation> result = XInputNative.IsAvailable ? Scan() : [];
        return Task.FromResult(result);
    }

    private static List<GearObservation> Scan()
    {
        var devices = new List<GearObservation>();

        for (var slot = 0; slot < XInputNative.MaxSlots; slot++)
        {
            if (!XInputNative.TryGetState(slot, out _))
            {
                continue;
            }

            var battery = BatteryReading.Unknown;
            string? detail = null;

            if (XInputNative.TryGetBatteryInfo(slot, out var info))
            {
                (battery, detail) = MapBattery(info);
            }

            devices.Add(new GearObservation
            {
                DeviceId = $"xinput:{slot}",
                Name = $"Xbox-геймпад (слот {slot + 1})",
                Source = "XInput",
                Kind = GearKind.Gamepad,
                IsConnected = true,
                Battery = battery,
                Detail = detail,
            });
        }

        return devices;
    }

    private static (BatteryReading Battery, string? Detail) MapBattery(XInputBatteryInformation info) => info.BatteryType switch
    {
        XInputNative.BatteryTypeWired => (BatteryReading.Unknown, "Питание от USB"),
        XInputNative.BatteryTypeDisconnected => (BatteryReading.Unknown, null),
        _ => (
            new BatteryReading
            {
                Coarse = info.BatteryLevel switch
                {
                    XInputNative.BatteryLevelEmpty => CoarseBatteryLevel.Empty,
                    XInputNative.BatteryLevelLow => CoarseBatteryLevel.Low,
                    XInputNative.BatteryLevelMedium => CoarseBatteryLevel.Medium,
                    XInputNative.BatteryLevelFull => CoarseBatteryLevel.Full,
                    _ => CoarseBatteryLevel.Unknown,
                },
            },
            info.BatteryType == XInputNative.BatteryTypeAlkaline ? "Батарейки AA" : "Аккумулятор"),
    };
}
