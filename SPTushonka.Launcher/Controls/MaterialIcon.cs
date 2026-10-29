using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Draws a Material icon path inside its 24x24 design box. PathIcon would stretch the path to fill the control and enlarge small glyphs.
/// </summary>
public class MaterialIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<MaterialIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<MaterialIcon>();

    static MaterialIcon()
    {
        AffectsRender<MaterialIcon>(DataProperty, ForegroundProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null)
        {
            return;
        }

        var scale = Bounds.Width / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(Foreground, null, Data);
        }
    }
}
