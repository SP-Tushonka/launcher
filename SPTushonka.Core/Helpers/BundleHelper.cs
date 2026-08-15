using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Bundles;

namespace SPTarkov.Core.Helpers;

public class BundleHelper(ILogger<BundleHelper> logger, HttpHelper httpHelper, StateHelper stateHelper, ConfigHelper configHelper)
{
    private const int MaxConcurrentDownloads = 8;
    private const int MaxAttemptsPerBundle = 3;
    private const int ReportIntervalMs = 100;

    public string? ErrorMessage { get; private set; }

    private string CacheFolder
    {
        get { return Paths.BundleCacheFolder(configHelper.GetGamePath()); }
    }

    private string CacheManifest
    {
        get { return Paths.BundleCacheManifest(configHelper.GetGamePath()); }
    }

    public bool IsLocalServer
    {
        get
        {
            var address = stateHelper.SelectedServer?.IpAddress ?? string.Empty;
            return address.Contains("127.0.0.1") || address.Contains("localhost");
        }
    }

    public async Task<bool> AcquireBundlesAsync(IProgress<BundleProgress>? progress, CancellationToken token)
    {
        ErrorMessage = null;

        if (IsLocalServer)
        {
            logger.LogInformation("Local server, bundles are loaded in place and need no acquisition");
            return true;
        }

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

        var cache = await LoadCacheAsync(token);
        var current = new ConcurrentDictionary<string, BundleCacheEntry>();

        var stale = new List<BundleManifestItem>();

        foreach (var bundle in manifest)
        {
            var path = Path.Join(CacheFolder, bundle.FileName);

            if (TryValidate(path, bundle, cache, out var entry))
            {
                current[bundle.FileName] = entry;
                continue;
            }

            stale.Add(bundle);
        }

        logger.LogInformation("{Fresh} bundles cached, {Stale} to download", manifest.Count - stale.Count, stale.Count);

        var acquired = await DownloadAsync(stale, manifest.Count - stale.Count, manifest.Count, current, progress, token);

        await SaveCacheAsync(current, token);

        return acquired;
    }

    private bool TryValidate(
        string path,
        BundleManifestItem bundle,
        Dictionary<string, BundleCacheEntry> cache,
        out BundleCacheEntry entry
    )
    {
        entry = null!;

        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;

        if (
            cache.TryGetValue(bundle.FileName, out var cached)
            && cached.Size == size
            && cached.ModifiedUtcTicks == modified
            && cached.Crc == bundle.Crc
        )
        {
            entry = cached;
            return true;
        }

        var crc = HashFile(path);

        if (crc != bundle.Crc)
        {
            return false;
        }

        entry = new BundleCacheEntry
        {
            Size = size,
            ModifiedUtcTicks = modified,
            Crc = crc,
        };

        return true;
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
        List<BundleManifestItem> stale,
        int alreadyFresh,
        int total,
        ConcurrentDictionary<string, BundleCacheEntry> current,
        IProgress<BundleProgress>? progress,
        CancellationToken token
    )
    {
        if (stale.Count == 0)
        {
            progress?.Report(new BundleProgress { Current = total, Total = total });
            return true;
        }

        var completed = alreadyFresh;
        var downloadedBytes = 0L;
        var totalBytes = stale.Sum(bundle => bundle.Size);

        var failures = new ConcurrentBag<string>();
        var started = Stopwatch.StartNew();

        var lastReportMs = 0L;
        var lastReportBytes = 0L;
        var speed = 0d;

        void Report(string bundleName, bool force)
        {
            var nowMs = (long)started.Elapsed.TotalMilliseconds;
            var previous = Interlocked.Read(ref lastReportMs);

            if (!force && nowMs - previous < ReportIntervalMs)
            {
                return;
            }

            if (!force && Interlocked.CompareExchange(ref lastReportMs, nowMs, previous) != previous)
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
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDownloads, CancellationToken = token },
            async (bundle, ct) =>
            {
                var destination = Path.Join(CacheFolder, bundle.FileName);

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

                                Report(bundle.FileName, false);
                            },
                            ct
                        );

                        var info = new FileInfo(destination);

                        current[bundle.FileName] = new BundleCacheEntry
                        {
                            Size = info.Length,
                            ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                            Crc = bundle.Crc,
                        };

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

    private async Task<Dictionary<string, BundleCacheEntry>> LoadCacheAsync(CancellationToken token)
    {
        if (!File.Exists(CacheManifest))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(CacheManifest);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, BundleCacheEntry>>(stream, cancellationToken: token) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Bundle cache unreadable, rebuilding");
            return [];
        }
    }

    /// <summary>Writes only the bundles seen this run, so entries for removed mods drop out.</summary>
    private async Task SaveCacheAsync(ConcurrentDictionary<string, BundleCacheEntry> entries, CancellationToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheManifest);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(CacheManifest);
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the bundle cache");
        }
    }
}
