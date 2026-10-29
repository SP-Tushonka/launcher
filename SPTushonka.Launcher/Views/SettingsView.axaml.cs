using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Controls;

namespace SPTarkov.Launcher.Views;

public record LocaleOption(string Tag, string Name);

public partial class SettingsView : UserControl
{
    // Scroll distance before the scroll-to-top button appears
    private const double ScrollTopThreshold = 300;

    private static readonly IBrush SaveReady = Brush.Parse("#0BBA83");
    private static readonly IBrush SaveIdle = Brush.Parse("#42FFFFFF");

    private readonly ConfigHelper _config = App.Services.GetRequiredService<ConfigHelper>();
    private readonly LocaleHelper _locale = App.Services.GetRequiredService<LocaleHelper>();
    private readonly TroubleshootingHelper _troubleshooting = App.Services.GetRequiredService<TroubleshootingHelper>();
    private readonly DispatcherTimer _validateDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;

    public event Action? LocaleChanged;
    public event Action? BackgroundChanged;

    public SettingsView()
    {
        InitializeComponent();

        var settings = _config.GetConfig();
        CloseToTray.IsChecked = settings.CloseToTray;
        MinimizeOnLaunch.IsChecked = settings.MinimizeOnLaunch;
        CloseOnGameStart.IsChecked = settings.CloseOnGameStart;
        AlwaysOnTop.IsChecked = settings.AlwaysTop;
        AlwaysOnTop.IsEnabled = OperatingSystem.IsWindows();
        UseBackground.IsChecked = settings.UseBackground;
        AutoConnect.IsChecked = settings.AutoConnectLastProfile;
        ClearCache.IsChecked = settings.ClearCacheOnLaunch;
        EnableGamePath.IsChecked = settings.EnableGamePath;
        DebugLogging.IsChecked = settings.DebugLogging;
        CopyLogs.IsEnabled = OperatingSystem.IsWindows();
        // Grid spacing applies to empty rows too, so the Linux row only exists on Linux
        if (OperatingSystem.IsLinux())
        {
            Panels.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(TroubleshootingPanel, 3);
            LinuxPanel.IsVisible = true;
            LinuxSettingsHost.Content = new LinuxSettingsPanel();
        }

        UpdateCloseToTrayAvailability();

        var locales = _locale.GetAvailableLocales().Select(l => new LocaleOption(l.Key, l.Value)).ToList();
        Language.ItemsSource = locales;
        Language.SelectedItem = locales.FirstOrDefault(l => l.Tag == settings.Language);

        GamePath.Text = _config.GetGamePath();
        GamePath.PropertyChanged += OnGamePathPropertyChanged;
        _validateDebounce.Tick += (_, _) =>
        {
            _validateDebounce.Stop();
            ValidateGamePath();
        };
        Scroller.ScrollChanged += (_, _) => ScrollTop.IsVisible = Scroller.Offset.Y > ScrollTopThreshold;

        ValidateGamePath();
        UpdateGamePathState();
        _loading = false;
    }

    private void UpdateCloseToTrayAvailability()
    {
        CloseToTray.IsEnabled = OperatingSystem.IsWindows() && CloseOnGameStart.IsChecked != true;
    }

    private void OnCloseToTray(object? sender, RoutedEventArgs e)
    {
        _config.SetCloseToTray(CloseToTray.IsChecked == true);
    }

    private void OnMinimizeOnLaunch(object? sender, RoutedEventArgs e)
    {
        _config.SetMinimizeOnLaunch(MinimizeOnLaunch.IsChecked == true);
    }

    // Closing on game start and closing to tray are exclusive
    private void OnCloseOnGameStart(object? sender, RoutedEventArgs e)
    {
        var enabled = CloseOnGameStart.IsChecked == true;
        _config.SetCloseOnGameStart(enabled);
        if (enabled)
        {
            CloseToTray.IsChecked = false;
        }

        UpdateCloseToTrayAvailability();
    }

    private void OnAlwaysOnTop(object? sender, RoutedEventArgs e)
    {
        var enabled = AlwaysOnTop.IsChecked == true;
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Topmost = enabled;
        }

        _config.SetAlwaysOnTop(enabled);
    }

    private void OnUseBackground(object? sender, RoutedEventArgs e)
    {
        _config.SetUseBackground(UseBackground.IsChecked == true);
        BackgroundChanged?.Invoke();
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || Language.SelectedItem is not LocaleOption option)
        {
            return;
        }

        _locale.SetLocale(option.Tag);
        _config.SetLocale(option.Tag);
        LocaleChanged?.Invoke();
    }

    private void OnAutoConnect(object? sender, RoutedEventArgs e)
    {
        _config.SetAutoConnectLastProfile(AutoConnect.IsChecked == true);
    }

    private void OnClearCache(object? sender, RoutedEventArgs e)
    {
        _config.SetClearCacheOnLaunch(ClearCache.IsChecked == true);
    }

    private void OnDebugLogging(object? sender, RoutedEventArgs e)
    {
        _config.SetDebugLogging(DebugLogging.IsChecked == true);
    }

    private void OnEnableGamePath(object? sender, RoutedEventArgs e)
    {
        _config.SetEnableGamePath(EnableGamePath.IsChecked == true);
        GamePath.Text = _config.GetGamePath();
        ValidateGamePath();
        UpdateGamePathState();
    }

    private void OnGamePathPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Controls.OutlinedField.TextProperty || _loading)
        {
            return;
        }

        _validateDebounce.Stop();
        _validateDebounce.Start();
        UpdateGamePathState();
    }

    private void ValidateGamePath()
    {
        GamePath.HasError = !_config.IsGamePathValid(GamePath.Text ?? "");
    }

    // Compared against the saved config so the button never offers to save what is already saved
    private void UpdateGamePathState()
    {
        var enabled = EnableGamePath.IsChecked == true;
        var saved = GamePath.Text == _config.GetConfig().GamePath;
        GamePathRow.IsEnabled = enabled;
        SaveGamePath.IsEnabled = enabled && !saved;
        SaveIcon.Foreground = SaveGamePath.IsEnabled ? SaveReady : SaveIdle;
    }

    private void OnSaveGamePath(object? sender, RoutedEventArgs e)
    {
        ValidateGamePath();
        if (GamePath.HasError)
        {
            ToastHost.Show(_locale.Get("setting_save_path_error_1"), ToastSeverity.Error);
            return;
        }

        _config.SetGamePath(GamePath.Text ?? "");
        UpdateGamePathState();
    }

    private async void OnBrowseGamePath(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync(this, GamePath.Text) is { } path)
        {
            GamePath.Text = path;
            OnSaveGamePath(sender, e);
        }
    }

    private void OnScrollTop(object? sender, RoutedEventArgs e)
    {
        Scroller.ScrollToHome();
    }

    private void OnLoadLiveSettings(object? sender, RoutedEventArgs e)
    {
        Report(_troubleshooting.LoadLiveSettings(), "setting_load_live_success", "setting_load_live_error_1", "setting_load_live_error_2");
    }

    private void OnClearGameSettings(object? sender, RoutedEventArgs e)
    {
        Report(
            _troubleshooting.ClearGameSettings(),
            "setting_clear_game_success",
            "setting_clear_game_error_1",
            "setting_clear_game_error_2"
        );
    }

    private void OnCleanTempFiles(object? sender, RoutedEventArgs e)
    {
        Report(
            _troubleshooting.CleanTempFiles(),
            "setting_clean_temp_success",
            "setting_clean_temp_error_1",
            "setting_clean_temp_error_2",
            blockedSeverity: ToastSeverity.Warning
        );
    }

    private void OnCopyLogs(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var selected = App.Services.GetRequiredService<StateHelper>().SelectedProfile;
        var files = _troubleshooting.CollectLogFiles(selected);
        try
        {
            App.Services.GetRequiredService<WindowsClipboard>().CopyFiles(files.ToArray());
        }
        catch (Exception)
        {
            ToastHost.Show(_locale.Get("setting_copy_logs_error_1"), ToastSeverity.Error);
            return;
        }

        ToastHost.Show(_locale.Get("setting_copy_logs_success"), ToastSeverity.Success);
    }

    /// <summary>
    ///     Show the toast for a troubleshooting result
    /// </summary>
    /// <param name="result">Outcome of the action</param>
    /// <param name="success">Locale key shown on success</param>
    /// <param name="blocked">Locale key shown when the action was blocked or had nothing to work on</param>
    /// <param name="failed">Locale key shown for any other failure</param>
    /// <param name="blockedSeverity">Severity of the blocked toast</param>
    private void Report(
        TroubleshootResult result,
        string success,
        string blocked,
        string failed,
        ToastSeverity blockedSeverity = ToastSeverity.Error
    )
    {
        switch (result)
        {
            case TroubleshootResult.Success:
                ToastHost.Show(_locale.Get(success), ToastSeverity.Success);
                break;
            case TroubleshootResult.Blocked or TroubleshootResult.Unavailable:
                ToastHost.Show(_locale.Get(blocked), blockedSeverity);
                break;
            default:
                ToastHost.Show(_locale.Get(failed), ToastSeverity.Error);
                break;
        }
    }
}
