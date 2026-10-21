using System.Text.Json;
using SPTarkov.Core.Runtime;

const string Origin = "https://server.example:6969";
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task<Exception> Fail(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception e)
    {
        return e;
    }
    throw new Exception("Expected refusal");
}
Check(ServerEndpoint.Normalize(" SERVER.example:6969 ") == Origin, "hostname origin is canonical and case independent");
Check(ServerEndpoint.Normalize("https://server.example") == "https://server.example:443", "HTTPS default port is explicit on the wire");
Check(ServerEndpoint.Normalize("[2001:0DB8::1]:6969") == "https://[2001:db8::1]:6969", "IPv6 has compressed lowercase bracketed authority");
foreach (
    var value in new[]
    {
        "http://server.example:6969",
        "https://user:pass@server.example:6969",
        "server.example:6969/path",
        "server.example:6969?q=1",
        "server.example:6969#part",
        "server.example:0",
        "server.example:65536",
        "sérver.example:6969",
        "server.example\\bad:6969",
        "server.example:6969\nother",
    }
)
    Check(!ServerEndpoint.IsValid(value), "invalid authority refused: " + value.Replace('\n', ' '));

{
    var t = new Transport();
    var runtime = new OfflineRuntime(t);
    await runtime.EnsureBackendAsync("SERVER.example:6969", default, true);
    Check(
        t.Calls[0].BackendUrl == Origin && t.Calls[0].AllowServerStart == false,
        "remote connection cannot start a local server even when automatic startup is on"
    );
    Check(
        runtime.BoundBackendUrl == Origin && runtime.VerifiedBackendTlsSha256 == t.Pin,
        "verified remote origin and TLS identity are retained"
    );
    var count = t.Calls.Count;
    await Fail(() => runtime.ReadEnvironmentAsync(default));
    Check(
        runtime.IsExternalBackend && !runtime.EnvironmentAvailable && t.Calls.Count == count,
        "remote environment editing is unavailable before any request"
    );
    await runtime.PreflightAsync(Path.GetFullPath("fixture-game"), default);
    var wire = JsonSerializer.Serialize(t.Calls[^1], FileRuntimeTransport.Json);
    Check(
        !wire.Contains("backendUrl") && !wire.Contains("backendTlsSha256") && !wire.Contains("allowServerStart"),
        "non-ensure wire requests cannot retarget endpoint or TLS identity"
    );
    count = t.Calls.Count;
    await Fail(() => runtime.EnsureBackendAsync("other.example:6969", default));
    Check(t.Calls.Count == count, "endpoint changes require disconnection before transport");
    await Fail(() => runtime.EnsureBackendAsync(Origin, default, false, new string('b', 64)));
    Check(t.Calls.Count == count, "saved certificate changes require disconnection before transport");
    await runtime.ReleaseAsync(default);
    await runtime.EnsureBackendAsync("other.example:6969", default, false);
    Check(runtime.BoundBackendUrl == "https://other.example:6969", "verified disconnect permits selecting a different endpoint");
}
{
    var t = new Transport { Challenge = true };
    var runtime = new OfflineRuntime(t);
    var error = await Fail(() => runtime.EnsureBackendAsync(Origin, default, false));
    Check(
        error is ServerCertificateTrustException trust && trust.BackendUrl == Origin && trust.Fingerprint == t.Pin,
        "certificate challenge carries the exact reviewed origin and fingerprint"
    );
    Check(
        runtime.VerifiedBackendTlsSha256 is null && runtime.BoundBackendUrl is null,
        "unapproved certificate never becomes a verified connection"
    );
    t.Challenge = false;
    await runtime.EnsureBackendAsync(Origin, default, false, t.Pin);
    Check(
        t.Calls[^1].BackendTlsSha256 == t.Pin && runtime.VerifiedBackendTlsSha256 == t.Pin,
        "explicit accepted pin is sent on retry and verified against the response"
    );
}
{
    var t = new Transport { Echo = "https://other.example:6969" };
    var runtime = new OfflineRuntime(t);
    await Fail(() => runtime.EnsureBackendAsync(Origin, default, false));
    Check(runtime.BoundBackendUrl is null, "mismatched origin reply cannot establish a connection");
}
{
    var t = new Transport { Challenge = true, Pin = "bad" };
    var runtime = new OfflineRuntime(t);
    var error = await Fail(() => runtime.EnsureBackendAsync(Origin, default, false));
    Check(error is not ServerCertificateTrustException, "malformed challenge cannot reach the trust dialog");
}
Console.WriteLine($"{checks} remote endpoint checks passed; no sockets, servers or game started.");

sealed class Transport : IRuntimeTransport
{
    public List<RuntimeRequest> Calls { get; } = new();
    public string Pin { get; set; } = new('a', 64);
    public string? Echo { get; set; }
    public bool Challenge { get; set; }
    private string origin = "";

    public Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (request.BackendUrl is not null)
            origin = request.BackendUrl;
        var state =
            request.Operation == "release" ? "cleaned"
            : Challenge ? "failed"
            : "backendReady";
        return Task.FromResult(
            new RuntimeReply(
                1,
                request.RequestId,
                "fixture-session",
                state,
                BackendUrl: state == "cleaned" ? null : Echo ?? origin,
                CleanupVerified: state == "cleaned",
                GameCleanupVerified: state is "backendReady" or "cleaned",
                BackendTlsSha256: Pin,
                CertificateTrustRequired: Challenge,
                EnvironmentAvailable: false
            )
        );
    }
}
