using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GearHub.App.ViewModels;

namespace GearHub.App;

/// <summary>
/// Mini card with battery levels of all devices — opens with a single click on the tray icon.
/// Behaves like a tooltip: stays while the cursor remains near the icon or over the card and hides
/// immediately when the cursor leaves. Appears above the taskbar and above the hidden-icons overflow.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>Gap between the card and the icon / work area edge.</summary>
    private const double Gap = 12;

    /// <summary>Margin to the work area edge.</summary>
    private const double EdgeMargin = 8;

    /// <summary>Offset of the card's right edge relative to the cursor.</summary>
    private const double CursorOffset = 14;

    /// <summary>"Hold" radius around the clicked icon.</summary>
    private const double AnchorRadius = 18;

    /// <summary>How many consecutive checks the cursor must be outside the zone for the card to close.</summary>
    private const int LeaveTicksToClose = 2;

    private readonly DispatcherTimer _cursorWatchTimer;
    private double _anchorX;
    private double _anchorY;
    private int _leaveTicks;

    public TrayFlyoutWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Tooltip behavior: the card disappears as soon as the cursor leaves the icon and the card.
        _cursorWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _cursorWatchTimer.Tick += OnCursorWatchTick;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        SizeChanged += OnSizeChanged;
        Closed += (_, _) => _cursorWatchTimer.Stop();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ScreenHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            _anchorX = cursorX;
            _anchorY = cursorY;
        }

        // Force layout so the card immediately gets the full device list height.
        UpdateLayout();

        PositionNearTrayIcon();
        _cursorWatchTimer.Start();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            PositionNearTrayIcon();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    /// <summary>Places the card above the icon (and the hidden-icons overflow) without overlapping the taskbar.</summary>
    private void PositionNearTrayIcon()
    {
        var cursorX = _anchorX;
        var cursorY = _anchorY;

        var area = ScreenHelper.GetWorkAreaAt((int)cursorX, (int)cursorY);
        var width = ActualWidth;
        var height = ActualHeight;

        var left = cursorX + CursorOffset - width;
        left = Math.Clamp(
            left,
            area.Left + EdgeMargin,
            Math.Max(area.Left + EdgeMargin, area.Right - width - EdgeMargin));

        // By default the card sits right above the icon; if the click came from the hidden-icons
        // overflow, lift it a bit higher (by the row height) so it does not overlap that window's top edge.
        var lift = 12.0;

        if (ScreenHelper.TryGetOverflowFlyoutBounds(out var overflow) && overflow.Contains(cursorX, cursorY))
        {
            lift = 34;
        }

        // Card bottom — above the icon, but always above the taskbar.
        var bottom = Math.Min(cursorY - lift, area.Bottom - 6);

        var top = bottom - height;
        if (top < area.Top + EdgeMargin)
        {
            // Taskbar on top or no room above the icon — show the card below the cursor.
            top = cursorY + Gap + 16;
        }

        top = Math.Clamp(
            top,
            area.Top + EdgeMargin,
            Math.Max(area.Top + EdgeMargin, area.Bottom - height - EdgeMargin));

        Left = left;
        Top = top;
    }

    /// <summary>Closes the card when the cursor leaves the icon, the "bridge" to it, and the card itself.</summary>
    private void OnCursorWatchTick(object? sender, EventArgs e)
    {
        if (!ScreenHelper.TryGetCursorPosition(out var x, out var y) || !IsCursorInKeepZone(x, y))
        {
            if (++_leaveTicks >= LeaveTicksToClose)
            {
                Close();
            }

            return;
        }

        _leaveTicks = 0;
    }

    private bool IsCursorInKeepZone(int x, int y)
    {
        const double cardMargin = 10;

        if (x >= Left - cardMargin && x <= Left + ActualWidth + cardMargin &&
            y >= Top - cardMargin && y <= Top + ActualHeight + cardMargin)
        {
            return true;
        }

        // Square around the clicked icon.
        if (Math.Abs(x - _anchorX) <= AnchorRadius && Math.Abs(y - _anchorY) <= AnchorRadius)
        {
            return true;
        }

        // "Bridge" between the icon and the card so it does not close on the way there.
        var bridgeMin = Math.Min(Top, _anchorY - AnchorRadius);
        var bridgeMax = Math.Max(Top + ActualHeight, _anchorY + AnchorRadius);
        return Math.Abs(x - _anchorX) <= AnchorRadius + 6 && y >= bridgeMin && y <= bridgeMax;
    }
}
