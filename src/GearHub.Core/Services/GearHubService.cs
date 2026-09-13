using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Persistence;

namespace GearHub.Core.Services;

/// <summary>
/// Collects data from all providers, keeps presence history, and computes device statuses.
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

    /// <summary>One scan cycle: poll providers → merge → statuses.</summary>
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

    /// <summary>Hide a device from the widget (or bring it back).</summary>
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

            // If the device is "asleep" and the battery cannot be read right now — keep the last known value.
            var battery = observation.Battery.IsAvailable
                ? observation.Battery
                : previous?.Battery ?? BatteryReading.Unknown;

            records[observation.DeviceId] = new GearPresenceRecord
            {
                DeviceId = observation.DeviceId,
                Name = observation.Name,
                Source = observation.Source,
                Kind = observation.Kind,
                LastSeenUtc = lastSeen,
                Battery = battery,
                Detail = observation.Detail,
                IsTrusted = observation.IsTrusted,
            };

            devices.Add(ToDevice(
                observation with { Battery = battery },
                lastSeen,
                _policy.Evaluate(observation.IsConnected, observation.HasFault, lastSeen, now)));
        }

        // Devices not present in the current observations: take them from history and paint them yellow/red.
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

        var sorted = CollapseNameTwins(devices)
            .OrderBy(device => (int)device.Status)
            .ThenBy(device => (int)device.Kind)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new GearSnapshot(sorted, errors);
    }

    /// <summary>
    /// Keeps exactly one row per physical device. The same hardware can appear under several ids:
    /// a re-plugged receiver gets a new HID path, and the G435 exists both as a Lightspeed dongle
    /// device and as a Bluetooth device. Twins are grouped by a normalized name
    /// ("G435 Wireless Gaming Headset" and "G435 Bluetooth Gaming Headset" → "g435 headset");
    /// an online row wins over an offline one, otherwise the freshest row wins.
    /// </summary>
    private static List<GearDevice> CollapseNameTwins(List<GearDevice> devices)
    {
        var best = new Dictionary<string, GearDevice>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            var key = NormalizeName(device.Name);
            if (!best.TryGetValue(key, out var current) || IsFresher(device, current))
            {
                best[key] = device;
            }
        }

        var result = new List<GearDevice>();

        foreach (var device in devices)
        {
            if (ReferenceEquals(best[NormalizeName(device.Name)], device))
            {
                result.Add(device);
            }
        }

        return result;
    }

    private static bool IsFresher(GearDevice candidate, GearDevice current)
    {
        var candidateOnline = candidate.Status == GearStatus.Online;
        var currentOnline = current.Status == GearStatus.Online;

        return candidateOnline != currentOnline ? candidateOnline : candidate.LastSeenUtc > current.LastSeenUtc;
    }

    private static readonly string[] NameNoiseWords = ["bluetooth", "wireless", "gaming"];

    private static string NormalizeName(string name)
    {
        var words = name
            .ToLowerInvariant()
            .Split([' ', '\t', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !NameNoiseWords.Contains(word));

        return string.Join(' ', words);
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
