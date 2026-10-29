using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     1px rounded frame with a gap in its top edge for a floating label
/// </summary>
public class OutlineBorder : Control
{
    public static readonly StyledProperty<double> NotchStartProperty = AvaloniaProperty.Register<OutlineBorder, double>(nameof(NotchStart));

    public static readonly StyledProperty<double> NotchWidthProperty = AvaloniaProperty.Register<OutlineBorder, double>(nameof(NotchWidth));

    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<OutlineBorder, double>(
        nameof(CornerRadius),
        4
    );

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<OutlineBorder, IBrush?>(nameof(Stroke));

    static OutlineBorder()
    {
        AffectsRender<OutlineBorder>(NotchStartProperty, NotchWidthProperty, CornerRadiusProperty, StrokeProperty);
    }

    public double NotchStart
    {
        get => GetValue(NotchStartProperty);
        set => SetValue(NotchStartProperty, value);
    }

    public double NotchWidth
    {
        get => GetValue(NotchWidthProperty);
        set => SetValue(NotchWidthProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        // Inset by half the stroke so the 1px line lands on whole pixels
        var left = 0.5;
        var top = 0.5;
        var right = Bounds.Width - 0.5;
        var bottom = Bounds.Height - 0.5;
        var r = CornerRadius;
        var arc = new Size(r, r);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(NotchStart + NotchWidth, top), false);
            g.LineTo(new Point(right - r, top));
            g.ArcTo(new Point(right, top + r), arc, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(right, bottom - r));
            g.ArcTo(new Point(right - r, bottom), arc, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(left + r, bottom));
            g.ArcTo(new Point(left, bottom - r), arc, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(left, top + r));
            g.ArcTo(new Point(left + r, top), arc, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(NotchStart, top));
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Stroke, 1), geometry);
    }
}
