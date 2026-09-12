using GearHub.Core.Models;

namespace GearHub.Core.Abstractions;

/// <summary>Источник данных о подключённой периферии (XInput, Bluetooth LE, HID++ и т.д.).</summary>
public interface IGearProvider
{
    string ProviderName { get; }

    /// <summary>Возвращает всё, что провайдер видит прямо сейчас. Не должен бросать исключения
    /// из-за одного сломанного устройства.</summary>
    Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken);
}
