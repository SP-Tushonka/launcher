using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Bundles;

namespace SPTarkov.Core.Helpers;

public class BundleHelper(ILogger<BundleHelper> logger, HttpHelper httpHelper, ConfigHelper configHelper)
{
    private const int MaxConcurrentDownloads = 8;
    private const int MaxAttemptsPerBundle = 3;
    private const int ReportIntervalMs = 100;

    public string? ErrorMessage { get; private set; }

    private string CacheFolder
    {
        get { return Paths.BundleCacheFolder(configHelper.GetGamePath()); }
    }

    private string RuntimeFolder
    {
        get { return Path.Join(configHelper.GetGamePath(), "SPT_Runtime"); }
    }

    private string CachePathFor(BundleManifestItem bundle)
    {
        return Path.Join(CacheFolder, bundle.Crc.ToString("X8"), bundle.FileName);
    }

    private string ModPathFor(BundleManifestItem bundle)
    {
        return Path.Join(RuntimeFolder, bundle.ModPath, "bundles", bundle.FileName);
    }

    public async Task<bool> AcquireBundlesAsync(IProgress<BundleProgress>? progress, CancellationToken token)
    {
        ErrorMessage = null;

        List<BundleManifestItem>? manifest;

        try
        {
            manifest = await httpHelper.GameServerGet<List<BundleManifestItem>>(Urls.Bundles, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not fetch the bundle manifest");
            ErrorMessage = "Could not reach the server to list bundles";
            return false;
        }

        if (manifest is null || manifest.Count == 0)
        {
            logger.LogInformation("Server published no bundles");
            return true;
        }

        var missing = manifest.Where(bundle => !IsAvailable(bundle)).ToList();

        logger.LogInformation("{Have} bundles already present, {Missing} to download", manifest.Count - missing.Count, missing.Count);

        return await DownloadAsync(missing, manifest.Count - missing.Count, manifest.Count, progress, token);
    }

    private bool IsAvailable(BundleManifestItem bundle)
    {
        return IsAvailable(bundle, CachePathFor(bundle), ModPathFor(bundle));
    }

    public static bool IsAvailable(BundleManifestItem bundle, string cachePath, string modPath)
    {
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length == bundle.Size)
        {
            return true;
        }

        if (!File.Exists(modPath))
        {
            return false;
        }

        var info = new FileInfo(modPath);

        if (info.Length != bundle.Size)
        {
            return false;
        }
        
        // Same size and write time means this is the same file the server hashed
        if (info.LastWriteTimeUtc.Ticks == bundle.ModifiedUtcTicks)
        {
            return true;
        }

        return HashFile(modPath) == bundle.Crc;
    }

    private static uint HashFile(string path)
    {
        var crc = new Crc32();
        using var stream = File.OpenRead(path);

        var buffer = new byte[81920];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc.Append(buffer.AsSpan(0, read));
        }

        return crc.GetCurrentHashAsUInt32();
    }

    private async Task<bool> DownloadAsync(
        List<BundleManifestItem> missing,
        int alreadyPresent,
        int total,
        IProgress<BundleProgress>? progress,
        CancellationToken token
    )
    {
        if (missing.Count == 0)
        {
            progress?.Report(new BundleProgress { Current = total, Total = total });
            return true;
        }

        var completed = alreadyPresent;
        var downloadedBytes = 0L;

        // Known up front from the manifest
        var totalBytes = missing.Sum(bundle => bundle.Size);

        var failures = new ConcurrentBag<string>();
        var started = Stopwatch.StartNew();

        var lastReportMs = 0L;
        var lastReportBytes = 0L;
        var speed = 0d;

        void Report(string bundleName)
        {
            var nowMs = (long)started.Elapsed.TotalMilliseconds;
            var previous = Interlocked.Read(ref lastReportMs);

            if (nowMs - previous < ReportIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastReportMs, nowMs, previous) != previous)
            {
                return;
            }

            var running = Interlocked.Read(ref downloadedBytes);
            var window = (nowMs - previous) / 1000d;

            if (window > 0)
            {
                speed = (running - Interlocked.Exchange(ref lastReportBytes, running)) / window;
            }

            progress?.Report(
                new BundleProgress
                {
                    Current = Volatile.Read(ref completed),
                    Total = total,
                    BundleName = bundleName,
                    DownloadedBytes = running,
                    TotalBytes = totalBytes,
                    BytesPerSecond = speed,
                    IsDownloading = true,
                }
            );
        }

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDownloads, CancellationToken = token },
            async (bundle, ct) =>
            {
                var destination = CachePathFor(bundle);

                for (var attempt = 1; attempt <= MaxAttemptsPerBundle; attempt++)
                {
                    // Outside the try so a failed attempt can take its partial bytes back out of the
                    // running total before the retry starts counting from zero again
                    var lastReported = 0L;

                    try
                    {
                        await httpHelper.DownloadFileAsync(
                            Urls.BundleFile + bundle.FileName,
                            destination,
                            (written, _) =>
                            {
                                Interlocked.Add(ref downloadedBytes, written - lastReported);
                                lastReported = written;

                                Report(bundle.FileName);
                            },
                            ct
                        );

                        Interlocked.Increment(ref completed);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt < MaxAttemptsPerBundle)
                    {
                        Interlocked.Add(ref downloadedBytes, -lastReported);
                        logger.LogWarning(ex, "Download of {Bundle} failed, attempt {Attempt}", bundle.FileName, attempt);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Add(ref downloadedBytes, -lastReported);
                        logger.LogError(ex, "Giving up on {Bundle}", bundle.FileName);
                        failures.Add(bundle.FileName);
                        return;
                    }
                }
            }
        );

        if (!failures.IsEmpty)
        {
            ErrorMessage = $"Failed to download {failures.Count} bundle(s), e.g. {failures.First()}";
            return false;
        }

        progress?.Report(
            new BundleProgress
            {
                Current = total,
                Total = total,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes,
            }
        );

        return true;
    }
}
