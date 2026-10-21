using SPTarkov.Core.Runtime;

const string Profile = "0123456789abcdef01234567";
var directory = Path.GetFullPath("fixture-game-directory");
var checks = 0;
void Check(bool value, string description)
{
    if (!value)
        throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
async Task<Exception> Failure(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception error)
    {
        return error;
    }
    throw new Exception("Expected a failure");
}
async Task Connect(OfflineRuntime runtime)
{
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await runtime.PreflightAsync(directory, default);
}

// A launch may have created a game despite its failed reply. Stop must verify
// cleanup before another preflight; it must keep the connected server session.
{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "failed", error: "original startup diagnostic");
    transport.Add("stop", "backendReady", clean: true);
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "running", pid: 12345);
    var runtime = new OfflineRuntime(transport);
    await Connect(runtime);
    var error = await Failure(() => runtime.LaunchWithRecoveryAsync(Profile, directory, default));
    Check(error.Message.Contains("original startup diagnostic"), "successful automatic recovery preserves startup failure");
    Check(!runtime.RecoveryRequired, "verified automatic cleanup clears pending game");
    await runtime.PreflightAsync(directory, default);
    var started = await runtime.LaunchWithRecoveryAsync(Profile, directory, default);
    Check(started.State == "running" && runtime.RecoveryRequired, "preflight and retry can acknowledge a new game");
    Check(
        transport
            .Calls.Select(x => x.Operation)
            .SequenceEqual(new[] { "ensureBackend", "preflight", "launch", "stop", "preflight", "launch" }),
        "recovery neither reconnects nor releases an existing attached or created backend"
    );
    Check(
        transport.Calls.Skip(1).All(x => x.SessionId == ScriptedTransport.Session),
        "recovery and retry retain the original backend session"
    );
    Check(transport.Calls.Where(x => x.Operation == "launch").All(x => x.ProfileId == Profile), "retry preserves the selected profile");
    transport.Complete();
}

{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "running"); // Missing process identity must never pass.
    transport.Add("stop", "backendReady", clean: true);
    var runtime = new OfflineRuntime(transport);
    await Connect(runtime);
    var error = await Failure(() => runtime.LaunchWithRecoveryAsync(Profile, directory, default));
    Check(error.Message == RuntimeUserMessages.StartFailed, "missing running identity has a user-facing startup error");
    Check(
        error.ToString().Contains("identity-verified") && error.ToString().Contains("GamePid="),
        "missing identity technical detail survives the friendly message"
    );
    Check(!runtime.RecoveryRequired && transport.Calls[^1].Operation == "stop", "missing identity triggers verified cleanup");
    transport.Complete();
}

{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "failed", error: "startup cause retained");
    transport.Add("stop", "failed", error: "bootstrap restore denied");
    transport.Add("stop", "backendReady", clean: true);
    var runtime = new OfflineRuntime(transport);
    await Connect(runtime);
    var error = await Failure(() => runtime.LaunchWithRecoveryAsync(Profile, directory, default));
    Check(
        error.ToString().Contains("startup cause retained") && error.ToString().Contains("bootstrap restore denied"),
        "failed automatic cleanup retains both failure causes"
    );
    Check(
        error is AggregateException || error.InnerException is AggregateException,
        "two failures have a structured aggregate for logging"
    );
    Check(
        runtime.RecoveryRequired && RuntimeUserMessages.Starting(true).Contains("Stop game and recover"),
        "failed cleanup retains the recovery action and pending latch"
    );
    var count = transport.Calls.Count;
    await Failure(() => runtime.PreflightAsync(directory, default));
    await Failure(() => runtime.ReleaseAsync(default));
    Check(transport.Calls.Count == count, "pending cleanup blocks preparation and disconnect before transport");
    await runtime.StopAsync(default);
    Check(!runtime.RecoveryRequired, "manual verified recovery remains available after automatic failure");
    transport.Complete();
}

{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "failed", error: "startup failed before confirmation");
    transport.Add("stop", "backendReady", clean: false);
    transport.Add("stop", "backendReady", clean: true);
    var runtime = new OfflineRuntime(transport);
    await Connect(runtime);
    var error = await Failure(() => runtime.LaunchWithRecoveryAsync(Profile, directory, default));
    Check(
        runtime.RecoveryRequired && error.ToString().Contains("cleanup is unverified"),
        "backend ready without cleanup proof cannot clear a failed game"
    );
    await runtime.StopAsync(default);
    Check(!runtime.RecoveryRequired, "later independent cleanup verification clears the latch");
    transport.Complete();
}

{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    transport.Add("preflight", "backendReady");
    transport.Add("launch", "failed", error: "original launch failed");
    transport.Add("stop", "backendReady", clean: true, session: "replacement-session");
    transport.Add("stop", "backendReady", clean: true);
    var runtime = new OfflineRuntime(transport);
    await Connect(runtime);
    var error = await Failure(() => runtime.LaunchWithRecoveryAsync(Profile, directory, default));
    Check(
        runtime.RecoveryRequired && error.ToString().Contains("session identity mismatch"),
        "replacement session cannot attest cleanup of the original game"
    );
    await runtime.StopAsync(default);
    Check(
        !runtime.RecoveryRequired && transport.Calls[^1].SessionId == ScriptedTransport.Session,
        "rejected replacement does not overwrite original recovery identity"
    );
    transport.Complete();
}

{
    var transport = new ScriptedTransport();
    transport.Add("ensureBackend", "backendReady");
    var runtime = new OfflineRuntime(transport);
    await runtime.EnsureBackendAsync("127.0.0.1:6969", default);
    await Failure(() => runtime.LaunchWithRecoveryAsync("invalid-profile", directory, default));
    Check(!runtime.RecoveryRequired && transport.Calls.Count == 1, "validation failure before launch does not request unrelated cleanup");
    transport.Complete();
}

Console.WriteLine($"{checks} runtime recovery checks passed; no processes or listeners started.");

sealed class ScriptedTransport : IRuntimeTransport
{
    public const string Session = "fixture-existing-backend";
    readonly Queue<Func<RuntimeRequest, RuntimeReply>> steps = new();
    public List<RuntimeRequest> Calls { get; } = new();

    public void Add(string operation, string state, bool clean = false, int? pid = null, string? error = null, string session = Session)
    {
        steps.Enqueue(request =>
        {
            if (request.Operation != operation)
                throw new Exception($"Expected {operation}, received {request.Operation}");
            return new RuntimeReply(
                1,
                request.RequestId,
                session,
                state,
                OfflineRuntime.BackendUrl,
                GamePid: pid,
                GameCreatedUtc: pid.HasValue ? "2026-10-20T17:26:23Z" : null,
                Error: error,
                GameCleanupVerified: clean,
                GameConnectionsV1: true
            );
        });
    }

    public Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (steps.Count == 0)
            throw new Exception("Unexpected runtime operation: " + request.Operation);
        return Task.FromResult(steps.Dequeue()(request));
    }

    public void Complete()
    {
        if (steps.Count != 0)
            throw new Exception("Fixture did not consume all expected runtime operations");
        if (Calls.Any(x => x.Operation == "release"))
            throw new Exception("Recovery must not release the existing backend");
    }
}
