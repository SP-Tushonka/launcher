using System.Diagnostics;
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

public partial class LinuxSettingsPanel : UserControl
{
    private static readonly IBrush SaveReady = Brush.Parse("#0BBA83");
    private static readonly IBrush SaveIdle = Brush.Parse("#42FFFFFF");

    private readonly ConfigHelper _config = App.Services.GetRequiredService<ConfigHelper>();
    private readonly LinuxHelper _linux = App.Services.GetRequiredService<LinuxHelper>();
    private readonly LocaleHelper _locale = App.Services.GetRequiredService<LocaleHelper>();
    private bool _loading = true;

    public LinuxSettingsPanel()
    {
        InitializeComponent();

        var settings = _config.GetConfig().LinuxSettings;

        // Empty paths are filled with whatever can be detected and saved straight away
        if (string.IsNullOrEmpty(settings.PrefixPath))
        {
            _config.SetLinuxPrefixPath(_config.TryDetectPrefixFromGamePath());
        }

        if (string.IsNullOrWhiteSpace(settings.UmuPath))
        {
            _config.SetLinuxUmuPath(_config.TryDetectUmuPath());
        }

        Prefix.Text = settings.PrefixPath;
        Umu.Text = settings.UmuPath;
        Launch.Text = settings.LaunchSettings;
        Watch(Prefix, () => Prefix.HasError = !_config.IsPrefixPathValid(Prefix.Text ?? ""));
        Watch(Umu, () => Umu.HasError = !_config.IsUmuPathValid(Umu.Text ?? ""));
        Watch(Launch, null);
        Proton.PropertyChanged += OnProtonChanged;

        Prefix.HasError = !_config.IsPrefixPathValid(Prefix.Text ?? "");
        Umu.HasError = !_config.IsUmuPathValid(Umu.Text ?? "");
        UpdateSaveButtons();
        _ = LoadProtonVersionsAsync();
        CheckGameMode();
        _loading = false;
    }

    /// <summary>
    ///     Show the GameMode switch, which the startup dialog leaves out
    /// </summary>
    public bool ShowGameMode
    {
        get => GameMode.IsVisible;
        set => GameMode.IsVisible = value;
    }

    /// <summary>
    ///     Whether any saved Linux setting is invalid
    /// </summary>
    public bool HasErrors =>
        !_config.IsPrefixPathValid(_config.GetConfig().LinuxSettings.PrefixPath)
        || !_config.IsUmuPathValid(_config.GetConfig().LinuxSettings.UmuPath)
        || !_config.IsProtonVersionValid(_config.GetConfig().LinuxSettings.ProtonVersion);

    // Path checks touch the disk, so typing is validated half a second after it stops
    private void Watch(OutlinedField field, Action? validate)
    {
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            validate?.Invoke();
        };

        field.PropertyChanged += (_, e) =>
        {
            if (e.Property != OutlinedField.TextProperty || _loading)
            {
                return;
            }

            debounce.Stop();
            debounce.Start();
            UpdateSaveButtons();
        };
    }

    private void UpdateSaveButtons()
    {
        var settings = _config.GetConfig().LinuxSettings;
        SetSave(SavePrefix, Prefix.Text != settings.PrefixPath);
        SetSave(SaveUmu, Umu.Text != settings.UmuPath);
        SetSave(SaveLaunch, Launch.Text != settings.LaunchSettings);
    }

    private static void SetSave(Button button, bool unsaved)
    {
        button.IsEnabled = unsaved;
        ((MaterialIcon)button.Content!).Foreground = unsaved ? SaveReady : SaveIdle;
    }

    private async Task LoadProtonVersionsAsync(string? select = null)
    {
        var versions = await _linux.GetProtonVersions();
        _loading = true;
        Proton.ItemsSource = versions;
        var current = select ?? _config.GetConfig().LinuxSettings.ProtonVersion;
        Proton.SelectedItem = versions.Contains(current) ? current : null;
        Proton.HasError = !_config.IsProtonVersionValid(current);
        _loading = false;
    }

    private void OnProtonChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != OutlinedSelect.SelectedItemProperty || _loading || Proton.SelectedItem is not string version)
        {
            return;
        }

        SavePath(Proton, version, _config.IsProtonVersionValid, _config.SetLinuxProtonVersion);
    }

    private void SavePath(Control field, string value, Func<string, bool> isValid, Action<string> save)
    {
        var valid = isValid(value);
        switch (field)
        {
            case OutlinedField text:
                text.HasError = !valid;
                break;
            case OutlinedSelect select:
                select.HasError = !valid;
                break;
        }

        if (!valid)
        {
            ToastHost.Show(_locale.Get("setting_save_path_error_1"), ToastSeverity.Error);
            return;
        }

        save(value);
        UpdateSaveButtons();
    }

    private void OnSavePrefix(object? sender, RoutedEventArgs e)
    {
        SavePath(Prefix, Prefix.Text ?? "", _config.IsPrefixPathValid, _config.SetLinuxPrefixPath);
    }

    private void OnSaveUmu(object? sender, RoutedEventArgs e)
    {
        SavePath(Umu, Umu.Text ?? "", _config.IsUmuPathValid, _config.SetLinuxUmuPath);
    }

    private void OnSaveLaunch(object? sender, RoutedEventArgs e)
    {
        _config.SetLinuxLaunchSettings(Launch.Text ?? "");
        UpdateSaveButtons();
    }

    private async void OnBrowsePrefix(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync(this, Prefix.Text) is { } path)
        {
            Prefix.Text = path;
            OnSavePrefix(sender, e);
        }
    }

    private async void OnBrowseUmu(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFileAsync(this, Umu.Text) is { } path)
        {
            Umu.Text = path;
            OnSaveUmu(sender, e);
        }
    }

    private async void OnAddProtonPath(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync(this, Proton.SelectedItem as string) is not { } path)
        {
            return;
        }

        if (!_config.AddLinuxProtonSearchPath(path))
        {
            ToastHost.Show(_locale.Get("setting_save_path_error_2"), ToastSeverity.Warning);
            return;
        }

        ToastHost.Show(_locale.Get("setting_save_path_success"), ToastSeverity.Success);
        await LoadProtonVersionsAsync();
    }

    // GameMode can only be turned on where the gamemoded daemon is installed
    private void CheckGameMode()
    {
        var installed = false;
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("gamemoded", "-version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            process?.WaitForExit();
            installed = process?.ExitCode == 0;
        }
        catch (Exception) { }

        if (!installed)
        {
            _config.SetLinuxGameMode(false);
        }

        GameMode.IsEnabled = installed;
        GameMode.IsChecked = installed && _config.GetConfig().LinuxSettings.GameMode;
    }

    private void OnGameMode(object? sender, RoutedEventArgs e)
    {
        _config.SetLinuxGameMode(GameMode.IsChecked == true);
    }
}
