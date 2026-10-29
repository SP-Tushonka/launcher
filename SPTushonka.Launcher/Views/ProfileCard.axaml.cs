using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;
using SPTarkov.Launcher.Controls;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Views;

public partial class ProfileCard : UserControl
{
    public ProfileCard()
    {
        InitializeComponent();
    }

    public event Action<MiniProfile>? Selected;
    public event Action<MiniProfile>? RemoveRequested;

    public MiniProfile? Profile { get; private set; }

    /// <summary>
    ///     Fill the card with a profile
    /// </summary>
    /// <param name="profile">Profile to show</param>
    /// <param name="onProfilePage">Hide the delete action and show the faction artwork</param>
    public void Show(MiniProfile profile, bool onProfilePage)
    {
        Profile = profile;
        var locale = App.Services.GetRequiredService<LocaleHelper>();

        PersonIcon.Foreground = Brush.Parse(ModPageVisual.GetColor(profile.Username));
        Level.Text = $"{locale.Get("profile_card_level")} {profile.CurrentLevel}".ToUpperInvariant();
        NameCaption.Text = $"{locale.Get("profile_card_name")}:";
        Username.Text = profile.Username;
        EditionCaption.Text = $"{locale.Get("profile_card_edition")}:";
        Edition.Text = profile.Edition;
        IdCaption.Text = $"{locale.Get("profile_card_id")}:";
        ProfileId.Text = profile.ProfileId;

        Delete.IsVisible = !onProfilePage;
        Invalid.IsVisible = profile.InvalidOrUnloadableProfile;
        Card.Classes.Set("interactive", !onProfilePage && !profile.InvalidOrUnloadableProfile);
        ShowSide(onProfilePage ? profile.Side.ToLowerInvariant() : "");
    }

    private void ShowSide(string side)
    {
        (double width, double left, double top)? placement = side switch
        {
            "bear" => (370, -60, -80),
            "usec" => (330, -18, -10),
            _ => null,
        };

        if (placement is not { } p)
        {
            SideImage.Source = null;
            return;
        }

        using var stream = AssetLoader.Open(new Uri($"avares://SPT.Launcher/Assets/side_{side}.png"));
        SideImage.Source = new Bitmap(stream);
        SideImage.Width = p.width;
        Canvas.SetLeft(SideImage, p.left);
        Canvas.SetTop(SideImage, p.top);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Profile is null || !Card.Classes.Contains("interactive"))
        {
            return;
        }

        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        Selected?.Invoke(Profile);
    }

    private async void OnCopyId(object? sender, RoutedEventArgs e)
    {
        if (Profile is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var locale = App.Services.GetRequiredService<LocaleHelper>();
        try
        {
            await clipboard.SetTextAsync(Profile.ProfileId);
        }
        catch (Exception)
        {
            ToastHost.Show(locale.Get("profile_copy_id_error"), ToastSeverity.Error);
            return;
        }

        ToastHost.Show(locale.Get("profile_copy_id_success"), ToastSeverity.Success);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Profile is not null)
        {
            RemoveRequested?.Invoke(Profile);
        }
    }
}
