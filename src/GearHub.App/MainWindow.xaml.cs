using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GearHub.App.ViewModels;

namespace GearHub.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _userMoved;
    private bool _exiting;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SnapToBottomCenter();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && !_userMoved)
        {
            SnapToBottomCenter();
        }
    }

    /// <summary>Прижимает полосу к низу рабочей области основного монитора по центру.</summary>
    private void SnapToBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2.0);
        Top = area.Bottom - ActualHeight - 10;
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
            _userMoved = true;
        }
        catch
        {
            // DragMove может бросить, если кнопка уже отпущена — не критично.
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = _viewModel.RefreshAsync();

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => ToggleWindow();

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

        if (!_userMoved)
        {
            SnapToBottomCenter();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Крестик и Alt+F4 прячут окно в трей; выход — только через меню трея.
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
