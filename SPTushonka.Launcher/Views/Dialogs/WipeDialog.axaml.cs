using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class WipeDialog : UserControl
{
    private readonly TaskCompletionSource<bool> _result = new();

    public WipeDialog()
    {
        InitializeComponent();
    }

    public WipeDialog(string username)
        : this()
    {
        var locale = App.Services.GetRequiredService<LocaleHelper>();
        Heading.Text = locale.Get("profile_wipe_confirm_heading") + " ";
        Username.Text = username;
        KeptItems.ItemsSource = Enumerable.Range(1, 4).Select(i => locale.Get($"profile_wipe_warning_{i}")).ToList();
    }

    public Task<bool> Result => _result.Task;

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
    }
}
