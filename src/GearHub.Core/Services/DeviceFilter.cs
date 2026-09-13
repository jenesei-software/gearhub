using GearHub.Core.Models;

namespace GearHub.Core.Services;

/// <summary>
/// Filters out system "junk": BLE services, enumerators, virtual HID nodes, etc.
/// Shows only what looks like real peripherals: a known device kind or battery data present.
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

    private static readonly string[] JunkPrefixes =
    [
        "Logitech G HUB Virtual",
        "Virtual HID",
        "Virtual Keyboard",
        "Virtual Mouse",
    ];

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

        if (JunkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var looksLikeJunk = JunkNames.Contains(name)
            || JunkSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (looksLikeJunk && device.Kind == GearKind.Other)
        {
            return true;
        }

        // A device of unknown kind with no battery and no faults is almost certainly a service node.
        // Exception: devices found in a reliable way (IsTrusted) — those are always shown.
        if (device.Kind == GearKind.Other && !device.Battery.IsAvailable && !device.HasFault && !device.IsTrusted)
        {
            return true;
        }

        return false;
    }
}
