using System.Windows;
using System.Windows.Threading;
using GearHub.App.Settings;

namespace GearHub.App;

public partial class SettingsWindow : Window
{
    /// <summary>Margins match the main widget: the same offset to the edge and to the bar.</summary>
    private const double Gap = 10;
    private const double EdgeMargin = 10;

    private readonly Window _owner;
    private bool _repositionPending;

    public SettingsWindow(AppSettings settings, Window owner)
    {
        InitializeComponent();
        DataContext = settings;
        _owner = owner;
        Owner = owner;

        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) => ScheduleReposition();

        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged += OnOwnerResized;
        owner.IsVisibleChanged += OnOwnerVisibilityChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ScheduleReposition();

    private void OnClosed(object? sender, EventArgs e)
    {
        _owner.LocationChanged -= OnOwnerMoved;
        _owner.SizeChanged -= OnOwnerResized;
        _owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
    }

    private void OnOwnerMoved(object? sender, EventArgs e) => ScheduleReposition();

    private void OnOwnerResized(object? sender, SizeChangedEventArgs e) => ScheduleReposition();

    private void OnOwnerVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => ScheduleReposition();

    /// <summary>Coalesces frequent events (e.g. during dragging) into a single reposition.</summary>
    private void ScheduleReposition()
    {
        if (_repositionPending)
        {
            return;
        }

        _repositionPending = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _repositionPending = false;
                RepositionPopover();
            }),
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Places the window next to the bar like a smart popover: right side first, then left
    /// if it does not fit, otherwise the roomier side; vertically aligned to the bottom.
    /// Always within the work area of the bar's monitor.
    /// </summary>
    private void RepositionPopover()
    {
        if (!IsLoaded || !_owner.IsLoaded || !_owner.IsVisible)
        {
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var area = ScreenHelper.GetWorkArea(_owner);
        var ownerWidth = _owner.ActualWidth;
        var ownerHeight = _owner.ActualHeight;

        // Horizontal: right if it fits; otherwise left; otherwise the roomier side, clamped to the screen.
        var rightX = _owner.Left + ownerWidth + Gap;
        var leftX = _owner.Left - width - Gap;
        double x;

        if (rightX + width <= area.Right - EdgeMargin)
        {
            x = rightX;
        }
        else if (leftX >= area.Left + EdgeMargin)
        {
            x = leftX;
        }
        else
        {
            var spaceRight = area.Right - (_owner.Left + ownerWidth);
            var spaceLeft = _owner.Left - area.Left;
            x = spaceRight >= spaceLeft ? rightX : leftX;
            x = Math.Clamp(x, area.Left + EdgeMargin, Math.Max(area.Left + EdgeMargin, area.Right - width - EdgeMargin));
        }

        // Vertical: aligned to the bottom of the bar, always within the work area.
        var y = _owner.Top + ownerHeight - height;
        y = Math.Clamp(y, area.Top + EdgeMargin, Math.Max(area.Top + EdgeMargin, area.Bottom - height - EdgeMargin));

        Left = x;
        Top = y;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
