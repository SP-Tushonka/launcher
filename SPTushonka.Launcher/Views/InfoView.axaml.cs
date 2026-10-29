using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;
using SPTarkov.Launcher.Controls;

namespace SPTarkov.Launcher.Views;

public partial class InfoView : UserControl
{
    private static readonly (string Name, string Icon, string DescriptionKey, string Link)[] Links =
    [
        ("Forge", "IconExtension", "info_forge_desc", "https://sp-mod.com/"),
        ("SPT WIKI", "IconMenuBook", "info_spt_desc", "https://wiki.sp-tushonka.com/en/home"),
        ("Github", "IconGitHub", "info_github_desc", "https://github.com/SP-Tushonka"),
        ("Discord", "IconDiscord", "info_discord_desc", "https://discord.sp-tushonka.com"),
        ("Escape From Tarkov Wiki", "IconPublic", "info_wiki_desc", "https://escapefromtarkov.fandom.com/wiki/Escape_from_Tarkov_Wiki"),
        ("Escape From Tarkov Wiki Changelog", "IconHistory", "info_changelog_desc", "https://escapefromtarkov.fandom.com/wiki/Changelog"),
        ("SPT Launcher Github", "IconTranslate", "info_translations", "https://github.com/SP-Tushonka/launcher/issues"),
    ];

    private static readonly IBrush IconTile = Brush.Parse("#12FFFFFF");

    public InfoView()
    {
        InitializeComponent();

        var locale = App.Services.GetRequiredService<LocaleHelper>();
        for (var i = 0; i < Links.Length; i++)
        {
            if (i % 3 == 0)
            {
                Cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var (name, icon, descriptionKey, link) = Links[i];
            var card = Card(Icon(icon), name, locale.Get(descriptionKey), new Uri(link).Host);
            card.Tapped += (_, _) => ExternalLinks.Open(link);
            Grid.SetRow(card, i / 3);
            Grid.SetColumn(card, i % 3);
            Cards.Children.Add(card);
        }
    }

    // The view is built before it joins the window, so theme resources come from the application
    private static Geometry Icon(string key)
    {
        return (Geometry)Application.Current!.FindResource(key)!;
    }

    private static GlassBorder Card(Geometry icon, string name, string description, string domain)
    {
        var link = new StackPanel
        {
            Classes = { "external" },
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Classes = { "caption" },
                    Text = domain,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new MaterialIcon
                {
                    Data = Icon("IconOpenInNew"),
                    Width = 18,
                    Height = 18,
                },
            },
        };

        return new GlassBorder
        {
            Classes = { "card", "interactive", "info" },
            Child = new StackPanel
            {
                Children =
                {
                    new Panel
                    {
                        Children =
                        {
                            new Border
                            {
                                Width = 32,
                                Height = 32,
                                CornerRadius = new CornerRadius(6),
                                Background = IconTile,
                                HorizontalAlignment = HorizontalAlignment.Left,
                                Child = new MaterialIcon
                                {
                                    Data = icon,
                                    Width = 20,
                                    Height = 20,
                                },
                            },
                            link,
                        },
                    },
                    new TextBlock
                    {
                        Classes = { "body1" },
                        Text = name,
                        Margin = new Thickness(0, 12, 0, 0),
                    },
                    new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
    }
}
