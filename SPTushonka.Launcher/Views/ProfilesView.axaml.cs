using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;

namespace SPTarkov.Launcher.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();

        var state = App.Services.GetRequiredService<StateHelper>();
        var locale = App.Services.GetRequiredService<LocaleHelper>();

        foreach (var profile in state.ProfileList)
        {
            var card = new ProfileCard();
            card.Show(profile, onProfilePage: false);
            card.Selected += p => ProfileSelected?.Invoke(p);
            card.RemoveRequested += p => RemoveRequested?.Invoke(p);
            Cards.Children.Add(card);
        }

        var empty = state.ProfileList.Count == 0;
        LoginButton.IsVisible = empty;
        EmptyState.IsVisible = empty;
        if (!empty)
        {
            return;
        }

        var connected = state.SelectedServer is not null;
        EmptyIcon.Data = (Geometry)Application.Current!.FindResource(connected ? "IconPersonSearch" : "IconLinkOff")!;
        EmptyTitle.Text = locale.Get(connected ? "profiles_empty_title" : "profiles_connect_warning");
        EmptySubtitle.Text = connected ? locale.Get("profiles_empty_subtitle") : null;
        EmptySubtitle.IsVisible = connected;
    }

    public event Action<MiniProfile>? ProfileSelected;
    public event Action<MiniProfile>? RemoveRequested;
    public event Action? CreateRequested;
    public event Action? LoginRequested;
    public event Action? DisconnectRequested;

    private void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        DisconnectRequested?.Invoke();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        CreateRequested?.Invoke();
    }

    private void OnLogin(object? sender, RoutedEventArgs e)
    {
        LoginRequested?.Invoke();
    }
}
