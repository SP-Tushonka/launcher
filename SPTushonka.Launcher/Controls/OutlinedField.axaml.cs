using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Text box with an outlined frame, a floating label and a helper line
/// </summary>
public partial class OutlinedField : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<OutlinedField, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HelperTextProperty = AvaloniaProperty.Register<OutlinedField, string?>(
        nameof(HelperText)
    );

    public static readonly StyledProperty<string?> ErrorTextProperty = AvaloniaProperty.Register<OutlinedField, string?>(nameof(ErrorText));

    public static readonly StyledProperty<bool> HasErrorProperty = AvaloniaProperty.Register<OutlinedField, bool>(nameof(HasError));

    public static readonly StyledProperty<string?> PlaceholderProperty = AvaloniaProperty.Register<OutlinedField, string?>(
        nameof(Placeholder)
    );

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<OutlinedField, string?>(
        nameof(Text),
        defaultBindingMode: BindingMode.TwoWay
    );

    public static readonly StyledProperty<object?> TrailingProperty = AvaloniaProperty.Register<OutlinedField, object?>(nameof(Trailing));

    public OutlinedField()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? HelperText
    {
        get => GetValue(HelperTextProperty);
        set => SetValue(HelperTextProperty, value);
    }

    /// <summary>
    ///     Text that replaces the helper line while <see cref="HasError"/> is set
    /// </summary>
    public string? ErrorText
    {
        get => GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HasErrorProperty || change.Property == ErrorTextProperty || change.Property == HelperTextProperty)
        {
            Classes.Set("error", HasError);
            var showError = HasError && !string.IsNullOrEmpty(ErrorText);
            Helper.Text = showError ? ErrorText : HelperText;
            Helper.IsVisible = !string.IsNullOrEmpty(Helper.Text);
        }
    }
}
