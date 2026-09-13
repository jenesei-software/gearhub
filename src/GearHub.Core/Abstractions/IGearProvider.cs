using GearHub.Core.Models;

namespace GearHub.Core.Abstractions;

/// <summary>Source of data about connected peripherals (XInput, Bluetooth LE, HID++, etc.).</summary>
public interface IGearProvider
{
    string ProviderName { get; }

    /// <summary>Returns everything the provider sees right now. Must not throw because of
    /// a single broken device.</summary>
    Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken);
}
