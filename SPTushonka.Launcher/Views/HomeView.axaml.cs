using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Models;

namespace SPTarkov.Launcher.Views;

public partial class HomeView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly HttpHelper _http = App.Services.GetRequiredService<HttpHelper>();
    private readonly List<ServerCardModel> _cards;
    private CancellationTokenSource? _polling;

    public HomeView()
    {
        InitializeComponent();

        var locale = App.Services.GetRequiredService<LocaleHelper>();
        _cards = App.Services.GetRequiredService<ConfigHelper>().GetServers().Select(s => new ServerCardModel(s, locale)).ToList();
        Servers.ItemsSource = _cards;
        EmptyAlert.IsVisible = _cards.Count == 0;
    }

    public event Action? AddServerRequested;
    public event Action<Server>? ServerSelected;
    public event Action<Server>? EditRequested;
    public event Action<Server>? RemoveRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                await Task.WhenAll(_cards.Select(c => c.ProbeAsync(_http, token)));
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        // Taps on the card's edit and delete buttons belong to those buttons
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: ServerCardModel card })
        {
            ServerSelected?.Invoke(card.Server);
        }
    }

    private void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ServerCardModel card })
        {
            EditRequested?.Invoke(card.Server);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ServerCardModel card })
        {
            RemoveRequested?.Invoke(card.Server);
        }
    }

    private void OnAddServer(object? sender, RoutedEventArgs e)
    {
        AddServerRequested?.Invoke();
    }
}
