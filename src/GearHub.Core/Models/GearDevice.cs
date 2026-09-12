namespace GearHub.Core.Models;

/// <summary>Итоговое состояние устройства, которое отображается в виджете.</summary>
public sealed record GearDevice
{
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    public required string Source { get; init; }

    public GearKind Kind { get; init; } = GearKind.Other;

    public bool IsConnected { get; init; }

    public bool HasFault { get; init; }

    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;

    public string? Detail { get; init; }

    public DateTimeOffset LastSeenUtc { get; init; }

    public GearStatus Status { get; init; }
}
