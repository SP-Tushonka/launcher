using System.Text.Json;
using SPTarkov.Core.Runtime;

int checks = 0;
const string profile = "0123456789abcdef01234567";
var directory = Path.GetFullPath("runtime-connection-fixture-no-game");
void Check(bool result, string name)
{
    if (!result)
        throw new InvalidOperationException(name);
    checks++;
}
async Task Reject(Func<Task> operation, string contains)
{
    try
    {
        await operation();
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(contains))
    {
        checks++;
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + contains);
}

var defaultRequest = new RuntimeRequest(1, "request", "launch", "session", profile, directory);
var json = JsonSerializer.Serialize(defaultRequest, FileRuntimeTransport.Json);
Check(!json.Contains("requireGameConnections"), "Legacy request omits default false feature flag");
json = JsonSerializer.Serialize(defaultRequest with { RequireGameConnections = true }, FileRuntimeTransport.Json);
Check(json.Contains("\"requireGameConnections\":true"), "Protected request serializes true feature flag");
var legacyReply = JsonSerializer.Deserialize<RuntimeReply>(
    "{\"protocol\":1,\"requestId\":\"id\",\"sessionId\":\"session\",\"state\":\"backendReady\"}",
    FileRuntimeTransport.Json
)!;
Check(!legacyReply.GameConnectionsV1 && !legacyReply.GameConnectionsVerified, "Older helper defaults feature fields safely false");

var fake = new FakeRuntime();
var runtime = new OfflineRuntime(fake);
await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
await Reject(() => runtime.LaunchAsync(profile, directory, default, requireGameConnections: true), "Update the offline runtime");
Check(fake.Calls.Count == 1 && !runtime.RecoveryRequired, "Unsupported protection rejected before launch and before recovery ownership");

fake = new FakeRuntime { Capability = true, Verified = false };
runtime = new(fake);
await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
await Reject(() => runtime.LaunchAsync(profile, directory, default, requireGameConnections: true), "did not verify");
Check(
    runtime.RecoveryRequired && fake.Calls[^1].RequireGameConnections,
    "Unverified direct running reply preserves ownership and records requirement"
);
Check(
    fake.Calls[^1].ProfileId == profile && fake.Calls[^1].GameDirectory == directory,
    "Protected launch preserves selected profile and exact game directory"
);

foreach (var launchState in new[] { "waiting", "starting" })
{
    fake = new FakeRuntime
    {
        Capability = true,
        Verified = false,
        LaunchState = launchState,
    };
    runtime = new(fake);
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await Reject(() => runtime.LaunchAsync(profile, directory, default, requireGameConnections: true), "did not verify");
    Check(runtime.RecoveryRequired && fake.Calls[^1].Operation == "observe", launchState + " unverified observe retains recovery");
}

fake = new FakeRuntime
{
    Capability = true,
    Verified = true,
    LaunchState = "starting",
};
runtime = new(fake);
await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
var running = await runtime.LaunchAsync(profile, directory, default, requireGameConnections: true);
Check(
    running.State == "running" && running.GameConnectionsVerified && runtime.RecoveryRequired,
    "Verified observed launch accepted with owned game"
);

fake = new FakeRuntime();
runtime = new(fake);
await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
running = await runtime.LaunchAsync(profile, directory, default);
Check(running.State == "running" && !fake.Calls[^1].RequireGameConnections, "Old helper continues to launch with protection off");

fake = new FakeRuntime { Capability = true };
runtime = new(fake);
await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
fake.Capability = false;
await runtime.PreflightAsync(directory, default);
await Reject(() => runtime.LaunchAsync(profile, directory, default, requireGameConnections: true), "Update the offline runtime");
Check(
    fake.Calls.All(x => x.Operation != "launch") && !runtime.RecoveryRequired,
    "Latest capability downgrade blocks launch before mutation"
);
Console.WriteLine($"{checks} runtime connection capability checks passed; no process or network started.");

sealed class FakeRuntime : IRuntimeTransport
{
    public bool Capability,
        Verified;
    public string LaunchState = "running";
    public List<RuntimeRequest> Calls = [];

    public Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        var state =
            request.Operation == "launch" ? LaunchState
            : request.Operation == "observe" ? "running"
            : "backendReady";
        return Task.FromResult(
            new RuntimeReply(
                1,
                request.RequestId,
                "owned-session",
                state,
                BackendUrl: OfflineRuntime.BackendUrl,
                GamePid: state == "running" ? 12345 : null,
                GameCreatedUtc: state == "running" ? "2026-10-18T01:00:00Z" : null,
                GameConnectionsV1: Capability,
                GameConnectionsVerified: Verified
            )
        );
    }
}
