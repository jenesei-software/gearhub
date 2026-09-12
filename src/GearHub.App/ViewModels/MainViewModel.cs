using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.App.ViewModels;

/// <summary>Состояние полосы GearHub: список устройств и периодическое обновление.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly GearHubService _service;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DeviceViewModel> _byId = new(StringComparer.Ordinal);

    public MainViewModel(GearHubService service, TimeSpan? refreshInterval = null)
    {
        _service = service;
        Devices = [];

        _timer = new DispatcherTimer { Interval = refreshInterval ?? TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; }

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    private string _statusLine = "Поиск устройств…";

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _service.ScanAsync();
            SyncDevices(snapshot);

            var online = snapshot.Devices.Count(device => device.Status == GearStatus.Online);
            StatusLine = Devices.Count == 0
                ? "Устройства не найдены"
                : $"{Devices.Count} устр. · онлайн: {online}";

            if (snapshot.ProviderErrors.Count > 0)
            {
                StatusLine += " · ошибки: " + string.Join("; ", snapshot.ProviderErrors);
            }
        }
        catch (Exception ex)
        {
            StatusLine = "Ошибка сканирования: " + ex.Message;
        }
    }

    private void SyncDevices(GearSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < snapshot.Devices.Count; index++)
        {
            var device = snapshot.Devices[index];
            seen.Add(device.DeviceId);

            if (_byId.TryGetValue(device.DeviceId, out var existing))
            {
                existing.Update(device);

                var currentIndex = Devices.IndexOf(existing);
                if (currentIndex != index)
                {
                    Devices.Move(currentIndex, index);
                }
            }
            else
            {
                var created = new DeviceViewModel(device, OnIgnoreRequested);
                _byId[device.DeviceId] = created;
                Devices.Insert(Math.Min(index, Devices.Count), created);
            }
        }

        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(Devices[index].DeviceId))
            {
                _byId.Remove(Devices[index].DeviceId);
                Devices.RemoveAt(index);
            }
        }

        HasDevices = Devices.Count > 0;
    }

    private void OnIgnoreRequested(DeviceViewModel device)
    {
        _service.SetIgnored(device.DeviceId);
        _ = RefreshAsync();
    }
}
