using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Bundles;
using SPTarkov.Launcher.Controls;
using SPTarkov.Launcher.Helpers;
using SPTarkov.Launcher.Views.Dialogs;

namespace SPTarkov.Launcher.Views;

public partial class ProfileView : UserControl
{
    private static readonly TimeSpan GameStartTimeout = TimeSpan.FromMinutes(2);
    private static readonly IBrush StartIconColor = Brush.Parse("#0BBA83");

    private static readonly (string Key, string Label)[][] TopColumns =
    [
        [("Raids", "profile_stats_raids"), ("RunThrough", "profile_stats_run_through")],
        [("Survived", "profile_stats_survived"), ("AWOL", "profile_stats_awol")],
        [("KIA", "profile_stats_kia"), ("MIA", "profile_stats_mia")],
        [("Kills", "profile_stats_kills"), ("KD", "profile_stats_kd")],
    ];

    private static readonly (string Key, string Label)[][] BottomColumns =
    [
        [("SurvivalRate", "profile_stats_survival_rate"), ("LeaveRate", "profile_stats_leave_rate")],
        [("AverageLifeSpan", "profile_stats_average_life_span"), ("SurvivalsInARow", "profile_stats_survivals_row")],
        [("Online", "profile_stats_online"), ("AccountLifetime", "profile_stats_account_lifetime")],
    ];

    private readonly LocaleHelper _locale = App.Services.GetRequiredService<LocaleHelper>();
    private readonly StateHelper _state = App.Services.GetRequiredService<StateHelper>();
    private readonly ConfigHelper _config = App.Services.GetRequiredService<ConfigHelper>();
    private readonly GameHelper _game = App.Services.GetRequiredService<GameHelper>();
    private readonly SessionHelper _session = App.Services.GetRequiredService<SessionHelper>();
    private MiniProfile _profile;

    public ProfileView()
    {
        InitializeComponent();

        _profile = _state.SelectedProfile ?? new MiniProfile();
        ExpBar.SizeChanged += (_, e) => UpdateExpBar(e.NewSize.Width);
        ShowProfile();
    }

    public event Action? LogoutRequested;

    private MainWindow? Window => TopLevel.GetTopLevel(this) as MainWindow;

    private bool WipeOnStart => WipeToggle.IsChecked == true;

    private void ShowProfile()
    {
        WipeBox.IsVisible = !_profile.Wipe;
        NameIcon.Foreground = Brush.Parse(ModPageVisual.GetColor(_profile.Username));
        Nickname.Text = _profile.Nickname == "unknown" ? _locale.Get("profile_no_character") : _profile.Nickname;
        Card.Show(_profile, onProfilePage: true);

        var currency = _profile.ProfileCurrency;
        Currencies.Children.Clear();
        foreach (var text in new[] { $"₽: {currency.Roubles}", $"$: {currency.Dollars}", $"€: {currency.Euros}", $"GP: {currency.GP}" })
        {
            Currencies.Children.Add(new TextBlock { Classes = { "h6" }, Text = text });
        }

        CurrentLevel.Text = $"{_locale.Get("profile_level")}: {_profile.CurrentLevel}";
        NeededExp.Text = $"{_locale.Get("profile_needed_exp")}: {_profile.NextLevel}";
        NextLevel.Text = $"{_locale.Get("profile_next_level")}: {_profile.CurrentLevel + 1}";
        UpdateExpBar(ExpBar.Bounds.Width);

        var stats = _profile.ProfileStats;
        ShowStats(
            PmcTab.IsChecked == true ? stats.Pmc
            : ScavTab.IsChecked == true ? stats.Scav
            : stats.Overall
        );
    }

    // The bar runs from zero to the next level's total experience
    private void UpdateExpBar(double width)
    {
        var percent = _profile.NextLevel > 0 ? Math.Clamp((double)_profile.CurrentExp / _profile.NextLevel, 0, 1) * 100 : 0;
        ExpFill.Width = width * percent / 100;
    }

    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } tab || TopStats is null)
        {
            return;
        }

        var stats = _profile.ProfileStats;
        ShowStats(
            tab == PmcTab ? stats.Pmc
            : tab == ScavTab ? stats.Scav
            : stats.Overall
        );
    }

    private void ShowStats(IReadOnlyDictionary<string, string> stats)
    {
        Fill(TopStats, TopColumns, stats);
        Fill(BottomStats, BottomColumns, stats);
    }

    // Columns sit in every other grid column so the star columns between them spread them evenly
    private void Fill(Grid grid, (string Key, string Label)[][] columns, IReadOnlyDictionary<string, string> stats)
    {
        grid.Children.Clear();
        for (var i = 0; i < columns.Length; i++)
        {
            var column = new StackPanel { Classes = { "stat" } };
            foreach (var (key, label) in columns[i])
            {
                column.Children.Add(new TextBlock { Classes = { "caption" }, Text = $"{_locale.Get(label)}:" });
                column.Children.Add(
                    new TextBlock
                    {
                        Classes = { "h6" },
                        Text = stats.TryGetValue(key, out var value) ? value : "—",
                        Margin = new Thickness(0, -8, 0, 0),
                    }
                );
            }

            Grid.SetColumn(column, i * 2);
            grid.Children.Add(column);
        }
    }

    private async void OnWipeToggled(object? sender, RoutedEventArgs e)
    {
        if (WipeOnStart && Window is { } window)
        {
            var dialog = new WipeDialog(_profile.Username);
            WipeToggle.IsChecked = await window.ShowDialogAsync(dialog, dialog.Result);
        }

        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        WipeGlyph.Data = (Geometry)this.FindResource(WipeOnStart ? "IconCheckBox" : "IconCheckBoxBlank")!;
        StartButton.Classes.Set("danger", WipeOnStart);
        StartText.Text = _locale.Get(WipeOnStart ? "profile_wipe_and_start" : "profile_start_game").ToUpperInvariant();
        StartIcon.Foreground = WipeOnStart ? Brushes.Black : StartIconColor;
    }

    private void SetBusy(bool busy)
    {
        LogoutButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy;
        WipeToggle.IsEnabled = !busy;
        _state.SetAllowNavigation(!busy);
    }

    private async void OnStartGame(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        if (await StartGameAsync())
        {
            return;
        }

        await RefreshProfileAsync();
        WipeToggle.IsChecked = false;
        UpdateStartButton();
        SetBusy(false);

        if (_config.GetConfig().MinimizeOnLaunch)
        {
            Window?.RestoreFromMinimized();
        }
    }

    /// <summary>
    ///     Check, patch and launch the game, then wait for it to close
    /// </summary>
    /// <returns>True when the launcher is closing because the game started</returns>
    private async Task<bool> StartGameAsync()
    {
        ToastHost.Show(_locale.Get("profile_page_task_1"));
        if (!await _game.CheckGame())
        {
            return Fail("profile_page_task_1_fail");
        }

        if (WipeOnStart)
        {
            ToastHost.Show(_locale.Get("profile_wipe"));
            if (await _session.WipeProfileAsync(_profile) != ProfileRequestResult.Success)
            {
                return Fail("profile_unable_wipe");
            }
        }

        if (_config.GetConfig().ClearCacheOnLaunch)
        {
            ToastHost.Show(_locale.Get("profile_clear"));
            App.Services.GetRequiredService<TroubleshootingHelper>().CleanTempFiles();
        }

        ToastHost.Show(_locale.Get("profile_page_task_2"));
        if (!await _game.PatchGame())
        {
            return Fail("profile_page_task_2_fail");
        }

        ToastHost.Show(_locale.Get("profile_page_task_4"));
        ShowBundleProgress(new BundleProgress());
        var bundlesAcquired = await _game.AcquireBundles(new Progress<BundleProgress>(ShowBundleProgress), CancellationToken.None);
        BundleCard.IsVisible = false;
        if (!bundlesAcquired)
        {
            return Fail("profile_page_task_4_fail");
        }

        ToastHost.Show(_locale.Get("profile_page_task_3"));
        if (!_game.LaunchGame())
        {
            return Fail("profile_page_task_3_fail");
        }

        var config = _config.GetConfig();
        if (config.AutoConnectLastProfile && _state.SelectedServer is { } server && _state.SelectedProfile is { } profile)
        {
            _config.SetPreferredProfile(server.ServerId, profile.ProfileId);
        }

        if (config.CloseOnGameStart)
        {
            if (await _game.WaitForGameRunning(GameStartTimeout, CancellationToken.None))
            {
                Window?.RequestExit();
                return true;
            }

            ToastHost.Show(_locale.Get("profile_page_close_on_start_fail"), ToastSeverity.Warning);
        }

        if (config.MinimizeOnLaunch)
        {
            Window?.Minimize();
        }

        if (!await _game.MonitorGame())
        {
            ToastHost.Show(_locale.Get("profile_page_game_closed"), ToastSeverity.Info);
        }

        return false;
    }

    private bool Fail(string messageKey)
    {
        BundleCard.IsVisible = false;
        ToastHost.Show($"{_locale.Get(messageKey)}: {_game.ErrorMessage}", ToastSeverity.Error);
        return false;
    }

    private void ShowBundleProgress(BundleProgress progress)
    {
        BundleCard.IsVisible = true;
        BundleCount.Text = $"{_locale.Get("profile_page_task_4")}: {progress.Current} / {progress.Total}";
        BundleBar.Value = progress.Percentage;
        BundleDownload.IsVisible = progress.IsDownloading;
        BundleName.Text = progress.BundleName;
        BundleSpeed.Text = $"{progress.DownloadSpeed} | {progress.FileSizeInfo}";
    }

    private async Task RefreshProfileAsync()
    {
        ToastHost.Show(_locale.Get("profile_update_request"));
        if (await _session.RefreshSelectedProfileAsync() && _state.SelectedProfile is { } refreshed)
        {
            _profile = refreshed;
            ShowProfile();
        }
    }

    private void OnLogout(object? sender, RoutedEventArgs e)
    {
        LogoutRequested?.Invoke();
    }
}
