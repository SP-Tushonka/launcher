using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class CreateProfileDialog : UserControl
{
    private readonly TaskCompletionSource<(string Edition, string Username)?> _result = new();
    private readonly Dictionary<string, string> _editions = App.Services.GetRequiredService<StateHelper>().ProfileTypes;

    public CreateProfileDialog()
    {
        InitializeComponent();

        var locale = App.Services.GetRequiredService<LocaleHelper>();
        Edition.Label = $"{locale.Get("profile_add_edition")}*";
        Edition.ItemsSource = _editions.Keys.ToList();
        Username.Label = $"{locale.Get("profile_add_name")}*";

        Edition.PropertyChanged += OnInputChanged;
        Username.PropertyChanged += OnInputChanged;
        KeyDown += OnKeyDown;
        Update();
    }

    public Task<(string Edition, string Username)?> Result => _result.Task;

    private string? SelectedEdition => Edition.SelectedItem as string;

    private bool CanSubmit => !string.IsNullOrWhiteSpace(SelectedEdition) && !string.IsNullOrWhiteSpace(Username.Text);

    private void OnInputChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Controls.OutlinedField.TextProperty || e.Property == Controls.OutlinedSelect.SelectedItemProperty)
        {
            Update();
        }
    }

    // The header tint matches the profile card, which hashes the username
    private void Update()
    {
        ConfirmButton.IsEnabled = CanSubmit;

        var description = SelectedEdition is { } edition ? _editions.GetValueOrDefault(edition, "") : "";
        EditionDescription.Text = description;
        EditionCard.IsVisible = description != "";

        var color = Color.Parse(ModPageVisual.GetColor(Username.Text ?? ""));
        HeaderGlyph.Foreground = new SolidColorBrush(color);
        HeaderIcon.Background = new SolidColorBrush(color, 0.12);
        HeaderIcon.BorderBrush = new SolidColorBrush(color, 0.4);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CanSubmit)
        {
            OnConfirm(sender, e);
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult((SelectedEdition!, Username.Text!));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(null);
    }
}
