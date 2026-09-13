using System.Diagnostics;
using System.Text;

namespace SPTarkov.Core.Helpers;

public class LinuxClipboard : IClipboard
{
    public void CopyFiles(string[] files)
    {
        throw new NotImplementedException("Copying files on Linux is not implemented yet.");
    }
    
    public bool CopyText(string value)
    {
        var sessionType = GetSessionType();
        var command = string.Empty;

        if (sessionType is not ("x11" or "wayland"))
        {
            throw new Exception($"Your window manager \"{sessionType}\" is not supported.");
        }

        value = value.Replace("\\", "\\\\").Replace("\"", "\\\"");      // Escapes on top of escapes

        if (sessionType == "x11")
            command = $"printf %b \"{value}\" | xclip -selection clipboard";

        if (sessionType == "wayland")
            command = $"wl-copy \"{value}\"";

        var process = LinuxHelper.ExecuteCommand(command);
        return process.ExitCode == 0;
    }

    // Check whether the user is using X11 or Wayland
    private static string GetSessionType()
    {
        var process = LinuxHelper.ExecuteCommand("echo $XDG_SESSION_TYPE");
        return process.StandardOutput.ReadToEnd().Trim();
    }
}
