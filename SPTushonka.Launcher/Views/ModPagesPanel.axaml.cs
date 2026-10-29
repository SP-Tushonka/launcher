using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.SPT;
using SPTarkov.Launcher.Controls;
using SPTarkov.Launcher.Helpers;

namespace SPTarkov.Launcher.Views;

/// <summary>
///     Panel beside the rail listing every page the connected server's mods register
/// </summary>
public partial class ModPagesPanel : UserControl
{
    private readonly StateHelper _state = App.Services.GetRequiredService<StateHelper>();

    public ModPagesPanel()
    {
        InitializeComponent();

        PointerEntered += (_, _) => _state.HoldModPagesPanel();
        PointerExited += (_, _) => _ = _state.ReleaseModPagesPanel();
    }

    /// <summary>
    ///     Rebuild the entries and show or hide the panel to match the session
    /// </summary>
    public void Refresh()
    {
        var open = _state.ModPagesPanelOpen && _state.AllowServerPage && _state.ModPages.Count > 0;
        Opacity = open ? 1 : 0;
        RenderTransform = TransformOperations.Parse(open ? "none" : "translate(-12px, 0px)");
        IsHitTestVisible = open;

        if (Entries.Children.Count != _state.ModPages.Count)
        {
            Entries.Children.Clear();
            foreach (var page in _state.ModPages)
            {
                Entries.Children.Add(Entry(page));
            }
        }
    }

    private Border Entry(ModPage page)
    {
        var hasDescription = !string.IsNullOrWhiteSpace(page.Description);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(
            new TextBlock
            {
                Text = page.Name,
                FontSize = 14.4,
                FontWeight = FontWeight.SemiBold,
                LineHeight = 17.28,
                Foreground = Brushes.White,
            }
        );
        if (hasDescription)
        {
            text.Children.Add(
                new TextBlock
                {
                    Text = page.Description,
                    Margin = new Thickness(0, 2, 0, 0),
                    FontSize = 12.8,
                    LineHeight = 18.3,
                    Foreground = Brush.Parse("#99FFFFFF"),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }

        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            Background = Brush.Parse(ModPageVisual.GetColor(page.Name)),
            VerticalAlignment = hasDescription ? VerticalAlignment.Top : VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = ModPageVisual.GetInitials(page.Name),
                FontSize = 12.8,
                FontWeight = FontWeight.Bold,
                LetterSpacing = 0.38,
                LineHeight = double.NaN,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        badge.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(badge, Dock.Left);
        var entry = new Border
        {
            Classes = { "entry" },
            Child = new DockPanel { Children = { badge, text } },
        };

        entry.Tapped += (_, _) =>
        {
            ExternalLinks.Open($"https://{_state.SelectedServer?.IpAddress}{page.HomePage}");
            _state.SetModPagesPanelOpen(false);
        };

        return entry;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        _state.SetModPagesPanelOpen(false);
    }
}
