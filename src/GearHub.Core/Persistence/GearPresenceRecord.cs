using GearHub.Core.Models;

namespace GearHub.Core.Persistence;

/// <summary>Persisted device state: when it was last seen connected and with what battery level.</summary>
public sealed record GearPresenceRecord
{
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    public required string Source { get; init; }

    public GearKind Kind { get; init; } = GearKind.Other;

    public DateTimeOffset LastSeenUtc { get; init; }

    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;

    public string? Detail { get; init; }

    public bool IsTrusted { get; init; }
}
