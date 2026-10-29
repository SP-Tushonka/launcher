using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Controls;

namespace SPTarkov.Launcher.Views;

public partial class NavRail : UserControl
{
    private readonly StateHelper _state = App.Services.GetRequiredService<StateHelper>();

    public NavRail()
    {
        InitializeComponent();
    }

    public event Action? ServersRequested;
    public event Action? SettingsRequested;
    public event Action? InfoRequested;

    private string ServerAddress => _state.SelectedServer?.IpAddress ?? "";

    /// <summary>
    ///     Show the server tiles while a session is open and lock the rail while the game starts
    /// </summary>
    public void Refresh()
    {
        ServerTiles.IsVisible = _state.AllowServerPage;
        ModPagesTile.IsVisible = _state.ModPages.Count > 0;
        SicUrl.Text = $"https://{ServerAddress}/";

        ServersButton.IsEnabled = _state.AllowNavigation;
        SettingsButton.IsEnabled = _state.AllowNavigation;
        InfoButton.IsEnabled = _state.AllowNavigation;
    }

    private void OnServers(object? sender, RoutedEventArgs e)
    {
        ServersRequested?.Invoke();
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnInfo(object? sender, RoutedEventArgs e)
    {
        InfoRequested?.Invoke();
    }

    private void OnSic(object? sender, RoutedEventArgs e)
    {
        if (_state.AllowServerPage)
        {
            ExternalLinks.Open($"https://{ServerAddress}/");
        }
    }

    private void OnEnterModPages(object? sender, PointerEventArgs e)
    {
        _state.HoldModPagesPanel();
    }

    private void OnExitModPages(object? sender, PointerEventArgs e)
    {
        _ = _state.ReleaseModPagesPanel();
    }

    private void OnLeaveModPages(object? sender, PointerEventArgs e)
    {
        _state.SetModPagesPanelOpen(false);
    }
}
