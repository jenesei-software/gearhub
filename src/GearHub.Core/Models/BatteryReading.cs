namespace GearHub.Core.Models;

/// <summary>Coarse battery level that some devices report (for example, XInput).</summary>
public enum CoarseBatteryLevel
{
    Unknown = 0,
    Empty,
    Low,
    Medium,
    High,
    Full,
}

/// <summary>Device battery. The percentage is not always known — then only <see cref="Coarse"/> is set.</summary>
public sealed record BatteryReading
{
    public static BatteryReading Unknown { get; } = new();

    public int? Percent { get; init; }

    public CoarseBatteryLevel Coarse { get; init; } = CoarseBatteryLevel.Unknown;

    public bool IsCharging { get; init; }

    public bool IsAvailable => Percent is not null || Coarse != CoarseBatteryLevel.Unknown;
}
