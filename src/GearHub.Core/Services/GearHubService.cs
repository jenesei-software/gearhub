using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Persistence;

namespace GearHub.Core.Services;

/// <summary>
/// Собирает данные всех провайдеров, ведёт историю присутствия и вычисляет статусы устройств.
/// </summary>
public sealed class GearHubService
{
    private readonly IReadOnlyList<IGearProvider> _providers;
    private readonly IPresenceStore _store;
    private readonly DeviceFilter _filter;
    private readonly StatusPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public GearHubService(
        IEnumerable<IGearProvider> providers,
        IPresenceStore store,
        DeviceFilter filter,
        StatusPolicy policy,
        TimeProvider? timeProvider = null)
    {
        _providers = providers.ToList();
        _store = store;
        _filter = filter;
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Один цикл сканирования: опрос провайдеров → merge → статусы.</summary>
    public async Task<GearSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Скрыть устройство из виджета (или вернуть обратно).</summary>
    public void SetIgnored(string deviceId, bool ignored = true) => _store.SetIgnored(deviceId, ignored);

    private async Task<GearSnapshot> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var errors = new List<string>();
        var observations = new List<GearObservation>();

        foreach (var provider in _providers)
        {
            try
            {
                observations.AddRange(await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.ProviderName}: {ex.Message}");
            }
        }

        var ignored = _store.IgnoredIds;
        var records = _store.GetAll().ToDictionary(record => record.DeviceId, StringComparer.Ordinal);
        var observedIds = new HashSet<string>(StringComparer.Ordinal);
        var devices = new List<GearDevice>();

        foreach (var observation in observations)
        {
            if (_filter.ShouldHide(observation, ignored))
            {
                continue;
            }

            observedIds.Add(observation.DeviceId);

            var previous = records.GetValueOrDefault(observation.DeviceId);
            var lastSeen = observation.IsConnected ? now : previous?.LastSeenUtc ?? now;

            records[observation.DeviceId] = new GearPresenceRecord
            {
                DeviceId = observation.DeviceId,
                Name = observation.Name,
                Source = observation.Source,
                Kind = observation.Kind,
                LastSeenUtc = lastSeen,
                Battery = observation.Battery,
                Detail = observation.Detail,
                IsTrusted = observation.IsTrusted,
            };

            devices.Add(ToDevice(
                observation,
                lastSeen,
                _policy.Evaluate(observation.IsConnected, observation.HasFault, lastSeen, now)));
        }

        // Устройства, которых сейчас нет среди наблюдений: берём из истории и красим в жёлтый/красный.
        foreach (var record in records.Values)
        {
            if (observedIds.Contains(record.DeviceId))
            {
                continue;
            }

            var stub = new GearObservation
            {
                DeviceId = record.DeviceId,
                Name = record.Name,
                Source = record.Source,
                Kind = record.Kind,
                IsConnected = false,
                Battery = record.Battery,
                Detail = record.Detail,
                IsTrusted = record.IsTrusted,
            };

            if (_filter.ShouldHide(stub, ignored))
            {
                continue;
            }

            devices.Add(ToDevice(stub, record.LastSeenUtc, _policy.Evaluate(false, false, record.LastSeenUtc, now)));
        }

        _store.Replace(records.Values);

        var sorted = devices
            .OrderBy(device => (int)device.Status)
            .ThenBy(device => (int)device.Kind)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new GearSnapshot(sorted, errors);
    }

    private static GearDevice ToDevice(GearObservation observation, DateTimeOffset lastSeenUtc, GearStatus status) => new()
    {
        DeviceId = observation.DeviceId,
        Name = observation.Name,
        Source = observation.Source,
        Kind = observation.Kind,
        IsConnected = observation.IsConnected,
        HasFault = observation.HasFault,
        IsTrusted = observation.IsTrusted,
        Battery = observation.Battery,
        Detail = observation.Detail,
        LastSeenUtc = lastSeenUtc,
        Status = status,
    };
}
