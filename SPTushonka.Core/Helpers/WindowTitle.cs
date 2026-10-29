namespace SPTarkov.Core.Helpers;

public enum LauncherPage
{
    Servers,
    Profiles,
    Profile,
    Settings,
    Info,
}

/// <summary>
///     Builds the window title from the page and the open session
/// </summary>
public class WindowTitle(LocaleHelper localeHelper, StateHelper stateHelper)
{
    public const string AppName = "SPT Launcher";

    /// <summary>
    ///     Get the window title for a page
    /// </summary>
    /// <param name="page">Page being shown</param>
    /// <returns>Title naming the app, the page and the server or profile it belongs to</returns>
    public string For(LauncherPage page)
    {
        var segments = page switch
        {
            LauncherPage.Profiles => WithServer(localeHelper.Get("profiles")),
            LauncherPage.Profile => WithServer(ProfileLabel()),
            LauncherPage.Settings => [localeHelper.Get("settings")],
            LauncherPage.Info => [localeHelper.Get("info")],
            _ => [localeHelper.Get("servers")],
        };

        return string.Join(" - ", segments.Prepend(AppName));
    }

    private List<string> WithServer(string? trailing)
    {
        var segments = new List<string>();
        var serverName = stateHelper.SelectedServer?.Name;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            segments.Add($"{localeHelper.Get("server")}: {serverName}");
        }

        if (!string.IsNullOrWhiteSpace(trailing))
        {
            segments.Add(trailing);
        }

        return segments;
    }

    private string? ProfileLabel()
    {
        var username = stateHelper.SelectedProfile?.Username;
        return string.IsNullOrWhiteSpace(username) ? null : $"{localeHelper.Get("profile")}: {username}";
    }
}
