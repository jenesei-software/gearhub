using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GearHub.App.Settings;
using GearHub.App.ViewModels;
using GearHub.Core.Abstractions;
using GearHub.Core.Localization;
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

        // A tray app must not die silently: log any failures and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };

        Loc.Use(GetWindowsLanguage());
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GearHub",
            "state.json");

        var store = new JsonPresenceStore(statePath);
        var settings = AppSettingsStore.Load();
        settings.PropertyChanged += (_, _) => AppSettingsStore.Save(settings);

        _providers =
        [
            new XInputGearProvider(),
            new LogitechHidppProvider(),
            new BleGattBatteryProvider(),
            new BluetoothHfpBatteryProvider(),
        ];

        var service = new GearHubService(_providers, store, new DeviceFilter(), new StatusPolicy());
        _viewModel = new MainViewModel(service, settings);

        var window = new MainWindow(_viewModel, settings);
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
            // The app is already shutting down — not a problem.
        }
    }

    /// <summary>Writes a failure to %TEMP%\gearhub-crash.log so problems are not lost.</summary>
    private static void LogCrash(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "gearhub-crash.log"),
                $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>Loads the theme dictionary according to the system "app mode" (dark/light) setting.</summary>
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

        // The taskbar may have switched along with the theme — refresh the tray icon.
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.UpdateTrayIcon();
        }
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

    /// <summary>
    /// The taskbar follows the "Windows" system mode (SystemUsesLightTheme), not the app mode,
    /// so the tray icon color is chosen from it. In light mode the taskbar may be filled
    /// with the accent color — in that case we check its luminance.
    /// </summary>
    internal static bool IsDarkTaskbar()
    {
        try
        {
            const string personalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var personalize = Registry.CurrentUser.OpenSubKey(personalizePath);

            if (personalize?.GetValue("SystemUsesLightTheme") is not int lightTheme)
            {
                return IsDarkTheme();
            }

            if (lightTheme == 0)
            {
                return true;
            }

            var accentOnTaskbar = personalize.GetValue("ColorPrevalence") is int prevalence && prevalence == 1;
            if (!accentOnTaskbar)
            {
                return false;
            }

            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is not int accent)
            {
                return false;
            }

            // AccentColor is stored as ABGR (0xAABBGGRR).
            var red = accent & 0xFF;
            var green = (accent >> 8) & 0xFF;
            var blue = (accent >> 16) & 0xFF;
            var luminance = (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255.0;

            return luminance < 0.5;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Windows UI language → supported code (en, ru, de, fr, es, zh);
    /// everything else falls back to English. Overridden by the GEARHUB_LANG environment variable.
    /// </summary>
    private static string GetWindowsLanguage()
    {
        var overridden = Environment.GetEnvironmentVariable("GEARHUB_LANG");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) switch
            {
                0x04 => "zh", // Chinese (Simplified)
                0x07 => "de", // German
                0x09 => "en", // English
                0x0A => "es", // Spanish
                0x0C => "fr", // French
                0x19 => "ru", // Russian
                _ => "en",
            };
        }
        catch
        {
            return CultureInfo.CurrentUICulture.Name;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
