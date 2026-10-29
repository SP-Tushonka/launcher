using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Extensions;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.Patching;

namespace SPTarkov.Launcher;

public class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        Services = new ServiceCollection()
            .AddLogging(builder => builder.AddFileLogger())
            .AddSingleton<ConfigHelper>()
            .AddSingleton<StateHelper>()
            .AddSingleton<HttpHelper>()
            .AddSingleton<LocaleHelper>()
            .AddSingleton<SessionHelper>()
            .AddSingleton<GameHelper>()
            .AddSingleton<BundleHelper>()
            .AddSingleton<FilePatcher>()
            .AddSingleton<LinuxHelper>()
            .AddSingleton<WindowsClipboard>()
            .AddSingleton<TroubleshootingHelper>()
            .AddSingleton<WindowTitle>()
            .AddSingleton<TrayHelper>()
            .BuildServiceProvider();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // A second launch of this install brings the running window forward
            var logger = Services.GetRequiredService<ILogger<App>>();
            Program.InstanceGuard.StartActivationListener(() => Dispatcher.UIThread.Post(window.Surface), logger);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
