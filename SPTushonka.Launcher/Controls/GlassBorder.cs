using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Border that draws the blurred backdrop behind its fill. It renders itself because Border.Render is sealed.
/// </summary>
public class GlassBorder : Decorator
{
    public static readonly StyledProperty<BackdropLayer> LayerProperty = AvaloniaProperty.Register<GlassBorder, BackdropLayer>(
        nameof(Layer)
    );

    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<GlassBorder>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty = Border.BorderBrushProperty.AddOwner<GlassBorder>();

    public static readonly StyledProperty<Thickness> BorderThicknessProperty = Border.BorderThicknessProperty.AddOwner<GlassBorder>();

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<GlassBorder>();

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty = Border.BoxShadowProperty.AddOwner<GlassBorder>();

    static GlassBorder()
    {
        AffectsRender<GlassBorder>(
            LayerProperty,
            BackgroundProperty,
            BorderBrushProperty,
            BorderThicknessProperty,
            CornerRadiusProperty,
            BoxShadowProperty
        );
        AffectsMeasure<GlassBorder>(BorderThicknessProperty);
    }

    /// <summary>
    ///     Which blurred backdrop the border samples
    /// </summary>
    public BackdropLayer Layer
    {
        get => GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Backdrop.Changed += InvalidateVisual;
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Backdrop.Changed -= InvalidateVisual;
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = Padding + BorderThickness;
        if (Child is null)
        {
            return new Size(inset.Left + inset.Right, inset.Top + inset.Bottom);
        }

        Child.Measure(availableSize.Deflate(inset));
        return Child.DesiredSize.Inflate(inset);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize).Deflate(Padding + BorderThickness));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var shape = new RoundedRect(bounds, CornerRadius);

        if (BoxShadow.Count > 0)
        {
            context.DrawRectangle(null, null, shape, BoxShadow);
        }

        using (context.PushClip(shape))
        {
            DrawBackdrop(context, bounds);

            if (Background is not null)
            {
                context.DrawRectangle(Background, null, bounds);
            }

            DrawBorder(context, bounds);
        }
    }

    private void DrawBackdrop(DrawingContext context, Rect bounds)
    {
        var glass = Backdrop.GlassFor(Layer);
        var topLevel = TopLevel.GetTopLevel(this);
        if (glass is null || topLevel is null || this.TranslatePoint(default, topLevel) is not { } origin)
        {
            return;
        }

        var scaling = topLevel.RenderScaling;
        var slice = new Rect(origin.X * scaling, origin.Y * scaling, bounds.Width * scaling, bounds.Height * scaling);
        context.DrawImage(glass, slice, bounds);
    }

    // The fill is clipped to the rounded shape, so an inset outline drawn inside it follows the corners
    private void DrawBorder(DrawingContext context, Rect bounds)
    {
        var t = BorderThickness;
        if (BorderBrush is null || t == default)
        {
            return;
        }

        if (t.IsUniform)
        {
            var inset = bounds.Deflate(t.Left / 2);
            var radius = new CornerRadius(
                Math.Max(0, CornerRadius.TopLeft - t.Left / 2),
                Math.Max(0, CornerRadius.TopRight - t.Left / 2),
                Math.Max(0, CornerRadius.BottomRight - t.Left / 2),
                Math.Max(0, CornerRadius.BottomLeft - t.Left / 2)
            );
            context.DrawRectangle(null, new Pen(BorderBrush, t.Left), new RoundedRect(inset, radius));
            return;
        }

        context.FillRectangle(BorderBrush, new Rect(0, 0, bounds.Width, t.Top));
        context.FillRectangle(BorderBrush, new Rect(bounds.Width - t.Right, 0, t.Right, bounds.Height));
        context.FillRectangle(BorderBrush, new Rect(0, bounds.Height - t.Bottom, bounds.Width, t.Bottom));
        context.FillRectangle(BorderBrush, new Rect(0, 0, t.Left, bounds.Height));
    }
}
