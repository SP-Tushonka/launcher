using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;
using SPTarkov.Launcher.Controls;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();

        var state = App.Services.GetRequiredService<StateHelper>();

        foreach (var profile in state.ProfileList)
        {
            var card = new ProfileCard();
            card.Show(profile, onProfilePage: false);
            card.Selected += p => ProfileSelected?.Invoke(p);
            card.RemoveRequested += p => RemoveRequested?.Invoke(p);
            Cards.Children.Add(card);
        }

        var empty = state.ProfileList.Count == 0;
        var connected = state.SelectedServer is not null;
        EmptyState.IsVisible = empty && !connected;
        LoginCard.IsVisible = empty && connected;
        if (!LoginCard.IsVisible)
        {
            return;
        }

        Username.Text = App.Services.GetRequiredService<ConfigHelper>().GetLastUsername(state.SelectedServer!.ServerId);
        UpdateLogin();
        Username.PropertyChanged += (_, e) =>
        {
            if (e.Property == OutlinedField.TextProperty)
            {
                UpdateLogin();
            }
        };
        Username.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && LoginButton.IsEnabled)
            {
                OnLogin(s, e);
            }
        };
        Loaded += (_, _) => Username.FocusInput();
    }

    // The avatar takes the colour the profile card will give this username
    private void UpdateLogin()
    {
        LoginButton.IsEnabled = !string.IsNullOrWhiteSpace(Username.Text);
        var color = Color.Parse(ModPageVisual.GetColor(Username.Text ?? ""));
        AvatarGlyph.Foreground = new SolidColorBrush(color);
        Avatar.Background = new SolidColorBrush(color, 0.12);
        Avatar.BorderBrush = new SolidColorBrush(color, 0.4);
    }

    public event Action<MiniProfile>? ProfileSelected;
    public event Action<MiniProfile>? RemoveRequested;
    public event Action? CreateRequested;
    public event Action<string>? LoginRequested;
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
        LoginRequested?.Invoke(Username.Text!.Trim());
    }
}
