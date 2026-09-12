using GearHub.Core.Models;

namespace GearHub.Core.Services;

/// <summary>
/// Отсеивает системный «мусор»: сервисы BLE, энумераторы, виртуальные узлы HID и т.п.
/// Показывает только то, что похоже на реальную периферию: известный класс устройства или есть данные о заряде.
/// </summary>
public sealed class DeviceFilter
{
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bluetooth LE Generic Attribute Service",
        "Generic Access Profile",
        "Generic Attribute Profile",
        "Microsoft Bluetooth LE Enumerator",
        "Microsoft Device Association Root Enumerator",
        "Microsoft RRAS Root Enumerator",
        "Bluetooth Device (RFCOMM Protocol TDI)",
        "USB Input Device",
        "HID-compliant device",
        "HID-compliant consumer control device",
        "HID-compliant system controller",
        "HID-compliant vendor-defined device",
        "AudioEndpoint",
        "Virtual HID Device",
    };

    private static readonly string[] JunkSuffixes = [" service", " service (", " profile", " enumerator"];

    public bool ShouldHide(GearObservation device, IReadOnlySet<string> ignoredIds)
    {
        if (ignoredIds.Contains(device.DeviceId))
        {
            return true;
        }

        var name = device.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        var looksLikeJunk = JunkNames.Contains(name)
            || JunkSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (looksLikeJunk && device.Kind == GearKind.Other)
        {
            return true;
        }

        // Устройство неизвестного класса без заряда и без ошибок — почти наверняка служебный узел.
        if (device.Kind == GearKind.Other && !device.Battery.IsAvailable && !device.HasFault)
        {
            return true;
        }

        return false;
    }
}
