using System.Diagnostics;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Opens http and https links in the default browser and ignores any other scheme
/// </summary>
public static class ExternalLinks
{
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception)
        {
            // A browser that fails to open is not worth interrupting the user for
        }
    }
}
