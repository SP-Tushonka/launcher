using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SPTarkov.Launcher.Controls;

public enum ToastSeverity
{
    Normal,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
///     Toasts stacked in the bottom right corner with the newest on top. At most three show at once for two seconds each and the rest wait their turn.
/// </summary>
public class ToastHost : StackPanel
{
    private readonly Queue<(string Message, ToastSeverity Severity)> _pending = new();

    private const int MaxShown = 3;
    private const double ShownOpacity = 0.95;
    private static readonly TimeSpan Visible = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Background colour and outlined Material icon for each severity
    /// </summary>
    private static readonly Dictionary<ToastSeverity, (string Color, string Icon)> Looks = new()
    {
        [ToastSeverity.Normal] = (
            "#27272F",
            "M17 10H7v2h10v-2zm2-7h-1V1h-2v2H8V1H6v2H5c-1.11 0-1.99.9-1.99 2L3 19c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H5V8h14v11zm-5-5H7v2h7v-2z"
        ),
        [ToastSeverity.Info] = (
            "#3299FF",
            "M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z"
        ),
        [ToastSeverity.Success] = (
            "#0BBA83",
            "M16.59 7.58 10 14.17l-3.59-3.58L5 12l5 5 8-8zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8z"
        ),
        [ToastSeverity.Warning] = ("#FFA800", "M12 5.99 19.53 19H4.47L12 5.99M12 2 1 21h22L12 2zm1 14h-2v2h2v-2zm0-6h-2v4h2v-4z"),
        [ToastSeverity.Error] = (
            "#F64E62",
            "M11 15h2v2h-2zm0-8h2v6h-2zm.99-5C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8z"
        ),
    };

    private const string CloseIcon =
        "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";

    public static ToastHost? Current { get; private set; }

    public ToastHost()
    {
        Spacing = 16;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 24, 24);
        Current = this;
    }

    /// <summary>
    ///     Show a toast from any thread. Does nothing before the window exists.
    /// </summary>
    /// <param name="message">Text of the toast</param>
    /// <param name="severity">Colour and icon of the toast</param>
    public static void Show(string message, ToastSeverity severity = ToastSeverity.Normal)
    {
        Dispatcher.UIThread.Post(() => Current?.Add(message, severity));
    }

    private void Add(string message, ToastSeverity severity)
    {
        if (Children.Count >= MaxShown)
        {
            _pending.Enqueue((message, severity));
            return;
        }

        Present(message, severity);
    }

    private void Present(string message, ToastSeverity severity)
    {
        var (color, icon) = Looks[severity];
        var close = new Button
        {
            Classes = { "icon" },
            Padding = new Thickness(3),
            Margin = new Thickness(24, 0, -8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new MaterialIcon
            {
                Data = Geometry.Parse(CloseIcon),
                Width = 20,
                Height = 20,
                Foreground = Brushes.White,
            },
        };
        DockPanel.SetDock(close, Dock.Right);

        var glyph = new MaterialIcon
        {
            Data = Geometry.Parse(icon),
            Width = 24,
            Height = 24,
            Opacity = 0.9,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 7, 12, 7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(glyph, Dock.Left);

        var toast = new Border
        {
            Background = Brush.Parse(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 6),
            MinWidth = 288,
            MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BoxShadow = BoxShadows.Parse("0 3 5 -1 #33000000, 0 6 10 0 #24000000, 0 1 18 0 #1F000000"),
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = Fade }],
            Child = new DockPanel
            {
                Children =
                {
                    glyph,
                    close,
                    new TextBlock
                    {
                        Text = message,
                        FontSize = 14,
                        LineHeight = 20.02,
                        LetterSpacing = 0.15,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(0, 8),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        var state = new Toast(toast);
        close.Click += (_, _) => Hide(state, cancellable: false);
        toast.PointerEntered += (_, _) => OnPointerEntered(state);
        toast.PointerExited += (_, _) => OnPointerExited(state);
        Children.Insert(0, toast);

        toast.Opacity = ShownOpacity;
        DispatcherTimer.RunOnce(() => OnExpired(state), Fade + Visible);
    }

    private void OnExpired(Toast toast)
    {
        toast.Expired = true;
        if (!toast.Hovered)
        {
            Hide(toast, cancellable: true);
        }
    }

    // Hovering a toast that is fading out brings it back until the pointer leaves
    private void OnPointerEntered(Toast toast)
    {
        toast.Hovered = true;
        if (toast is { Hiding: true, Cancellable: true })
        {
            toast.RemoveTimer?.Dispose();
            toast.Hiding = false;
            toast.View.Opacity = ShownOpacity;
        }
    }

    private void OnPointerExited(Toast toast)
    {
        toast.Hovered = false;
        if (toast is { Expired: true, Hiding: false })
        {
            Hide(toast, cancellable: true);
        }
    }

    private void Hide(Toast toast, bool cancellable)
    {
        if (toast.Hiding || !Children.Contains(toast.View))
        {
            return;
        }

        toast.Hiding = true;
        toast.Cancellable = cancellable;
        toast.View.Opacity = 0;
        toast.RemoveTimer = DispatcherTimer.RunOnce(() => Remove(toast), Fade);
    }

    private void Remove(Toast toast)
    {
        Children.Remove(toast.View);
        while (Children.Count < MaxShown && _pending.TryDequeue(out var next))
        {
            Present(next.Message, next.Severity);
        }
    }

    private sealed class Toast(Border view)
    {
        public Border View { get; } = view;
        public bool Hovered { get; set; }
        public bool Expired { get; set; }
        public bool Hiding { get; set; }
        public bool Cancellable { get; set; }
        public IDisposable? RemoveTimer { get; set; }
    }
}
