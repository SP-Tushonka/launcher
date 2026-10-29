using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class ProgressDialog : UserControl
{
    public ProgressDialog()
    {
        InitializeComponent();
    }

    public event Action? Cancelled;

    public string? Step
    {
        get => StepText.Text;
        set => StepText.Text = value;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
    }
}
