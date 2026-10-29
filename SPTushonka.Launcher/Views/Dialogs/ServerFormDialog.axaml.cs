using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Views.Dialogs;

/// <summary>
///     Form for adding a server or editing an existing one
/// </summary>
public partial class ServerFormDialog : UserControl
{
    private readonly TaskCompletionSource<(string Name, string Address)?> _result = new();

    public ServerFormDialog()
    {
        InitializeComponent();
    }

    public ServerFormDialog(Server? editing)
        : this()
    {
        var locale = App.Services.GetRequiredService<LocaleHelper>();
        var prefix = editing is null ? "server_add" : "server_edit";

        Title.Text = locale.Get($"{prefix}_title");
        Description.Text = locale.Get($"{prefix}_text");
        NameField.Label = $"{locale.Get($"{prefix}_name")}*";
        NameField.HelperText = locale.Get("server_add_name_help");
        AddressField.Label = $"{locale.Get($"{prefix}_address")}*";
        AddressField.HelperText = locale.Get("server_add_address_help");
        ConfirmText.Text = locale.Get($"{prefix}_confirm").ToUpperInvariant();
        CancelText.Text = locale.Get($"{prefix}_cancel").ToUpperInvariant();

        NameField.Text = editing?.Name;
        AddressField.Text = editing?.IpAddress;
        NameField.PropertyChanged += OnFieldChanged;
        AddressField.PropertyChanged += OnFieldChanged;
        KeyDown += OnKeyDown;
        Update();
    }

    public Task<(string Name, string Address)?> Result => _result.Task;

    private bool CanSubmit => !string.IsNullOrWhiteSpace(NameField.Text) && !string.IsNullOrWhiteSpace(AddressField.Text);

    private void OnFieldChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Controls.OutlinedField.TextProperty)
        {
            Update();
        }
    }

    // The header tint follows the colour the server card will get
    private void Update()
    {
        ConfirmButton.IsEnabled = CanSubmit;
        var color = Color.Parse(ModPageVisual.GetColor($"{NameField.Text}|{AddressField.Text}"));
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
        var address = AddressField.Text!.Replace("https://", "").Trim();
        _result.TrySetResult((NameField.Text!, address));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(null);
    }
}
