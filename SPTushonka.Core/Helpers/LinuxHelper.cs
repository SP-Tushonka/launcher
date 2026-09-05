using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;

namespace SPTarkov.Core.Helpers;

public class LinuxHelper(ILogger<LinuxHelper> logger, ConfigHelper configHelper)
{
    /// <summary>
    /// Runs an executable or Wine tool (<c>winecfg</c>, <c>winetricks</c>, <c>regedit</c>, etc.) inside the configured Wine/Proton
    /// prefix via <c>umu-run</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// RunInPrefix("EscapeFromTarkov.exe", args);           // launch any executable in the current working dir
    /// RunInPrefix("winecfg");                              // open the winecfg menu
    /// RunInPrefix("winetricks", ["-q", "win11"]);          // set the prefix's Windows version to Windows 11
    /// RunInPrefix("winetricks", ["-q", "dotnetdesktop9"]); // install .NET Desktop 9
    /// RunInPrefix("regedit");                              // open the regedit tool
    /// </code>
    /// </example>
    public bool RunInPrefix(string cmd = "", List<string>? args = null)
    {
        // This looks something like: "/home/{username}/Games/tarkov"
        var prefixPath = configHelper.GetConfig().LinuxSettings.PrefixPath;

        // This looks something like this: "/home/{username}/.local/bin/umu-run"
        var umuPath = configHelper.GetConfig().LinuxSettings.UmuPath;

        // this looks something like this: "/home/{username}/.steam/steam/compatibilitytools.d/GE-Proton11-5"
        var protonPath = configHelper.GetConfig().LinuxSettings.ProtonVersion;

        // This looks something like this: "MANGOHUD=1 PROTON_USE_XALIA=0 --disable-software-renderer"
        var defaultEnv = configHelper.GetConfig().LinuxSettings.DefaultEnv;

        if (string.IsNullOrEmpty(prefixPath) || string.IsNullOrEmpty(umuPath) || string.IsNullOrEmpty(protonPath))
        {
            logger.LogError("Prefix path and umu path and proton version are required");
            return false;
        }

        // this looks something like: "/home/{username}/Games/SPT"
        var sptPath = configHelper.GetGamePath();

        ProcessStartInfo? process;

        // User must install gamemode from package manager, try catch below will log it
        if (configHelper.GetConfig().LinuxSettings.GameMode)
        {
            process = new ProcessStartInfo
            {
                FileName = "gamemoderun",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sptPath,
                Environment = { { "WINEPREFIX", prefixPath }, { "PROTONPATH", protonPath } },
                ArgumentList = { umuPath, cmd },
            };
        }
        else
        {
            process = new ProcessStartInfo
            {
                FileName = umuPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sptPath,
                Environment = { { "WINEPREFIX", prefixPath }, { "PROTONPATH", protonPath } },
                ArgumentList = { cmd },
            };
        }

        // Add these individually so they are not wrapped in ""
        if (args != null)
        {
            foreach (var arg in args)
            {
                process.ArgumentList.Add(arg);
            }
        }

        // Combine DefaultEnv with LaunchSettings tokens
        var tokens = new List<string>();

        if (!string.IsNullOrEmpty(defaultEnv))
        {
            tokens.Add(defaultEnv);
        }

        tokens.AddRange(TokenizeLaunchSettings(configHelper.GetConfig().LinuxSettings.LaunchSettings));

        // Process all tokens with the same logic
        foreach (var token in tokens)
        {
            var separator = token.IndexOf('=');

            // args start with a -, so does anything thats not NAME=VALUE
            if (token.StartsWith('-') || separator <= 0)
            {
                process.ArgumentList.Add(token);
                continue;
            }

            // indexer not Add, a repeated name should overwrite instead of throwing
            // Remove quotes from value if present
            var value = token[(separator + 1)..].Trim('"');
            process.Environment[token[..separator]] = value;
        }

        var overrides = process.Environment.TryGetValue("WINEDLLOVERRIDES", out var existing) ? existing ?? "" : "";
        foreach (var dll in new[] { "winhttp", "version" })
        {
            if (overrides.Contains($"{dll}=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            overrides = string.IsNullOrEmpty(overrides) ? $"{dll}=n,b" : $"{overrides};{dll}=n,b";
        }

        process.Environment["WINEDLLOVERRIDES"] = overrides;

        try
        {
            Process.Start(process);
            logger.LogInformation("Game process started on linux");
        }
        catch (Exception ex)
        {
            logger.LogError("Starting game process failed: {Exception}", ex);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Splits launch options on whitespace, keeping quoted runs together so values with spaces survive, e.g.
    /// <c>MANGOHUD=1 -arg="/path with spaces" WINEDLLOVERRIDES="d3d11=n,b"</c>.
    /// </summary>
    /// <remarks>
    /// Quotes are stripped, as they only group the value - use <c>\"</c> for a literal one. Single quotes work too, and
    /// an unterminated quote just runs to the end rather than throwing.
    /// </remarks>
    internal static List<string> TokenizeLaunchSettings(string? launchSettings)
    {
        var tokens = new List<string>();

        if (string.IsNullOrWhiteSpace(launchSettings))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var quote = '\0';

        // not just a length check on the builder, so an empty quoted value ("") still gives a token
        var started = false;

        for (var index = 0; index < launchSettings.Length; index++)
        {
            var character = launchSettings[index];

            if (character == '\\' && index + 1 < launchSettings.Length && launchSettings[index + 1] is '"' or '\'')
            {
                current.Append(launchSettings[++index]);
                started = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(character);
            started = true;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public Task<List<string>> GetProtonVersions()
    {
        var protonSet = new HashSet<string>();

        var allPaths = new List<string>(Paths.ProtonPaths);
        allPaths.AddRange(configHelper.GetConfig().LinuxSettings.ProtonPaths);

        foreach (var rootPath in allPaths)
        {
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(rootPath))
            {
                // Used to verify Proton directories
                string compatFilePath = Path.Combine(directory, "compatibilitytool.vdf");

                if (directory.Contains("LegacyRuntime") || !File.Exists(compatFilePath))
                {
                    continue;
                }

                protonSet.Add(directory);
            }
        }

        return Task.FromResult(new List<string>(protonSet));
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    public static extern int SetEnvironmentVariableNative(string name, string value, int overwrite);
}
