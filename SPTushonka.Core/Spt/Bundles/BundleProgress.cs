namespace SPTarkov.Core.SPT.Bundles;

public sealed record BundleProgress
{
    public int Current { get; init; }

    public int Total { get; init; }

    public string BundleName { get; init; } = string.Empty;

    public long DownloadedBytes { get; init; }

    public long TotalBytes { get; init; }

    public double BytesPerSecond { get; init; }

    public bool IsDownloading { get; init; }

    public double Percentage
    {
        get { return Total == 0 ? 0 : (double)Current / Total * 100; }
    }

    public string DownloadSpeed
    {
        get { return $"{FormatBytes((long)BytesPerSecond)}/s"; }
    }

    public string FileSizeInfo
    {
        get { return $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}"; }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
