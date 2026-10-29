using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;
using SPTarkov.Launcher.Controls;
using SPTarkov.Launcher.Views;
using SPTarkov.Launcher.Views.Dialogs;

namespace SPTarkov.Launcher;

public partial class MainWindow : Window
{
    // Delay before a login dialog starts so a fast success does not flash it
    private static readonly TimeSpan DialogFlashGuard = TimeSpan.FromMilliseconds(250);

    // Installs run side by side and share the temp folder, so each process writes its own copy
    private static readonly string TrayIconPath = System.IO.Path.Join(
        System.IO.Path.GetTempPath(),
        "SPT.Launcher",
        $"spt-logo-{Environment.ProcessId}.ico"
    );

    private readonly StateHelper _state = App.Services.GetRequiredService<StateHelper>();
    private readonly SessionHelper _session = App.Services.GetRequiredService<SessionHelper>();
    private readonly LocaleHelper _locale = App.Services.GetRequiredService<LocaleHelper>();
    private readonly ConfigHelper _config = App.Services.GetRequiredService<ConfigHelper>();
    private readonly WindowTitle _title = App.Services.GetRequiredService<WindowTitle>();
    private readonly TrayHelper _tray = App.Services.GetRequiredService<TrayHelper>();
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

        Topmost = _config.GetConfig().AlwaysTop;
        // Tray callbacks arrive on the tray thread
        _tray.Configure(
            TrayIconPath,
            onRestore: () => Dispatcher.UIThread.Post(Surface),
            onExit: () => Dispatcher.UIThread.Post(RequestExit)
        );

        Rail.ServersRequested += () => ShowServerPage();
        Rail.SettingsRequested += () => ShowSettings();
        Rail.InfoRequested += () => ShowInfo();
        _state.OnStateChanged += () => Dispatcher.UIThread.Post(RefreshShell);

        Opened += OnOpened;
        Closing += OnClosing;
        ShowHome();
    }

    /// <summary>
    ///     Restore the window from the tray or the taskbar and bring it to the front
    /// </summary>
    public void Surface()
    {
        _tray.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void Minimize()
    {
        WindowState = WindowState.Minimized;
    }

    public void RestoreFromMinimized()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        RestoreSize();

        if (OperatingSystem.IsLinux() && LinuxSettingsInvalid())
        {
            var setup = new LinuxSetupDialog();
            await ShowDialogAsync(setup, setup.Result);
        }

        await TryAutoConnectAsync();
    }

    // The saved size includes the window frame, so the frame is taken off before it becomes the client size
    private void RestoreSize()
    {
        var config = _config.GetConfig();
        if (config.FirstRun)
        {
            return;
        }

        var chrome = FrameSize is { } frame ? frame - ClientSize : default;
        Width = Math.Max(MinWidth, config.StartSize.Width - chrome.Width);
        Height = Math.Max(MinHeight, config.StartSize.Height - chrome.Height);
        Position = Screens.ScreenFromWindow(this) is { } screen
            ? screen.WorkingArea.Center - new PixelPoint((int)(Width * RenderScaling / 2), (int)(Height * RenderScaling / 2))
            : Position;
    }

    private bool LinuxSettingsInvalid()
    {
        var linux = _config.GetConfig().LinuxSettings;
        return !_config.IsPrefixPathValid(linux.PrefixPath)
            || !_config.IsUmuPathValid(linux.UmuPath)
            || !_config.IsProtonVersionValid(linux.ProtonVersion);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // A window closed to the tray already saved its size when it was hidden
        if (!_exitRequested)
        {
            var size = FrameSize ?? ClientSize;
            _config.SetClientLocation(Position.Y, Position.X);
            _config.SetClientSize((int)size.Height, (int)size.Width);
            _config.SetFirstRun(false);
        }

        if (!_exitRequested && OperatingSystem.IsWindows() && _config.GetConfig().CloseToTray)
        {
            e.Cancel = true;
            Hide();
            WriteTrayIcon();
            _tray.Show();
            return;
        }

        _tray.Dispose();
        DeleteTrayIcon();
    }

    // The native tray icon loads from a file
    private void WriteTrayIcon()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrayIconPath)!);
            using var icon = AssetLoader.Open(new Uri("avares://SPT.Launcher/Assets/spt-logo.ico"));
            using var file = new FileStream(TrayIconPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            icon.CopyTo(file);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainWindow>>().LogError("Failed to extract tray icon: {ex}", ex);
        }
    }

    private void DeleteTrayIcon()
    {
        try
        {
            File.Delete(TrayIconPath);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainWindow>>().LogError("Failed to remove tray icon temp file: {ex}", ex);
        }
    }

    private void RefreshShell()
    {
        Rail.Refresh();
        ModPages.Refresh();
    }

    private void ShowHome()
    {
        var home = new HomeView();
        home.AddServerRequested += () => _ = AddServerAsync();
        home.EditRequested += server => _ = EditServerAsync(server);
        home.RemoveRequested += server => _ = RemoveServerAsync(server);
        home.ServerSelected += server => _ = ConnectAsync(server);
        Show(home, LauncherPage.Servers);
    }

    private void ShowProfiles()
    {
        var profiles = new ProfilesView();
        profiles.DisconnectRequested += Disconnect;
        profiles.CreateRequested += () => _ = CreateProfileAsync();
        profiles.LoginRequested += () => _ = LoginWithUsernameAsync();
        profiles.RemoveRequested += profile => _ = RemoveProfileAsync(profile);
        profiles.ProfileSelected += profile => _ = LoginAsync(profile);
        Show(profiles, LauncherPage.Profiles);
    }

    private void ShowProfile()
    {
        var profile = new ProfileView();
        profile.LogoutRequested += () =>
        {
            _state.SetSelectedProfile(null);
            ShowServerPage();
        };
        Show(profile, LauncherPage.Profile);
    }

    private void ShowSettings()
    {
        var settings = new SettingsView();
        settings.LocaleChanged += () => ShowSettings();
        settings.BackgroundChanged += RebuildBackdrop;
        Show(settings, LauncherPage.Settings);
    }

    private void ShowInfo()
    {
        Show(new InfoView(), LauncherPage.Info);
    }

    // The rail's server button returns to wherever the session is
    private void ShowServerPage()
    {
        if (_state.SelectedServer is null)
        {
            ShowHome();
        }
        else if (_state.SelectedProfile is null)
        {
            ShowProfiles();
        }
        else
        {
            ShowProfile();
        }
    }

    // Pages are rebuilt on every visit so they always read the current config, session and locale strings
    private void Show(Control page, LauncherPage kind)
    {
        _state.SetModPagesPanelOpen(false);
        RefreshShell();
        PageHost.Content = page;
        Title = _title.For(kind);
    }

    private async Task TryAutoConnectAsync()
    {
        var config = _config.GetConfig();
        if (_state.AutoConnectAttempted || !config.AutoConnectLastProfile || config.PreferredProfile is null)
        {
            return;
        }

        PageHost.Content = Connecting(_locale.Get("auto_connect_connecting"));
        var result = await _session.TryAutoConnectAsync();

        switch (result)
        {
            case AutoConnectResult.Success:
                ShowProfile();
                return;
            case AutoConnectResult.ServerMissing:
                ToastHost.Show(_locale.Get("auto_connect_server_missing"), ToastSeverity.Warning);
                break;
            case AutoConnectResult.ProfileMissing:
                ToastHost.Show(_locale.Get("auto_connect_profile_missing"), ToastSeverity.Warning);
                break;
            case AutoConnectResult.ConnectFailed or AutoConnectResult.LoginFailed:
                ToastHost.Show(_locale.Get("auto_connect_failed"), ToastSeverity.Error);
                break;
        }

        ShowHome();
    }

    private static Control Connecting(string text)
    {
        var spinner = new Arc
        {
            Classes = { "spinner" },
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = new RotateTransform(),
        };
        return new StackPanel
        {
            Margin = new Thickness(16, 44, 16, 8),
            Spacing = 16,
            Children =
            {
                spinner,
                new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }

    private async Task AddServerAsync()
    {
        var dialog = new ServerFormDialog(null);
        if (await ShowDialogAsync(dialog, dialog.Result) is not { } entry)
        {
            return;
        }

        var servers = _config.GetServers();
        servers.Add(
            new Server
            {
                Name = entry.Name,
                IpAddress = entry.Address,
                ServerId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            }
        );
        _config.SetServers(servers);
        ToastHost.Show(_locale.Get("server_add_success"), ToastSeverity.Success);
        ShowHome();
    }

    private async Task EditServerAsync(Server server)
    {
        if (_state.SelectedServer?.ServerId == server.ServerId)
        {
            _state.LogoutAndDispose();
        }

        var dialog = new ServerFormDialog(server);
        if (await ShowDialogAsync(dialog, dialog.Result) is not { } entry)
        {
            return;
        }

        var servers = _config.GetServers();
        if (servers.FirstOrDefault(s => s.ServerId == server.ServerId) is { } saved)
        {
            saved.Name = entry.Name;
            saved.IpAddress = entry.Address;
        }

        _config.SetServers(servers);
        ToastHost.Show(_locale.Get("server_edit_success"), ToastSeverity.Success);
        ShowHome();
    }

    private async Task RemoveServerAsync(Server server)
    {
        if (_state.SelectedServer is not null)
        {
            _state.LogoutAndDispose();
        }

        var dialog = ServerRemoval(server);
        if (!await ShowDialogAsync(dialog, dialog.Result))
        {
            return;
        }

        _config.SetServers(_config.GetServers().Where(s => s.ServerId != server.ServerId).ToList());
        ToastHost.Show(_locale.Get("server_remove_submit"));
        ShowHome();
    }

    // Connects behind the progress dialog and opens the server's profiles
    private async Task ConnectAsync(Server server)
    {
        if (_state.SelectedServer is not null)
        {
            _state.LogoutAndDispose();
        }

        using var cancel = new CancellationTokenSource();
        var dialog = new ProgressDialog();
        dialog.Cancelled += cancel.Cancel;
        ShowDialog(dialog);

        try
        {
            await Task.Delay(DialogFlashGuard, cancel.Token);
            var progress = new Progress<ConnectStep>(step => dialog.Step = ConnectStepText(step));
            var result = await _session.ConnectToServerAsync(server, progress, cancel.Token);

            if (!result.Success)
            {
                _state.LogoutAndDispose();
                if (!cancel.IsCancellationRequested)
                {
                    ToastHost.Show(ConnectFailureText(result), ToastSeverity.Error);
                }

                return;
            }

            _state.SetSelectedServer(server);
            _state.SetAllowServerPage(true);
            ToastHost.Show(_locale.Get("server_login_task_success"), ToastSeverity.Success);
            ShowProfiles();
        }
        catch (OperationCanceledException)
        {
            _state.LogoutAndDispose();
        }
        finally
        {
            CloseDialog();
        }
    }

    private async Task LoginAsync(MiniProfile profile)
    {
        using var cancel = new CancellationTokenSource();
        var dialog = new ProgressDialog { Step = _locale.Get("profile_login_task_1") };
        dialog.Cancelled += cancel.Cancel;
        ShowDialog(dialog);

        try
        {
            await Task.Delay(DialogFlashGuard, cancel.Token);
            if (!await _session.LoginToProfileAsync(profile, cancel.Token))
            {
                if (!cancel.IsCancellationRequested)
                {
                    ToastHost.Show(_locale.Get("profile_login_task_fail"), ToastSeverity.Error);
                }

                return;
            }

            ToastHost.Show($"{_locale.Get("profile_login_task_success")} {profile.Username}!", ToastSeverity.Success);
            _state.SetSelectedProfile(profile);
            ShowProfile();
        }
        catch (OperationCanceledException) { }
        finally
        {
            CloseDialog();
        }
    }

    private async Task LoginWithUsernameAsync()
    {
        var form = new LoginDialog();
        if (await ShowDialogAsync(form, form.Result) is not { } username)
        {
            return;
        }

        var progress = new ProgressDialog { Step = _locale.Get("profile_login_task_1") };
        ShowDialog(progress);
        await Task.Delay(DialogFlashGuard);
        var profile = await _session.LoginWithDetailsAsync(username);
        CloseDialog();

        if (profile is null)
        {
            ToastHost.Show(_locale.Get("profile_login_task_fail"), ToastSeverity.Error);
            return;
        }

        ToastHost.Show($"{_locale.Get("profile_login_task_success")} {profile.Username}!", ToastSeverity.Success);
        ShowServerPage();
    }

    private async Task CreateProfileAsync()
    {
        var form = new CreateProfileDialog();
        if (await ShowDialogAsync(form, form.Result) is not { } request)
        {
            return;
        }

        var result = await RunWithProgressAsync(
            _locale.Get("profile_add_task_1"),
            () => _session.CreateProfileAsync(request.Edition, request.Username)
        );
        Report(
            result,
            $"{_locale.Get("profile_add_task_success")}: {request.Username}!",
            "profile_add_task_fail",
            "profile_add_task_default"
        );
        ShowProfiles();
    }

    private async Task RemoveProfileAsync(MiniProfile profile)
    {
        var dialog = ProfileRemoval(profile);
        if (!await ShowDialogAsync(dialog, dialog.Result))
        {
            return;
        }

        var result = await RunWithProgressAsync(_locale.Get("profile_remove_task_1"), () => _session.RemoveProfileAsync(profile.Username));
        Report(
            result,
            $"{_locale.Get("profile_remove_task_success")}: {profile.Username}!",
            "profile_remove_task_fail",
            "profile_remove_task_default"
        );
        ShowProfiles();
    }

    private RemoveDialog ServerRemoval(Server server)
    {
        return new RemoveDialog(
            _locale.Get("server_remove_text"),
            _locale.Get("server_remove_confirm"),
            _locale.Get("server_remove_cancel"),
            [(_locale.Get("server_add_name"), server.Name, false), (_locale.Get("server_add_address"), server.IpAddress, true)]
        );
    }

    private RemoveDialog ProfileRemoval(MiniProfile profile)
    {
        return new RemoveDialog(
            _locale.Get("profile_remove_text"),
            _locale.Get("profile_remove_confirm"),
            _locale.Get("profile_remove_cancel"),
            [
                (_locale.Get("profile_card_name"), profile.Username, false),
                (_locale.Get("profile_card_level"), profile.CurrentLevel.ToString(), false),
                (_locale.Get("profile_card_id"), profile.ProfileId, true),
            ]
        );
    }

    private async Task<ProfileRequestResult> RunWithProgressAsync(string step, Func<Task<ProfileRequestResult>> request)
    {
        ShowDialog(new ProgressDialog { Step = step });
        try
        {
            return await request();
        }
        finally
        {
            CloseDialog();
        }
    }

    private void Report(ProfileRequestResult result, string success, string rejectedKey, string failedKey)
    {
        switch (result)
        {
            case ProfileRequestResult.Success:
                ToastHost.Show(success, ToastSeverity.Success);
                break;
            case ProfileRequestResult.Rejected:
                ToastHost.Show(_locale.Get(rejectedKey), ToastSeverity.Error);
                break;
            default:
                ToastHost.Show(_locale.Get(failedKey), ToastSeverity.Error);
                break;
        }
    }

    private void Disconnect()
    {
        _state.SetSelectedServer(null);
        _state.SetAllowServerPage(false);
        ShowHome();
    }

    private string ConnectStepText(ConnectStep step)
    {
        return step switch
        {
            ConnectStep.Ping => _locale.Get("server_login_task_1"),
            ConnectStep.Profiles => _locale.Get("server_login_task_2"),
            ConnectStep.Types => _locale.Get("server_login_task_3"),
            ConnectStep.ModPages => _locale.Get("server_login_task_4"),
            _ => "",
        };
    }

    private string ConnectFailureText(ConnectResult result)
    {
        if (result.TimedOut)
        {
            return _locale.Get("request_timed_out");
        }

        return result.FailedStep switch
        {
            ConnectStep.Profiles => _locale.Get("server_login_task_fail_1"),
            ConnectStep.Types => _locale.Get("server_login_task_fail_2"),
            ConnectStep.ModPages => _locale.Get("server_login_task_fail_3"),
            _ => _locale.Get("server_login_default"),
        };
    }

    /// <summary>
    ///     Show a dialog over the blurred page until it produces a result
    /// </summary>
    /// <param name="dialog">Dialog to show</param>
    /// <param name="result">Task that completes with the dialog's answer</param>
    /// <returns>The dialog's answer</returns>
    public async Task<T> ShowDialogAsync<T>(Control dialog, Task<T> result)
    {
        ShowDialog(dialog);
        try
        {
            return await result;
        }
        finally
        {
            CloseDialog();
        }
    }

    private async Task ShowDialogAsync(Control dialog, Task result)
    {
        ShowDialog(dialog);
        try
        {
            await result;
        }
        finally
        {
            CloseDialog();
        }
    }

    private void ShowDialog(Control dialog)
    {
        RefreshDialogBackdrop();
        DialogHost.Content = dialog;
        DialogLayer.IsVisible = true;
    }

    private void CloseDialog()
    {
        DialogLayer.IsVisible = false;
        DialogHost.Content = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // ClientSize is first set by the base constructor before the named controls exist
        if (change.Property == ClientSizeProperty && PageBackground is not null)
        {
            RebuildBackdrop();
        }
    }

    private void RebuildBackdrop()
    {
        Backdrop.RebuildPage(ClientSize, RenderScaling, _config.GetConfig().UseBackground);
        PageBackground.Source = Backdrop.Page;

        if (DialogLayer.IsVisible)
        {
            RefreshDialogBackdrop();
        }
    }

    // The dialog blur samples a still of the page, which cannot change while a modal is open
    private void RefreshDialogBackdrop()
    {
        var scaling = RenderScaling;
        var size = PixelSize.FromSize(PageLayer.Bounds.Size, scaling);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        using var still = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        still.Render(PageLayer);
        Backdrop.RebuildDialog(still, scaling);
        DialogBackdrop.Source = Backdrop.DialogPage;
    }
}
