using System.IO;
using System.Windows;
using GearHub.App.ViewModels;
using GearHub.Core.Abstractions;
using GearHub.Core.Persistence;
using GearHub.Core.Services;
using GearHub.Providers.Windows.Bluetooth;
using GearHub.Providers.Windows.Logitech;
using GearHub.Providers.Windows.XInput;

namespace GearHub.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private IReadOnlyList<IGearProvider>? _providers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
