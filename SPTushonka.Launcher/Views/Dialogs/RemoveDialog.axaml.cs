using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SPTarkov.Launcher.Views.Dialogs;

public partial class RemoveDialog : UserControl
{
    private readonly TaskCompletionSource<bool> _result = new();

    public RemoveDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Confirmation for removing a server or profile
    /// </summary>
    /// <param name="question">Question shown above the details</param>
    /// <param name="confirm">Label of the red confirm button</param>
    /// <param name="cancel">Label of the cancel button</param>
    /// <param name="details">Label and value rows. Mono values such as addresses and IDs use the monospace font.</param>
    public RemoveDialog(string question, string confirm, string cancel, IEnumerable<(string Label, string Value, bool Mono)> details)
        : this()
    {
        Question.Text = question;
        ConfirmText.Text = confirm.ToUpperInvariant();
        CancelText.Text = cancel.ToUpperInvariant();

        var row = 0;
        foreach (var (label, value, mono) in details)
        {
            Details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var caption = new TextBlock { Classes = { "caption" }, Text = label };
            var text = new TextBlock { Text = value };
            if (mono)
            {
                text.Classes.Add("mono");
                text.LetterSpacing = 0.28;
            }

            Grid.SetRow(caption, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            Details.Children.Add(caption);
            Details.Children.Add(text);
            row++;
        }
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
