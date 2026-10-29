using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher.Controls;

/// <summary>
///     Native folder and file pickers that open where the current path points
/// </summary>
public static class Pickers
{
    /// <summary>
    ///     Ask for a folder
    /// </summary>
    /// <param name="owner">Control whose window owns the picker</param>
    /// <param name="current">Path the picker starts from</param>
    /// <returns>Chosen folder, or null when cancelled</returns>
    public static async Task<string?> PickFolderAsync(Control owner, string? current)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose Folder", SuggestedStartLocation = await StartAsync(storage, current) }
        );

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    ///     Ask for a file
    /// </summary>
    /// <param name="owner">Control whose window owns the picker</param>
    /// <param name="current">Path the picker starts from</param>
    /// <returns>Chosen file, or null when cancelled</returns>
    public static async Task<string?> PickFileAsync(Control owner, string? current)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Choose File", SuggestedStartLocation = await StartAsync(storage, current) }
        );

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<IStorageFolder?> StartAsync(IStorageProvider storage, string? current)
    {
        return FilePickerHelper.StartDirectory(current) is { } start ? await storage.TryGetFolderFromPathAsync(start) : null;
    }
}
