using System.IO;
using System.Windows;
using GearHub.App.ViewModels;
using GearHub.Core.Abstractions;
using GearHub.Core.Persistence;
using GearHub.Core.Services;
using GearHub.Providers.Windows.Bluetooth;
using GearHub.Providers.Windows.Logitech;
using GearHub.Providers.Windows.XInput;
using Microsoft.Win32;

namespace GearHub.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private IReadOnlyList<IGearProvider>? _providers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GearHub",
            "state.json");

        var store = new JsonPresenceStore(statePath);
        _providers =
        [
            new XInputGearProvider(),
            new LogitechHidppProvider(),
            new BleGattBatteryProvider(),
        ];

        var service = new GearHubService(_providers, store, new DeviceFilter(), new StatusPolicy());
        _viewModel = new MainViewModel(service);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        _viewModel.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _viewModel?.Stop();

        if (_providers is not null)
        {
            foreach (var provider in _providers.OfType<IDisposable>())
            {
                provider.Dispose();
            }
        }

        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        try
        {
            Dispatcher.Invoke(ApplyTheme);
        }
        catch
        {
            // Приложение уже завершается — не страшно.
        }
    }

    /// <summary>Подключает словарь темы по системной настройке «Приложения в тёмном/светлом режиме».</summary>
    private void ApplyTheme()
    {
        var source = new Uri(IsDarkTheme() ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dictionaries = Resources.MergedDictionaries;

        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var existing = dictionaries[index].Source?.OriginalString ?? string.Empty;
            if (existing.Contains("Themes/", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Add(new ResourceDictionary { Source = source });
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }
}
