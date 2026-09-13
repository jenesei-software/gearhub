using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearHub.Core.Localization;
using GearHub.Core.Models;

namespace GearHub.App.ViewModels;

/// <summary>A single device card in the bar.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(GearDevice device, Action<DeviceViewModel> ignoreRequested)
    {
        IgnoreCommand = new RelayCommand(() => ignoreRequested(this));
        Update(device);
    }

    public string DeviceId { get; private set; } = string.Empty;

    public IRelayCommand IgnoreCommand { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _batteryText = string.Empty;

    [ObservableProperty]
    private string _kindGlyph = "\uE772";

    [ObservableProperty]
    private string _tooltip = string.Empty;

    [ObservableProperty]
    private GearStatus _status = GearStatus.Online;

    public void Update(GearDevice device)
    {
        DeviceId = device.DeviceId;
        Name = device.Name;
        Status = device.Status;
        BatteryText = FormatBatteryText(device.Battery);
        KindGlyph = KindGlyphFor(device.Kind);
        Tooltip = BuildTooltip(device);
    }

    private static string BuildTooltip(GearDevice device)
    {
        var lines = new List<string>
        {
            device.Name,
            StatusText(device.Status),
            Loc.Format("DeviceSource", device.Source),
            Loc.Format("DeviceCharge", FormatBatteryText(device.Battery)),
        };

        if (!string.IsNullOrWhiteSpace(device.Detail))
        {
            lines.Add(device.Detail);
        }

        lines.Add(Loc.Format("DeviceLastSeen", HumanizeAgo(DateTimeOffset.UtcNow - device.LastSeenUtc)));
        return string.Join(Environment.NewLine, lines);
    }

    private static string StatusText(GearStatus status) => status switch
    {
        GearStatus.Online => Loc.Get("DeviceConnected"),
        GearStatus.Attention => Loc.Get("DeviceAttention"),
        _ => Loc.Get("DeviceLost"),
    };

    private static string FormatBatteryText(BatteryReading battery)
    {
        var percent = battery.Percent ?? CoarsePercent(battery.Coarse);
        var text = percent is { } value ? $"{value}%" : "—";

        return battery is { IsCharging: true, IsAvailable: true } ? $"⚡ {text}" : text;
    }

    /// <summary>Maps the coarse level to percentages: "full" means 100%.</summary>
    private static int? CoarsePercent(CoarseBatteryLevel coarse) => coarse switch
    {
        CoarseBatteryLevel.Full => 100,
        CoarseBatteryLevel.High => 75,
        CoarseBatteryLevel.Medium => 50,
        CoarseBatteryLevel.Low => 25,
        CoarseBatteryLevel.Empty => 5,
        _ => null,
    };

    /// <summary>Device glyph for compact mode (Segoe Fluent Icons).</summary>
    private static string KindGlyphFor(GearKind kind) => kind switch
    {
        GearKind.Keyboard => "\uE765",
        GearKind.Mouse => "\uE962",
        GearKind.Headset => "\uE7F6",
        GearKind.Gamepad => "\uE7FC",
        _ => "\uE772",
    };

    private static string HumanizeAgo(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return Loc.Get("AgoJustNow");
        }

        if (elapsed.TotalHours < 1)
        {
            return Loc.Format("AgoMinutes", (int)elapsed.TotalMinutes);
        }

        if (elapsed.TotalDays < 1)
        {
            return Loc.Format("AgoHours", (int)elapsed.TotalHours);
        }

        return Loc.Format("AgoDays", (int)elapsed.TotalDays);
    }
}
