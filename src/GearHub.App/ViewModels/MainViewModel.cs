using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GearHub.App.Settings;
using GearHub.Core.Localization;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.App.ViewModels;

/// <summary>GearHub bar state: device list and periodic refresh.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly GearHubService _service;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _refreshFeedbackTimer;
    private readonly Dictionary<string, DeviceViewModel> _byId = new(StringComparer.Ordinal);

    public MainViewModel(GearHubService service, AppSettings settings, TimeSpan? refreshInterval = null)
    {
        _service = service;
        Settings = settings;
        Settings.PropertyChanged += OnSettingsChanged;
        Devices = [];

        _timer = new DispatcherTimer { Interval = refreshInterval ?? TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _refreshFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _refreshFeedbackTimer.Tick += (_, _) =>
        {
            _refreshFeedbackTimer.Stop();
            RefreshDone = false;
            RefreshStatusText = string.Empty;
        };
    }

    public AppSettings Settings { get; }

    /// <summary>Compact mode: device glyph and percentage instead of cards.</summary>
    public bool IsCompact => Settings.Mode == DisplayMode.Min;

    public ObservableCollection<DeviceViewModel> Devices { get; }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Mode))
        {
            OnPropertyChanged(nameof(IsCompact));
        }
    }

    [ObservableProperty]
    private bool _hasDevices;

    /// <summary>Refresh in progress — the button spins and is disabled.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>Refresh just finished — show the checkmark and the label.</summary>
    [ObservableProperty]
    private bool _refreshDone;

    [ObservableProperty]
    private string _refreshStatusText = string.Empty;

    [ObservableProperty]
    private string _statusLine = Loc.Get("StatusSearching");

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        RefreshDone = false;
        RefreshStatusText = Loc.Get("RefreshBusy");

        try
        {
            var snapshot = await _service.ScanAsync();
            SyncDevices(snapshot);

            var online = snapshot.Devices.Count(device => device.Status == GearStatus.Online);
            StatusLine = Devices.Count == 0
                ? Loc.Get("StatusNoDevices")
                : Loc.Format("StatusCount", Devices.Count, online);

            if (snapshot.ProviderErrors.Count > 0)
            {
                StatusLine += Loc.Get("StatusErrors") + string.Join("; ", snapshot.ProviderErrors);
            }
        }
        catch (Exception ex)
        {
            StatusLine = Loc.Format("StatusScanError", ex.Message);
        }
        finally
        {
            IsRefreshing = false;
            RefreshDone = true;
            RefreshStatusText = Loc.Get("RefreshDone");
            _refreshFeedbackTimer.Stop();
            _refreshFeedbackTimer.Start();
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
