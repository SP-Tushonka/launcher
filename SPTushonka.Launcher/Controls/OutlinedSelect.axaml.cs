using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Drop-down with an outlined frame, a floating label and an error line
/// </summary>
public partial class OutlinedSelect : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<OutlinedSelect, string?>(nameof(Label));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty = AvaloniaProperty.Register<OutlinedSelect, IEnumerable?>(
        nameof(ItemsSource)
    );

    public static readonly StyledProperty<object?> SelectedItemProperty = AvaloniaProperty.Register<OutlinedSelect, object?>(
        nameof(SelectedItem),
        defaultBindingMode: BindingMode.TwoWay
    );

    public static readonly StyledProperty<string?> ErrorTextProperty = AvaloniaProperty.Register<OutlinedSelect, string?>(
        nameof(ErrorText)
    );

    public static readonly StyledProperty<bool> HasErrorProperty = AvaloniaProperty.Register<OutlinedSelect, bool>(nameof(HasError));

    public OutlinedSelect()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

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

    public IDataTemplate? ItemTemplate
    {
        get => Picker.ItemTemplate;
        set => Picker.ItemTemplate = value;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HasErrorProperty || change.Property == ErrorTextProperty)
        {
            Classes.Set("error", HasError);
            Helper.Text = ErrorText;
            Helper.IsVisible = HasError && !string.IsNullOrEmpty(ErrorText);
        }
    }
}
