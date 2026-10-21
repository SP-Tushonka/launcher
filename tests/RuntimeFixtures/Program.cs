using System.Text.Json;
using SPTarkov.Core.Runtime;

if (args.Length == 4 && args[0] == "--request")
{
    var request = JsonSerializer.Deserialize<RuntimeRequest>(File.ReadAllText(args[1]), FileRuntimeTransport.Json)!;
    var root = Environment.GetEnvironmentVariable("FIXTURE_RUNTIME_ROOT")!;
    var mode = Environment.GetEnvironmentVariable("FIXTURE_RUNTIME_MODE") ?? "normal";
    File.AppendAllText(Path.Combine(root, "calls.jsonl"), JsonSerializer.Serialize(request, FileRuntimeTransport.Json) + "\n");
    var countPath = Path.Combine(root, "count");
    int count = File.Exists(countPath) ? int.Parse(File.ReadAllText(countPath)) : 0;
    if (request.Operation == "launch")
    {
        count = 0;
        File.WriteAllText(countPath, "0");
    }
    var state = request.Operation switch
    {
        "ensureBackend" => "backendReady",
        "preflight" => "backendReady",
        "launch" => "waiting",
        "observe" => count == 0 ? "running" : "backendReady",
        "release" => "cleaned",
        "releaseWhenIdle" => "running",
        "stop" => "stopping",
        _ => "failed",
    };
    if (request.Operation == "observe")
        File.WriteAllText(countPath, (count + 1).ToString());
    if (mode == "backend-error" || (mode == "start-error" && request.Operation == "launch"))
        state = "failed";
    if (mode == "stop-ready" && request.Operation == "stop")
        state = "backendReady";
    if (mode == "stop-poll" && request.Operation == "stop")
        File.WriteAllText(countPath, "0");
    if (mode == "stop-poll" && request.Operation == "observe")
        state = count == 0 ? "stopping" : "backendReady";
    if (mode == "stop-error" && request.Operation == "stop")
        state = "failed";
    if (mode == "renew-ready" && request.Operation == "observe")
        state = "backendReady";
    if (mode == "renew-cleaned" && request.Operation == "observe")
        state = "cleaned";
    if (mode == "slow-launch" && request.Operation == "launch")
        Thread.Sleep(3000);
    if (mode == "changed-pid" && request.Operation == "observe" && count > 0)
        state = "running";
    if (mode == "false-start" && request.Operation == "launch")
        state = "cleaned";
    var reply = new RuntimeReply(
        mode == "wrong-protocol" ? 2 : 1,
        mode == "wrong-id" ? "wrong" : request.RequestId,
        mode == "stop-wrong-session" && request.Operation == "stop" ? "replacement-session" : "fixture-owned-session",
        state,
        mode == "foreign-backend" ? "https://example.invalid" : OfflineRuntime.BackendUrl,
        mode == "no-pid" ? null
            : mode == "changed-pid" && count > 0 ? 99999
            : 12345,
        "2026-10-17T03:00:00Z",
        state == "cleaned" && mode != "unclean",
        state == "failed" ? "fixture backend refused" : null,
        mode != "unclean" && state == "backendReady" && (request.Operation == "observe" || request.Operation == "stop")
    );
    File.WriteAllText(args[3], JsonSerializer.Serialize(reply, FileRuntimeTransport.Json));
    return;
}
var temporary = Path.Combine(Path.GetTempPath(), "launcher-runtime-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_ROOT", temporary);
var executable = Environment.ProcessPath!;
int checks = 0;
void Assert(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
async Task Fails(Func<Task> action, string contains, string name)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException ex)
    {
        Assert(ex.Message.Contains(contains), name);
        return;
    }
    throw new Exception(name + ": accepted invalid runtime response");
}
OfflineRuntime Client(string mode)
{
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", mode);
    File.Delete(Path.Combine(temporary, "count"));
    return new(new FileRuntimeTransport(executable));
}
try
{
    var client = Client("normal");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await client.PreflightAsync(temporary, default);
    var started = await client.LaunchAsync("0123456789abcdef01234567", temporary, default);
    Assert(started.State == "running", "queue acceptance is followed by owned running acknowledgement");
    await client.ReleaseIfIdleAsync(default);
    var midCalls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"));
    Assert(!midCalls.Any(x => x.Contains("\"operation\":\"release\"")), "close-on-start keeps owned game and backend alive");
    await client.MonitorAsync(default);
    var refresh = await new FileRuntimeTransport(executable).SendAsync(
        new RuntimeRequest(1, Guid.NewGuid().ToString("N"), "ensureBackend", started.SessionId),
        default
    );
    Assert(
        refresh.SessionId == started.SessionId && refresh.State == "backendReady",
        "profile refresh retains the same ready backend session"
    );
    await client.LaunchAsync("fedcba9876543210fedcba98", temporary, default);
    await client.MonitorAsync(default);
    await client.ReleaseIfIdleAsync(default);
    var calls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))
        .Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!)
        .ToArray();
    Assert(calls.First(x => x.Operation == "launch").ProfileId == "0123456789abcdef01234567", "selected local profile forwarded unchanged");
    Assert(calls[0].Operation == "ensureBackend" && calls[1].Operation == "preflight", "backend ownership precedes game preparation");
    Assert(calls.Where(x => x.Operation == "launch").Select(x => x.SessionId).Distinct().Count() == 1, "second Play reuses backend lease");
    Assert(
        calls.Count(x => x.Operation == "release") == 1 && calls[^1].Operation == "release",
        "only explicit idle shutdown releases full session"
    );
    await Fails(
        () =>
            new OfflineRuntime(new FileRuntimeTransport(Path.Combine(temporary, "missing.exe"))).EnsureBackendAsync(
                "127.0.0.1:6969",
                default
            ),
        "Install the matching",
        "missing binary gives actionable error"
    );
    await Fails(
        () => Client("wrong-id").EnsureBackendAsync("127.0.0.1:6969", default),
        "identity mismatch",
        "reply request correlation enforced"
    );
    await Fails(
        () => Client("backend-error").EnsureBackendAsync("127.0.0.1:6969", default),
        "backend refused",
        "backend failure propagates"
    );
    await Fails(
        () => Client("normal").EnsureBackendAsync("http://example.com", default),
        "HTTPS server",
        "non-HTTPS backend rejected before invocation"
    );
    client = Client("false-start");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await Fails(
        () => client.LaunchAsync("0123456789abcdef01234567", temporary, default),
        "could not finish starting",
        "cleaned response is not a game start"
    );
    client = Client("no-pid");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await Fails(
        () => client.LaunchAsync("0123456789abcdef01234567", temporary, default),
        "could not finish starting",
        "running without process identity rejected"
    );
    client = Client("unclean");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await client.LaunchAsync("0123456789abcdef01234567", temporary, default);
    await Fails(() => client.MonitorAsync(default), "game cleanup is unverified", "uncertain cleanup never releases lease");
    var releasesBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Count(x => x.Contains("\"operation\":\"release\""));
    await client.ReleaseIfIdleAsync(default);
    Assert(
        File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Count(x => x.Contains("\"operation\":\"release\"")) == releasesBefore,
        "window close after uncertain game cleanup does not release ownership"
    );
    var countBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await Fails(
        () => client.PreflightAsync(temporary, default),
        "cleanup is still pending",
        "uncertain cleanup blocks preflight before any preparation"
    );
    await Fails(
        () => client.LaunchAsync("0123456789abcdef01234567", temporary, default),
        "cleanup is still pending",
        "uncertain cleanup blocks another launch"
    );
    await Fails(() => client.ReleaseAsync(default), "cleanup is still pending", "explicit disconnect cannot release uncertain game");
    Assert(File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length == countBefore, "blocked operations send no helper commands");
    await Fails(() => client.StopAsync(default), "game cleanup is unverified", "stop polling refuses unverified cleanup");
    Assert(client.RecoveryRequired, "unverified stop keeps recovery required");
    await Fails(
        () => Client("wrong-protocol").EnsureBackendAsync("127.0.0.1:6969", default),
        "identity mismatch",
        "protocol mismatch refused"
    );
    await Fails(
        () => Client("foreign-backend").EnsureBackendAsync("127.0.0.1:6969", default),
        "endpoint changed",
        "helper cannot substitute backend"
    );
    client = Client("start-error");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await Fails(() => client.LaunchAsync("0123456789abcdef01234567", temporary, default), "backend refused", "launch failure propagates");
    await Fails(
        () => client.PreflightAsync(temporary, default),
        "cleanup is still pending",
        "failed launch requires verified recovery before retry"
    );
    client = Client("changed-pid");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await client.LaunchAsync("0123456789abcdef01234567", temporary, default);
    await Fails(() => client.MonitorAsync(default), "process identity changed", "monitor rejects replacement process");
    client = Client("slow-launch");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = client.LaunchAsync("0123456789abcdef01234567", temporary, cancellation.Token);
        await Task.Delay(150);
        var before = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
        var exiting = client.ReleaseIfIdleAsync(default);
        await Task.Delay(50);
        Assert(
            !exiting.IsCompleted && !pending.IsCompleted && File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length == before,
            "window exit waits for the pending launch instead of skipping release scheduling"
        );
        cancellation.Cancel();
        try
        {
            await pending;
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            Assert(true, "cancelled CLI launch returns control");
        }
        await exiting;
        await client.RenewIdleLeaseAsync(default);
    }
    await Fails(
        () => client.LaunchAsync("0123456789abcdef01234567", temporary, default),
        "cleanup is still pending",
        "cancelled launch cannot be retried blindly"
    );
    Assert(client.RecoveryRequired, "lost launch reply exposes recovery state without PID acknowledgement");
    var pendingRenewBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await client.RenewIdleLeaseAsync(default);
    Assert(
        File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length == pendingRenewBefore,
        "idle renewal skips pending game cleanup"
    );
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "stop-error");
    await Fails(() => client.StopAsync(default), "backend refused", "failed stop retains pending game");
    Assert(client.RecoveryRequired, "stop failure keeps profile actions blocked");
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "stop-wrong-session");
    await Fails(() => client.StopAsync(default), "session identity mismatch", "stop refuses replacement session");
    Assert(client.RecoveryRequired, "replacement session cannot clear recovery state");
    var recoveryCallsBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "stop-poll");
    var stopped = await client.StopAsync(default);
    Assert(
        stopped.State == "backendReady" && stopped.GameCleanupVerified,
        "stop can acknowledge verified recovery after a lost launch reply"
    );
    Assert(!client.RecoveryRequired, "verified game cleanup clears recovery state");
    var recoveryCalls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))
        .Skip(recoveryCallsBefore)
        .Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!)
        .ToArray();
    Assert(
        recoveryCalls.Select(x => x.Operation).SequenceEqual(new[] { "stop", "observe", "observe" }),
        "recovery polls stopping without a process acknowledgement"
    );
    Assert(
        recoveryCalls.All(x => x.SessionId == "fixture-owned-session"),
        "recovery retains original backend session without release or restart"
    );
    await client.PreflightAsync(temporary, default);
    await client.ReleaseAsync(default);
    client = Client("normal");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    await client.ReleaseAsync(default);
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    var last = JsonSerializer.Deserialize<RuntimeRequest>(
        File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))[^1],
        FileRuntimeTransport.Json
    )!;
    Assert(last.Operation == "ensureBackend" && last.SessionId is null, "explicit idle disconnect clears lease before reconnect");
    client = Client("renew-ready");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    var renewBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await client.RenewIdleLeaseAsync(default);
    await client.RenewIdleLeaseAsync(default);
    var renewCalls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))
        .Skip(renewBefore)
        .Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!)
        .ToArray();
    Assert(
        renewCalls.Length == 2 && renewCalls.All(x => x.Operation == "observe" && x.SessionId == "fixture-owned-session"),
        "idle renewal only observes the same session repeatedly"
    );
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "renew-cleaned");
    await Fails(() => client.RenewIdleLeaseAsync(default), "no longer ready", "idle renewal surfaces verified session expiry");
    Assert(client.IdleLeaseLost && !client.RecoveryRequired, "idle expiry is distinguished from pending game recovery");
    renewBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await client.RenewIdleLeaseAsync(default);
    Assert(
        File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length == renewBefore,
        "expired renewal never creates a new owner or backend"
    );
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "renew-ready");
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    last = JsonSerializer.Deserialize<RuntimeRequest>(
        File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))[^1],
        FileRuntimeTransport.Json
    )!;
    Assert(last.SessionId is null && !client.IdleLeaseLost, "explicit reconnect after verified expiry starts with no stale session");
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "backend-error");
    await Fails(() => client.RenewIdleLeaseAsync(default), "backend refused", "idle renewal surfaces backend failure");
    Assert(client.IdleLeaseLost, "failed idle renewal remains visible to global UI");
    renewBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await Fails(
        () => client.EnsureBackendAsync("127.0.0.1:6969", default),
        "backend refused",
        "reconnect refuses to abandon a failed idle release"
    );
    renewCalls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))
        .Skip(renewBefore)
        .Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!)
        .ToArray();
    Assert(
        renewCalls.Length == 1
            && renewCalls[0].Operation == "release"
            && renewCalls[0].SessionId == "fixture-owned-session"
            && client.IdleLeaseLost,
        "failed reconciliation preserves old session and sends no backend start"
    );
    Environment.SetEnvironmentVariable("FIXTURE_RUNTIME_MODE", "renew-ready");
    renewBefore = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl")).Length;
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    renewCalls = File.ReadAllLines(Path.Combine(temporary, "calls.jsonl"))
        .Skip(renewBefore)
        .Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!)
        .ToArray();
    Assert(
        renewCalls.Length == 2
            && renewCalls[0].Operation == "release"
            && renewCalls[0].SessionId == "fixture-owned-session"
            && renewCalls[1].Operation == "ensureBackend"
            && renewCalls[1].SessionId is null
            && !client.IdleLeaseLost,
        "explicit reconnect verifies old lease release before requesting a fresh backend"
    );
    Console.WriteLine($"{checks} runtime IPC fixtures passed; no game or server started.");
}
finally
{
    Directory.Delete(temporary, true);
}
