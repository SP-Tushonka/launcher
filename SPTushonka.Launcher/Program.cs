using Avalonia;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher;

public static class Program
{
    /// <summary>
    ///     Guard that keeps one launcher running per install
    /// </summary>
    public static SingleInstanceGuard InstanceGuard { get; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        if (!InstanceGuard.TryClaimPrimary())
        {
            InstanceGuard.Dispose();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGuard.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .UseWaylandWithFallback()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { WmClass = "SPT.Launcher" })
            .LogToTrace();
    }
}
