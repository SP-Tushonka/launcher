using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class LoginDialog : UserControl
{
    private readonly TaskCompletionSource<string?> _result = new();

    public LoginDialog()
    {
        InitializeComponent();

        Username.PropertyChanged += (_, e) =>
        {
            if (e.Property == Controls.OutlinedField.TextProperty)
            {
                LoginButton.IsEnabled = !string.IsNullOrWhiteSpace(Username.Text);
            }
        };
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && LoginButton.IsEnabled)
            {
                OnLogin(s, e);
            }
        };
    }

    public Task<string?> Result => _result.Task;

    private void OnLogin(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(Username.Text);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(null);
    }
}
