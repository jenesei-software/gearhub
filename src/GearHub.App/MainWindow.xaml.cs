using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GearHub.App.Settings;
using GearHub.App.ViewModels;

namespace GearHub.App;

public partial class MainWindow : Window
{
    /// <summary>Margin from the work area edge — the same as for corner anchoring.</summary>
    private const double AnchorMargin = 10;

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _trayFlyoutTimer;
    private SettingsWindow? _settingsWindow;
    private TrayFlyoutWindow? _trayFlyout;
    private DateTime _trayFlyoutClosedAt;
    private DateTime _lastTrayUpAt;
    private bool _exiting;

    private bool _pressed;
    private bool _dragging;
    private Point _dragOriginScreen;
    private double _dragOriginLeft;
    private double _dragOriginTop;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        UpdateTrayIcon();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Closed += OnClosed;

        // Preview events reliably catch dragging even when the press started outside the panel.
        PreviewMouseLeftButtonDown += OnRootMouseDown;
        PreviewMouseMove += OnRootMouseMove;
        PreviewMouseLeftButtonUp += OnRootMouseUp;
        LostMouseCapture += OnRootLostMouseCapture;

        // A short delay before showing the card: a double click (toggling the bar)
        // must not flash the flyout.
        _trayFlyoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _trayFlyoutTimer.Tick += (_, _) =>
        {
            _trayFlyoutTimer.Stop();
            ShowTrayFlyout();
        };
    }

    /// <summary>
    /// Windows-style tray icon: white on a dark taskbar, dark on a light one.
    /// Called at startup and on system theme changes (see App.ApplyTheme).
    /// </summary>
    public void UpdateTrayIcon()
    {
        Tray.IconSource = (ImageSource)FindResource(App.IsDarkTaskbar() ? "TrayIconDark" : "TrayIconLight");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = _settings.Topmost;
        RestorePosition();
    }

    private void OnClosed(object? sender, EventArgs e) => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_settings.Anchor != PanelAnchor.Free)
        {
            SnapToAnchor();
            return;
        }

        ClampIntoWorkArea();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Topmost):
                Topmost = _settings.Topmost;
                break;

            case nameof(AppSettings.Anchor):
                if (_settings.Anchor != PanelAnchor.Free)
                {
                    SnapToAnchor();
                }

                break;
        }
    }

    /// <summary>Restores the window position: saved free position or place anchoring.</summary>
    private void RestorePosition()
    {
        if (_settings.Anchor != PanelAnchor.Free)
        {
            SnapToAnchor();
            return;
        }

        if (_settings.Left is { } left && _settings.Top is { } top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            SnapToBottomCenter();
        }

        ClampIntoWorkArea();
    }

    /// <summary>Keeps the bar inside the screen: preserves the same margin as corner anchoring.</summary>
    private void ClampIntoWorkArea()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var area = ScreenHelper.GetWorkArea(this);
        var left = Math.Clamp(Left, area.Left + AnchorMargin, Math.Max(area.Left + AnchorMargin, area.Right - ActualWidth - AnchorMargin));
        var top = Math.Clamp(Top, area.Top + AnchorMargin, Math.Max(area.Top + AnchorMargin, area.Bottom - ActualHeight - AnchorMargin));

        if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
        {
            Left = left;
            Top = top;
        }
    }

    /// <summary>Snaps the bar to the chosen work area corner (margin — AnchorMargin).</summary>
    private void SnapToAnchor()
    {
        var area = ScreenHelper.GetWorkArea(this);

        (Left, Top) = _settings.Anchor switch
        {
            PanelAnchor.TopLeft => (area.Left + AnchorMargin, area.Top + AnchorMargin),
            PanelAnchor.TopRight => (area.Right - ActualWidth - AnchorMargin, area.Top + AnchorMargin),
            PanelAnchor.BottomLeft => (area.Left + AnchorMargin, area.Bottom - ActualHeight - AnchorMargin),
            PanelAnchor.BottomRight => (area.Right - ActualWidth - AnchorMargin, area.Bottom - ActualHeight - AnchorMargin),
            _ => (Left, Top),
        };
    }

    /// <summary>Default position: bottom center of the work area.</summary>
    private void SnapToBottomCenter()
    {
        var area = ScreenHelper.GetWorkArea(this);
        Left = area.Left + ((area.Width - ActualWidth) / 2.0);
        Top = area.Bottom - ActualHeight - AnchorMargin;
    }

    private void OnRootMouseEnter(object sender, MouseEventArgs e) => SetToolbarExpanded(true);

    private void OnRootMouseLeave(object sender, MouseEventArgs e) => SetToolbarExpanded(false);

    /// <summary>
    /// Shows or hides the button toolbar: the block grows by its height while the "anchor"
    /// edge (bottom or top depending on position) stays in place.
    /// </summary>
    private void SetToolbarExpanded(bool expanded)
    {
        var target = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (Toolbar.Visibility == target)
        {
            return;
        }

        var bottomBefore = Top + ActualHeight;
        var keepBottom = ShouldKeepBottomFixed();

        Toolbar.Visibility = target;

        // The size is recalculated after layout — realign the edge afterwards.
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (keepBottom)
                {
                    Top = bottomBefore - ActualHeight;
                }

                ClampIntoWorkArea();
            }),
            DispatcherPriority.Loaded);
    }

    /// <summary>The block grows upward near the bottom edge of the screen and downward near the top.</summary>
    private bool ShouldKeepBottomFixed()
    {
        if (_settings.Anchor is PanelAnchor.BottomLeft or PanelAnchor.BottomRight)
        {
            return true;
        }

        if (_settings.Anchor is PanelAnchor.TopLeft or PanelAnchor.TopRight)
        {
            return false;
        }

        var area = ScreenHelper.GetWorkArea(this);
        return Top + (ActualHeight / 2) > area.Top + (area.Height / 2);
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _pressed = true;
        _dragging = false;
        _dragOriginScreen = PointToScreen(e.GetPosition(this));
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
    }

    /// <summary>Manual dragging: the window is kept inside the screen during the move.</summary>
    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));

        if (!_dragging)
        {
            if (Math.Abs(current.X - _dragOriginScreen.X) < 4 && Math.Abs(current.Y - _dragOriginScreen.Y) < 4)
            {
                return;
            }

            _dragging = true;
            Root.CaptureMouse();

            // Recalculate the origin from the current position so the window does not jump.
            _dragOriginScreen = current;
            _dragOriginLeft = Left;
            _dragOriginTop = Top;
        }

        var area = ScreenHelper.GetWorkArea(this);
        var left = Math.Clamp(
            _dragOriginLeft + (current.X - _dragOriginScreen.X),
            area.Left + AnchorMargin,
            Math.Max(area.Left + AnchorMargin, area.Right - ActualWidth - AnchorMargin));
        var top = Math.Clamp(
            _dragOriginTop + (current.Y - _dragOriginScreen.Y),
            area.Top + AnchorMargin,
            Math.Max(area.Top + AnchorMargin, area.Bottom - ActualHeight - AnchorMargin));

        Left = left;
        Top = top;
    }

    private void OnRootMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _pressed = false;

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.ReleaseMouseCapture();

        // The position becomes free and is remembered (already clamped to the screen).
        _settings.Anchor = PanelAnchor.Free;
        _settings.Left = Left;
        _settings.Top = Top;
    }

    /// <summary>Mouse capture may be lost (Alt+Tab etc.) — record the current position.</summary>
    private void OnRootLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _pressed = false;
        _settings.Anchor = PanelAnchor.Free;
        _settings.Left = Left;
        _settings.Top = Top;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = _viewModel.RefreshAsync();

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Close();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// A single click on the tray icon shows a mini card with device battery levels (like system flyouts).
    /// A repeated click closes it.
    /// </summary>
    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var sinceLastUp = now - _lastTrayUpAt;
        _lastTrayUpAt = now;

        if (_trayFlyout is { IsVisible: true })
        {
            _trayFlyout.Close();
            return;
        }

        // The second click of a double click: do not show the card, the window double click handles it.
        if (sinceLastUp.TotalMilliseconds < 350)
        {
            _trayFlyoutTimer.Stop();
            return;
        }

        // The click closed the card that just appeared (it lost focus) — do not flash it again.
        if ((now - _trayFlyoutClosedAt).TotalMilliseconds < 350)
        {
            return;
        }

        _trayFlyoutTimer.Stop();
        _trayFlyoutTimer.Start();
    }

    private void ShowTrayFlyout()
    {
        if (_exiting || _trayFlyout is { IsVisible: true })
        {
            return;
        }

        var flyout = new TrayFlyoutWindow(_viewModel);
        flyout.Closed += (_, _) =>
        {
            _trayFlyoutClosedAt = DateTime.UtcNow;
            if (ReferenceEquals(_trayFlyout, flyout))
            {
                _trayFlyout = null;
            }
        };

        _trayFlyout = flyout;
        flyout.Show();
    }

    /// <summary>Double click — show/hide the main bar.</summary>
    private void OnTrayDoubleClick(object sender, RoutedEventArgs e)
    {
        _trayFlyoutTimer.Stop();

        if (_trayFlyout is { IsVisible: true })
        {
            _trayFlyout.Close();
        }

        ToggleWindow();
    }

    private void OnToggleWindowClick(object sender, RoutedEventArgs e) => ToggleWindow();

    private void ToggleWindow()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Activate();

        if (_settings.Anchor != PanelAnchor.Free)
        {
            SnapToAnchor();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The close button and Alt+F4 hide the window to the tray; exit only from the tray menu.
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
