namespace SPTarkov.Core.SPT.Bundles;

public sealed record BundleCacheEntry
{
    public required long Size { get; init; }

    public required long ModifiedUtcTicks { get; init; }

    public required uint Crc { get; init; }
}
