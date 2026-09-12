using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GearHub.Core.Models;

namespace GearHub.App.ViewModels;

/// <summary>Одна карточка устройства в полосе.</summary>
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
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _batteryText = string.Empty;

    [ObservableProperty]
    private string _batteryGlyph = string.Empty;

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
        BatteryGlyph = BatteryGlyphFor(device.Battery);
        Subtitle = BuildSubtitle(device);
        Tooltip = BuildTooltip(device);
    }

    private static string BuildSubtitle(GearDevice device)
    {
        if (!device.IsConnected)
        {
            return $"отключено {HumanizeAgo(DateTimeOffset.UtcNow - device.LastSeenUtc)}";
        }

        return string.IsNullOrWhiteSpace(device.Detail)
            ? device.Source
            : $"{device.Source} · {device.Detail}";
    }

    private static string BuildTooltip(GearDevice device)
    {
        var lines = new List<string>
        {
            device.Name,
            StatusText(device.Status),
            $"Источник: {device.Source}",
            $"Заряд: {FormatBatteryText(device.Battery)}",
        };

        if (!string.IsNullOrWhiteSpace(device.Detail))
        {
            lines.Add(device.Detail);
        }

        lines.Add($"Последний контакт: {HumanizeAgo(DateTimeOffset.UtcNow - device.LastSeenUtc)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string StatusText(GearStatus status) => status switch
    {
        GearStatus.Online => "Подключено",
        GearStatus.Attention => "Недавно отключено или проблема",
        _ => "Давно отключено",
    };

    private static string FormatBatteryText(BatteryReading battery)
    {
        if (battery.Percent is { } percent)
        {
            return $"{percent}%";
        }

        return battery.Coarse switch
        {
            CoarseBatteryLevel.Empty => "Разряжен",
            CoarseBatteryLevel.Low => "Низкий",
            CoarseBatteryLevel.Medium => "Средний",
            CoarseBatteryLevel.High => "Высокий",
            CoarseBatteryLevel.Full => "Полный",
            _ => "—",
        };
    }

    /// <summary>Глиф батарейки из Segoe MDL2: Battery0..Battery9 = E850..E859, Battery10 = E83F.</summary>
    private static string BatteryGlyphFor(BatteryReading battery)
    {
        var index = battery.Percent is { } percent
            ? (int)Math.Round(percent / 10.0)
            : battery.Coarse switch
            {
                CoarseBatteryLevel.Empty => 0,
                CoarseBatteryLevel.Low => 1,
                CoarseBatteryLevel.Medium => 5,
                CoarseBatteryLevel.High => 8,
                CoarseBatteryLevel.Full => 10,
                _ => -1,
            };

        if (index < 0)
        {
            return "\uE850";
        }

        index = Math.Clamp(index, 0, 10);
        return index == 10 ? "\uE83F" : ((char)('\uE850' + index)).ToString();
    }

    private static string HumanizeAgo(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return "только что";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes} мин назад";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{(int)elapsed.TotalHours} ч назад";
        }

        return $"{(int)elapsed.TotalDays} дн назад";
    }
}
