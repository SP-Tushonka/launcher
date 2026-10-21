using System.Text.Json;
using SPTarkov.Core.Runtime;

var checks = 0;
void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task Fails(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new Exception("Expected refusal");
}
var directory = Path.GetFullPath("fixture-game");
const string profile = "0123456789abcdef01234567";

{
    var t = new Transport();
    var runtime = new OfflineRuntime(t);
    await runtime.EnsureBackendOnStartupAsync(true, default);
    await runtime.EnsureBackendOnStartupAsync(true, default);
    Check(t.Calls.Count == 1 && t.Calls[0].AllowServerStart == true, "startup runs once without any saved-profile requirement");
    await runtime.PreflightAsync(directory, default);
    Check(t.Calls[^1].AllowServerStart is null, "non-ensure commands omit startup policy");
    var wire = JsonSerializer.Serialize(t.Calls[^1], FileRuntimeTransport.Json);
    Check(!wire.Contains("allowServerStart"), "optional policy is absent from unrelated wire commands");
}
{
    var t = new Transport { FailEnsure = true };
    var runtime = new OfflineRuntime(t);
    await Fails(() => runtime.EnsureBackendOnStartupAsync(false, default));
    await runtime.EnsureBackendOnStartupAsync(true, default);
    Check(
        t.Calls.Count == 1 && t.Calls[0].AllowServerStart == false,
        "failed startup does not silently retry or enable automatic spawning"
    );
    Check(
        JsonSerializer.Serialize(t.Calls[0], FileRuntimeTransport.Json).Contains("\"allowServerStart\":false"),
        "disabled policy is explicit false rather than omitted"
    );
    t.FailEnsure = false;
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default, false);
    Check(
        t.Calls.Count == 2 && t.Calls[^1].AllowServerStart == false,
        "manual Connect remains available after startup failure with no-spawn policy"
    );
}
{
    var t = new Transport();
    var runtime = new OfflineRuntime(t);
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await runtime.PreflightAsync(directory, default);
    await runtime.LaunchAsync(profile, directory, default);
    await runtime.ReleaseIfIdleAsync(default);
    Check(t.Calls[^1].Operation == "releaseWhenIdle", "Exit during a game schedules idle release without stopping the game");
    Check(!t.Calls.Any(x => x.Operation is "stop" or "release"), "deferred Exit never sends immediate stop or release");
    Check(runtime.RecoveryRequired, "deferred shutdown acknowledgement does not fabricate game cleanup");
}
{
    var t = new Transport { HoldEnsure = true };
    var runtime = new OfflineRuntime(t);
    var connecting = runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await t.EnsureEntered.Task;
    var exiting = runtime.ReleaseIfIdleAsync(default);
    Check(!exiting.IsCompleted, "Exit waits behind an in-flight startup command instead of skipping cleanup");
    t.AllowEnsure.TrySetResult();
    await connecting;
    await exiting;
    Check(
        t.Calls.Select(x => x.Operation).SequenceEqual(new[] { "ensureBackend", "release" }),
        "Exit releases the verified idle server after startup finishes"
    );
}
{
    var t = new Transport { RefuseDeferred = true };
    var runtime = new OfflineRuntime(t);
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await runtime.LaunchAsync(profile, directory, default);
    await Fails(() => runtime.ReleaseIfIdleAsync(default));
    Check(runtime.RecoveryRequired, "refused deferred Exit retains the pending game and propagates an actionable failure");
}
{
    var t = new Transport { FailReleaseOnce = true };
    var runtime = new OfflineRuntime(t);
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await Fails(() => runtime.ReleaseIfIdleAsync(default));
    await runtime.ReleaseIfIdleAsync(default);
    Check(
        t.Calls.Select(x => x.Operation).SequenceEqual(new[] { "ensureBackend", "release", "observe", "release" }),
        "Exit retry observes the same session after a failed release before releasing again"
    );
    Check(t.Calls.Skip(1).All(x => x.SessionId == "fixture-session"), "Exit retry never replaces the failed session with another backend");
}
{
    var t = new Transport { FailEnsure = true, ObserveWaiting = true };
    var runtime = new OfflineRuntime(t);
    await Fails(() => runtime.EnsureBackendAsync("127.0.0.1:6969", default, false));
    await runtime.ReleaseIfIdleAsync(default);
    Check(
        t.Calls.Select(x => x.Operation).SequenceEqual(new[] { "ensureBackend", "observe", "release" })
            && t.Calls[0].AllowServerStart == false,
        "disabled startup with absent server can Exit through verified no-spawn release"
    );
}
Console.WriteLine($"{checks} startup/Exit checks passed; no processes or servers started.");

sealed class Transport : IRuntimeTransport
{
    public List<RuntimeRequest> Calls { get; } = new();
    public bool FailEnsure { get; set; }
    public bool HoldEnsure { get; set; }
    public bool RefuseDeferred { get; set; }
    public bool FailReleaseOnce { get; set; }
    public bool ObserveWaiting { get; set; }
    public TaskCompletionSource EnsureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowEnsure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (request.Operation == "ensureBackend" && HoldEnsure)
        {
            EnsureEntered.TrySetResult();
            await AllowEnsure.Task.WaitAsync(cancellationToken);
        }
        var failed = request.Operation == "ensureBackend" && FailEnsure || request.Operation == "releaseWhenIdle" && RefuseDeferred;
        if (request.Operation == "release" && FailReleaseOnce)
        {
            failed = true;
            FailReleaseOnce = false;
        }
        var state = failed
            ? "failed"
            : request.Operation switch
            {
                "launch" or "releaseWhenIdle" => "running",
                "release" => "cleaned",
                _ => "backendReady",
            };
        if (request.Operation == "observe" && ObserveWaiting)
            state = "waiting";
        return new RuntimeReply(
            1,
            request.RequestId,
            "fixture-session",
            state,
            OfflineRuntime.BackendUrl,
            GamePid: state == "running" ? 12345 : null,
            GameCreatedUtc: state == "running" ? "2026-10-20T17:26:23Z" : null,
            CleanupVerified: state == "cleaned",
            Error: failed ? "fixture refusal" : null,
            GameCleanupVerified: state == "backendReady",
            GameConnectionsV1: true
        );
    }
}
