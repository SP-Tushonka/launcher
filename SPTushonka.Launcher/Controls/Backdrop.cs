using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace SPTarkov.Launcher.Controls;

public enum BackdropLayer
{
    Page,
    Dialog,
}

/// <summary>
///     Pre-rendered blur of the window background that glass panels draw from. Avalonia has no backdrop filter, so each
///     <see cref="GlassBorder"/> draws its own slice. Blur radii are Gaussian standard deviations.
/// </summary>
public static class Backdrop
{
    private const float PageBlur = 2;
    private const float GlassBlur = 20;
    private const float DialogBackdropBlur = 10;

    private static readonly SKColor PageOverlay = new(0, 0, 0, 153);
    private static readonly SKColor DialogScrim = new(33, 33, 33, 127);

    private static SKBitmap? _source;

    public static event Action? Changed;

    /// <summary>
    ///     Window background with its 60% black overlay and 2px blur applied
    /// </summary>
    public static Bitmap? Page { get; private set; }

    /// <summary>
    ///     Rendered page with the dialog backdrop blur and scrim applied
    /// </summary>
    public static Bitmap? DialogPage { get; private set; }

    private static Bitmap? PageGlass { get; set; }
    private static Bitmap? DialogGlass { get; set; }

    public static Bitmap? GlassFor(BackdropLayer layer)
    {
        return layer == BackdropLayer.Dialog ? DialogGlass : PageGlass;
    }

    /// <summary>
    ///     Render the window background and the glass that sits on it
    /// </summary>
    /// <param name="clientSize">Window client size</param>
    /// <param name="scaling">Window render scaling</param>
    /// <param name="useArtwork">Draw the background artwork rather than the plain page colour</param>
    public static void RebuildPage(Size clientSize, double scaling, bool useArtwork)
    {
        var width = (int)Math.Round(clientSize.Width * scaling);
        var height = (int)Math.Round(clientSize.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var cover = useArtwork ? Cover(_source ??= LoadArtwork(), width, height) : Plain(width, height);

        var oldPage = Page;
        var oldGlass = PageGlass;

        // Stacked Gaussian blurs combine as the root of the summed variances
        Page = Blur(cover, PageBlur * (float)scaling, PageOverlay);
        PageGlass = Blur(cover, Combine(PageBlur, GlassBlur) * (float)scaling, PageOverlay);
        Changed?.Invoke();

        oldPage?.Dispose();
        oldGlass?.Dispose();
    }

    /// <summary>
    ///     Blur a still of the page for the dialog backdrop and the glass of dialogs on it
    /// </summary>
    /// <param name="renderedPage">Still of the page behind the dialog</param>
    /// <param name="scaling">Window render scaling</param>
    public static void RebuildDialog(Bitmap renderedPage, double scaling)
    {
        var oldPage = DialogPage;
        var oldGlass = DialogGlass;

        using var page = ToSkia(renderedPage);
        DialogPage = Blur(page, DialogBackdropBlur * (float)scaling, DialogScrim);
        DialogGlass = Blur(page, Combine(DialogBackdropBlur, GlassBlur) * (float)scaling, DialogScrim);
        Changed?.Invoke();

        oldPage?.Dispose();
        oldGlass?.Dispose();
    }

    private static SKBitmap Plain(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(39, 39, 47));
        return bitmap;
    }

    private static float Combine(float a, float b)
    {
        return MathF.Sqrt(a * a + b * b);
    }

    private static SKBitmap LoadArtwork()
    {
        using var stream = AssetLoader.Open(new Uri("avares://SPT.Launcher/Assets/bg.jpg"));
        return SKBitmap.Decode(stream);
    }

    // Scales the artwork to fill the window and crops the overflow evenly from both sides
    private static SKBitmap Cover(SKBitmap source, int width, int height)
    {
        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var drawWidth = source.Width * scale;
        var drawHeight = source.Height * scale;
        var dest = SKRect.Create((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);

        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return bitmap;
    }

    // Mirroring past the edges keeps the blur from darkening towards the window border
    private static Bitmap Blur(SKBitmap input, float sigma, SKColor overlay)
    {
        using var surface = SKSurface.Create(new SKImageInfo(input.Width, input.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var paint = new SKPaint())
        {
            paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Mirror);
            using var image = SKImage.FromBitmap(input);
            canvas.DrawImage(image, 0, 0, paint);
        }

        canvas.DrawColor(overlay, SKBlendMode.SrcOver);

        using var pixels = surface.PeekPixels();
        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            pixels.GetPixels(),
            new PixelSize(input.Width, input.Height),
            new Vector(96, 96),
            pixels.RowBytes
        );
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        var colorType = bitmap.Format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
        var skia = new SKBitmap(new SKImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, colorType, SKAlphaType.Premul));
        bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), skia.GetPixels(), skia.ByteCount, skia.RowBytes);
        return skia;
    }
}
