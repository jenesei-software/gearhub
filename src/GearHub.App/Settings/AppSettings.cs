using CommunityToolkit.Mvvm.ComponentModel;

namespace GearHub.App.Settings;

/// <summary>Bar display mode.</summary>
public enum DisplayMode
{
    /// <summary>Cards: device name and percentage.</summary>
    Max,

    /// <summary>Compact: device glyph and percentage.</summary>
    Min,
}

/// <summary>Window anchoring to a screen position.</summary>
public enum PanelAnchor
{
    Free,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>Widget settings (persisted to %APPDATA%\GearHub\settings.json).</summary>
public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private DisplayMode _mode = DisplayMode.Max;

    [ObservableProperty]
    private bool _topmost = true;

    [ObservableProperty]
    private PanelAnchor _anchor = PanelAnchor.Free;

    [ObservableProperty]
    private double? _left;

    [ObservableProperty]
    private double? _top;
}
