using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Models;

public enum ServerStatus
{
    Checking,
    Online,
    Offline,
}

public class ServerCardModel(Server server, LocaleHelper locale) : INotifyPropertyChanged
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly IBrush Online = Brush.Parse("#66BB6A");
    private static readonly IBrush Muted = Brush.Parse("#80FFFFFF");
    private static readonly IBrush OfflineDot = Brush.Parse("#EF5350");
    private static readonly IBrush CheckingDot = Brush.Parse("#BDBDBD");
    private static readonly BoxShadows OnlineGlow = BoxShadows.Parse("0 0 6 0 #CC66BB6A");

    private ServerStatus _status = ServerStatus.Checking;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Server Server { get; } = server;
    public string Name => Server.Name;
    public string Address => Server.IpAddress;
    public bool Locked => Server.Locked;
    public bool Editable => !Server.Locked;

    public IBrush IconBrush { get; } = Brush.Parse(ModPageVisual.GetColor($"{server.Name}|{server.IpAddress}"));

    public ServerStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            foreach (var name in new[] { nameof(Status), nameof(StatusText), nameof(StatusBrush), nameof(DotBrush), nameof(DotGlow) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public string StatusText =>
        locale
            .Get(
                Status switch
                {
                    ServerStatus.Online => "server_card_status_online",
                    ServerStatus.Offline => "server_card_status_offline",
                    _ => "server_card_status_checking",
                }
            )
            .ToUpperInvariant();

    public IBrush StatusBrush => Status == ServerStatus.Online ? Online : Muted;

    public IBrush DotBrush =>
        Status switch
        {
            ServerStatus.Online => Online,
            ServerStatus.Offline => OfflineDot,
            _ => CheckingDot,
        };

    public BoxShadows DotGlow => Status == ServerStatus.Online ? OnlineGlow : default;

    public async Task ProbeAsync(HttpHelper http, CancellationToken token)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
        probe.CancelAfter(ProbeTimeout);

        var online = await http.PingServerAsync(Server.IpAddress, probe.Token);
        await Dispatcher.UIThread.InvokeAsync(() => Status = online ? ServerStatus.Online : ServerStatus.Offline);
    }
}
