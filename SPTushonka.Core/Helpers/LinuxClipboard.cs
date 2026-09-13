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

        if (sessionType == "x11")
            return CopyTextX11(value);
        
        if (sessionType == "wayland")
            return CopyTextWayland(value);

        throw new Exception($"Your window manager \"{sessionType}\" is not supported.");
    }

    // Check whether the user is using X11 or Wayland
    private static string GetSessionType()
    {
        var process = LinuxHelper.ExecuteCommand("echo $XDG_SESSION_TYPE");
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private bool CopyTextX11(string value)
    {
        value = value.Replace("\\", "\\\\").Replace("\"", "\\\"");      // Escapes on top of escapes
        var process = LinuxHelper.ExecuteCommand($"printf %b \"{value}\" | xclip -selection clipboard");
        return process.ExitCode == 0;
    }

    private bool CopyTextWayland(string value)
    {
        // TODO: Add wayland support with wl-copy, the alternative of xclip for wayland
        throw new NotImplementedException("Wayland clipboard is not supported yet. Make a pull request :)");

        // Sample implementation, HAS NOT BEEN TESTED
        // value = value.Replace("\\", "\\\\").Replace("\"", "\\\"");      // Escapes on top of escapes
        // var process = LinuxHelper.ExecuteCommand($"printf %b \"{value}\" | wl-copy --primary");
        // return process.ExitCode == 0;
    }
}
