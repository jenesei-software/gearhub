namespace GearHub.Core.Models;

/// <summary>Raw device data a provider obtained during a scan.</summary>
public sealed record GearObservation
{
    /// <summary>Stable identifier, e.g. <c>xinput:0</c> or <c>ble:Bluetooth#...</c>.</summary>
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    /// <summary>Data source: "XInput", "Bluetooth LE", etc.</summary>
    public required string Source { get; init; }

    public GearKind Kind { get; init; } = GearKind.Other;

    public bool IsConnected { get; init; } = true;

    /// <summary>True if the device is connected but the provider could not read its data.</summary>
    public bool HasFault { get; init; }

    /// <summary>
    /// True for definitely real devices found in a reliable way (for example, behind a Logitech receiver).
    /// Such devices are shown in the widget even if battery and kind are unknown.
    /// </summary>
    public bool IsTrusted { get; init; }

    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;

    /// <summary>Free-form detail for the UI: "USB power", "AA batteries", etc.</summary>
    public string? Detail { get; init; }
}
