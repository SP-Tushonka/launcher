using System.Net;
using System.Net.Sockets;

namespace SPT.GameConnections;

/// <summary>Allows TCP to the exact approved host, preserving private self-wakeup sockets.</summary>
public sealed class ConnectionPolicy
{
    public const string PolicyRevision = "spt-host-tcp-v2";
    public const int ExpectedFilterCount = 4;
    public string GameExecutable { get; }
    public IReadOnlyList<ApprovedEndpoint> Endpoints { get; }

    private ConnectionPolicy(string executable)
    {
        GameExecutable = executable;
        Endpoints = Array.AsReadOnly(new[] { new ApprovedEndpoint(IPAddress.Loopback, null, 6) });
    }

    /// <summary>Exact 127.0.0.1 TCP host; no 127/8, native ::1, UDP or DNS exception.</summary>
    public static ConnectionPolicy ForSupportedRuntime(string gameExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutable);
        if (!Path.IsPathFullyQualified(gameExecutable))
            throw new ArgumentException("The game executable must be an absolute path.", nameof(gameExecutable));
        var path = Path.GetFullPath(gameExecutable);
        if (!string.Equals(Path.GetFileName(path), "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only EscapeFromTarkov.exe is supported.", nameof(gameExecutable));
        if (!File.Exists(path))
            throw new FileNotFoundException("The game executable does not exist.", path);
        // Build identity is checked by the existing runtime preflight. No game DLL is loaded here.
        return new ConnectionPolicy(path);
    }
}

/// <summary>Null Port omits the remote-port condition; zero is never used as a wildcard.</summary>
public sealed record ApprovedEndpoint(IPAddress Address, ushort? Port, byte Protocol);

public sealed record ConnectionVerification(bool IsEnabled, string Detail, int OwnedFilterCount);

internal sealed record FilterRule(Guid Key, Guid Layer, ApprovedEndpoint? Endpoint)
{
    public bool Permit => Endpoint is not null;
}

internal static class FilterPlan
{
    internal static readonly Guid Provider = new("c1f25162-7eea-4d1d-9277-80312a9937c1");
    internal static readonly Guid Sublayer = new("76341b36-0cb7-407d-9adf-b16b7a89bd46");
    internal static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal const int MaximumRules = 34;

    internal static Guid Key(int slot) => new($"8248c323-bd21-4384-98fc-{slot + 1:x12}");

    internal static IReadOnlyList<FilterRule> Build(ConnectionPolicy policy)
    {
        var rules = new List<FilterRule>();
        foreach (var endpoint in policy.Endpoints)
        {
            var layer = endpoint.Address.AddressFamily == AddressFamily.InterNetwork ? ConnectV4 : ConnectV6;
            rules.Add(new FilterRule(Key(rules.Count), layer, endpoint));
            // True dual-stack IPv6 sockets may be classified only on V6 for an IPv4 target.
            if (endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
                rules.Add(new FilterRule(Key(rules.Count), ConnectV6, endpoint with { Address = endpoint.Address.MapToIPv6() }));
        }
        rules.Add(new FilterRule(Key(rules.Count), ConnectV4, null));
        rules.Add(new FilterRule(Key(rules.Count), ConnectV6, null));
        if (rules.Count > MaximumRules)
            throw new InvalidOperationException("Too many approved endpoints.");
        return rules.AsReadOnly();
    }
}
