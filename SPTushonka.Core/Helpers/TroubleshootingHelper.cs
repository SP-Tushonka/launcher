using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;

namespace SPTarkov.Core.Helpers;

/// <summary>
///     Outcome of a troubleshooting action. <see cref="Blocked"/> means a file was in use or access was denied.
/// </summary>
public enum TroubleshootResult
{
    Success,
    Unavailable,
    Blocked,
    Failed,
}

public class TroubleshootingHelper(ILogger<TroubleshootingHelper> logger, ConfigHelper configHelper)
{
    /// <summary>
    ///     Copy the live game's settings into the SPT settings folder
    /// </summary>
    /// <returns>How the copy ended</returns>
    public TroubleshootResult LoadLiveSettings()
    {
        if (!Directory.Exists(Paths.EftSettingsFolder))
        {
            logger.LogWarning("Settings can not be loaded from live, path given: {EftSettingsFolder}", Paths.EftSettingsFolder);
            return TroubleshootResult.Unavailable;
        }

        try
        {
            Directory.CreateDirectory(Paths.SptSettingsFolder);
            foreach (var path in Directory.GetDirectories(Paths.EftSettingsFolder, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(path.Replace(Paths.EftSettingsFolder, Paths.SptSettingsFolder));
            }

            foreach (var file in Directory.GetFiles(Paths.EftSettingsFolder, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(Paths.EftSettingsFolder, Paths.SptSettingsFolder), true);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("Unable to load live settings {Exception}", e);
            return TroubleshootResult.Failed;
        }

        logger.LogInformation("Loaded live settings to SPTsettings folder");
        return TroubleshootResult.Success;
    }

    /// <summary>
    ///     Delete the SPT game settings and leave an empty settings folder
    /// </summary>
    /// <returns>How the delete ended</returns>
    public TroubleshootResult ClearGameSettings()
    {
        try
        {
            var settings = new DirectoryInfo(Paths.SptSettingsFolder);
            settings.Delete(true);
            Directory.CreateDirectory(settings.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Unable to clean game settings, UnauthorizedAccessException or IOException: {Exception}", e);
            return TroubleshootResult.Blocked;
        }
        catch (Exception e)
        {
            logger.LogError("Clearing game settings failed: {Exception}", e);
            return TroubleshootResult.Failed;
        }

        logger.LogInformation("Cleared game settings");
        return TroubleshootResult.Success;
    }

    /// <summary>
    ///     Delete the game's temp and cache folder
    /// </summary>
    /// <returns>How the delete ended</returns>
    public TroubleshootResult CleanTempFiles()
    {
        try
        {
            new DirectoryInfo(Paths.SptAppDataFolder).Delete(true);
        }
        catch (DirectoryNotFoundException)
        {
            return TroubleshootResult.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Unable to clean temp files, UnauthorizedAccessException or IOException: {Exception}", e);
            return TroubleshootResult.Blocked;
        }
        catch (Exception e)
        {
            logger.LogError("Unable to clean temp files: {Exception}", e);
            return TroubleshootResult.Failed;
        }

        logger.LogInformation("Cleaned temp files");
        return TroubleshootResult.Success;
    }

    /// <summary>
    ///     Get the log files worth attaching to a bug report. These are the selected profile, BepInEx, server and launcher logs and today's game trace.
    /// </summary>
    /// <param name="selectedProfile">Profile whose save is included, if any</param>
    /// <returns>Paths of the files that exist</returns>
    public List<string> CollectLogFiles(MiniProfile? selectedProfile)
    {
        var files = new List<string>();
        var gamePath = configHelper.GetGamePath();

        if (selectedProfile is not null)
        {
            files.Add(Path.Join(Paths.ProfilesFolder, $"{selectedProfile.ProfileId}.json"));
        }

        files.Add(Path.Join(gamePath, "BepInEx", "LogOutput.log"));
        files.Add(Path.Join(Paths.LogsFolder, $"spt{DateTime.Now:yyyy-MM-dd}.log"));
        files.Add(Paths.LauncherLog);

        var existing = files.Where(File.Exists).ToList();

        var tracePath = Path.Join(gamePath, "Logs");
        if (Directory.Exists(tracePath))
        {
            var trace = Directory
                .GetFiles(tracePath, $"{DateTime.Now:yyyy.MM.dd}_* traces.log", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(trace))
            {
                existing.Add(trace);
            }
        }

        return existing;
    }
}
