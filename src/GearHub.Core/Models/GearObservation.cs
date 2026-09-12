namespace GearHub.Core.Models;

/// <summary>Сырые данные об устройстве, которые провайдер получил в ходе сканирования.</summary>
public sealed record GearObservation
{
    /// <summary>Стабильный идентификатор, например <c>xinput:0</c> или <c>ble:Bluetooth#...</c>.</summary>
    public required string DeviceId { get; init; }

    public required string Name { get; init; }

    /// <summary>Источник данных: «XInput», «Bluetooth LE» и т.п.</summary>
    public required string Source { get; init; }

    public GearKind Kind { get; init; } = GearKind.Other;

    public bool IsConnected { get; init; } = true;

    /// <summary>True, если устройство подключено, но провайдер не смог прочитать данные о нём.</summary>
    public bool HasFault { get; init; }

    public BatteryReading Battery { get; init; } = BatteryReading.Unknown;

    /// <summary>Произвольная подробность для UI: «Питание от USB», «Батарейки AA» и т.п.</summary>
    public string? Detail { get; init; }
}
