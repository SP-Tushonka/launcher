using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Controls;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class LinuxSetupDialog : UserControl
{
    private readonly TaskCompletionSource _result = new();

    public LinuxSetupDialog()
    {
        InitializeComponent();
    }

    public Task Result => _result.Task;

    // The dialog stays open until the settings are valid
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var locale = App.Services.GetRequiredService<LocaleHelper>();
        if (Settings.HasErrors)
        {
            ToastHost.Show(locale.Get("setting_confirm_error_1"), ToastSeverity.Error);
            return;
        }

        ToastHost.Show(locale.Get("linux_add_success"), ToastSeverity.Success);
        _result.TrySetResult();
    }
}
