namespace GearHub.Core.Models;

/// <summary>Уровень заряда, который умеют отдавать некоторые устройства (например, XInput).</summary>
public enum CoarseBatteryLevel
{
    Unknown = 0,
    Empty,
    Low,
    Medium,
    High,
    Full,
}

/// <summary>Заряд устройства. Процент известен не всегда — тогда заполнен только <see cref="Coarse"/>.</summary>
public sealed record BatteryReading
{
    public static BatteryReading Unknown { get; } = new();

    public int? Percent { get; init; }

    public CoarseBatteryLevel Coarse { get; init; } = CoarseBatteryLevel.Unknown;

    public bool IsCharging { get; init; }

    public bool IsAvailable => Percent is not null || Coarse != CoarseBatteryLevel.Unknown;
}
