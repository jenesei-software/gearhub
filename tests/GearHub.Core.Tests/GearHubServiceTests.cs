using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Persistence;
using GearHub.Core.Services;
using Xunit;

namespace GearHub.Core.Tests;

public class GearHubServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TracksDeviceLifecycle_Online_Attention_Lost()
    {
        var time = new FakeTimeProvider(Start);
        var store = new MemoryStore();
        var provider = new FakeProvider();
        var service = new GearHubService([provider], store, new DeviceFilter(), new StatusPolicy(), time);

        // 1. Устройство подключено.
        provider.Observations =
        [
            new GearObservation
            {
                DeviceId = "ble:mouse",
                Name = "MX Master 3S",
                Source = "Bluetooth LE",
                Kind = GearKind.Mouse,
                Battery = new BatteryReading { Percent = 75 },
            },
        ];

        var online = await service.ScanAsync();
        var device = Assert.Single(online.Devices);
        Assert.Equal(GearStatus.Online, device.Status);
        Assert.Equal(75, device.Battery.Percent);
        Assert.Equal(Start, device.LastSeenUtc);

        // 2. Через 2 часа устройство отключилось — жёлтый.
        time.Now = Start + TimeSpan.FromHours(2);
        provider.Observations = [];

        var recentlyOffline = await service.ScanAsync();
        device = Assert.Single(recentlyOffline.Devices);
        Assert.Equal(GearStatus.Attention, device.Status);
        Assert.False(device.IsConnected);
        Assert.Equal(Start, device.LastSeenUtc);

        // 3. Через сутки — красный, заряд из истории сохранился.
        time.Now = Start + TimeSpan.FromHours(25);

        var lost = await service.ScanAsync();
        device = Assert.Single(lost.Devices);
        Assert.Equal(GearStatus.Lost, device.Status);
        Assert.Equal(75, device.Battery.Percent);
    }

    [Fact]
    public async Task HidesIgnoredDevices()
    {
        var time = new FakeTimeProvider(Start);
        var store = new MemoryStore();
        store.SetIgnored("ble:junk", true);

        var provider = new FakeProvider
        {
            Observations =
            [
                new GearObservation
                {
                    DeviceId = "ble:junk",
                    Name = "Unknown gadget",
                    Source = "Bluetooth LE",
                    Battery = new BatteryReading { Percent = 10 },
                },
            ],
        };

        var service = new GearHubService([provider], store, new DeviceFilter(), new StatusPolicy(), time);
        var snapshot = await service.ScanAsync();

        Assert.Empty(snapshot.Devices);
    }

    [Fact]
    public async Task ReportsProviderErrors_WithoutFailingScan()
    {
        var time = new FakeTimeProvider(Start);
        var service = new GearHubService([new ThrowingProvider()], new MemoryStore(), new DeviceFilter(), new StatusPolicy(), time);

        var snapshot = await service.ScanAsync();

        Assert.Empty(snapshot.Devices);
        Assert.Single(snapshot.ProviderErrors);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProvider : IGearProvider
    {
        public string ProviderName => "Fake";

        public IReadOnlyList<GearObservation> Observations { get; set; } = [];

        public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult(Observations);
    }

    private sealed class ThrowingProvider : IGearProvider
    {
        public string ProviderName => "Throwing";

        public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class MemoryStore : IPresenceStore
    {
        private readonly Dictionary<string, GearPresenceRecord> _records = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

        public IReadOnlySet<string> IgnoredIds => new HashSet<string>(_ignored, StringComparer.Ordinal);

        public IReadOnlyCollection<GearPresenceRecord> GetAll() => _records.Values.ToList();

        public bool IsIgnored(string deviceId) => _ignored.Contains(deviceId);

        public void Replace(IEnumerable<GearPresenceRecord> records)
        {
            _records.Clear();
            foreach (var record in records)
            {
                _records[record.DeviceId] = record;
            }
        }

        public void SetIgnored(string deviceId, bool ignored)
        {
            if (ignored)
            {
                _ignored.Add(deviceId);
            }
            else
            {
                _ignored.Remove(deviceId);
            }
        }
    }
}
