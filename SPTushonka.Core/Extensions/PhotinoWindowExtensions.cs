using System.Drawing;
using Photino.NET;

namespace SPTarkov.Core.Extensions;

public static class PhotinoWindowExtensions
{
    public static PhotinoWindow SetIconFile(this PhotinoWindow window, Stream iconStream, string fileName)
    {
        var iconpath = ExtractEmbeddedResourceToTempFile(window.TemporaryFilesPath, iconStream, fileName);
        return iconpath != null ? window.SetIconFile(iconpath) : window;
    }

    // PhotinoWindow.Size's setter only calls into the native window when the new value differs from what it reads
    // back as the *current* size. Right after a resize, that read can still return the pre-resize value (GTK has
    // not caught up yet), so reverting with no delay can be silently dropped - the window would then be stuck 1px
    // larger than intended. This delay is comfortably above what was needed in testing.
    private const int NudgeRepaintRevertDelayMs = 50;

    /// <summary>
    /// Works around a WebKitGTK bug where restoring a minimized window can leave the WebView's rendering surface
    /// permanently un-painted (a blank/grey window showing only the native GTK background), most commonly seen on
    /// Nvidia + X11. WebKit only actually repaints once it sees a real size change, so nudging the size by a pixel
    /// and reverting it shortly after forces that repaint without any visible/lasting change in window size.
    /// </summary>
    /// <remarks>
    /// Only meaningful on Linux, where this bug has been observed - see <c>SetNvidiaLinuxEnv</c> in Launcher.cs for
    /// the accompanying <c>WEBKIT_DISABLE_DMABUF_RENDERER</c> workaround this pairs with. Cheap no-op on other
    /// platforms, so callers do not need to guard it themselves.
    /// </remarks>
    public static async Task NudgeRepaint(this PhotinoWindow window)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var size = window.Size;
        window.SetSize(new Size(size.Width + 1, size.Height));

        await Task.Delay(NudgeRepaintRevertDelayMs);

        window.SetSize(size);
    }

    private static string? ExtractEmbeddedResourceToTempFile(string temporaryFilesPath, Stream? iconStream, string fileName)
    {
        if (iconStream == null)
        {
            Console.WriteLine("Icon stream is null");
            return null;
        }
        var tempFile = Path.Join(temporaryFilesPath, fileName);
        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write);
        iconStream.CopyTo(fileStream);
        return tempFile;
    }
}
