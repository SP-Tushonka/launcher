using System.Text.Json;
using SPTarkov.Core.Runtime;

if (args.Length == 4 && args[0] == "--request" && args[2] == "--response")
{
    using var wire = JsonDocument.Parse(File.ReadAllText(args[1]));
    var request = JsonSerializer.Deserialize<RuntimeRequest>(wire.RootElement.GetRawText(), FileRuntimeTransport.Json)!;
    if (request.Operation is "readEnvironment" or "applyEnvironment")
    {
        var expected = new[] { "protocol", "requestId", "operation", "sessionId" }.Concat(
            request.Operation == "applyEnvironment" ? new[] { "settings", "expectedRevision" } : Array.Empty<string>()
        );
        if (!wire.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).SequenceEqual(expected.OrderBy(n => n)))
            throw new Exception("Unexpected environment wire fields");
    }
    var log = Environment.GetEnvironmentVariable("ENVIRONMENT_FIXTURE_LOG");
    if (log is not null)
        File.AppendAllText(log, JsonSerializer.Serialize(request, FileRuntimeTransport.Json) + "\n");
    var reply = await new Fake().SendAsync(request, default);
    var responseJson = JsonSerializer.Serialize(reply, FileRuntimeTransport.Json);
    if (Environment.GetEnvironmentVariable("ENVIRONMENT_FIXTURE_NUMERIC_STRING") == "1")
        responseJson = responseJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"");
    File.WriteAllText(args[3], responseJson);
    return;
}

const string Profile = "0123456789abcdef01234567";
var directory = Path.GetTempPath();
int checks = 0;
void Assert(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (Exception ex) when (ex is not FixtureFailure)
    {
        Assert(true, name);
        return;
    }
    throw new FixtureFailure(name + ": accepted unexpectedly");
}
EnvironmentSelection Choice() => new("christmas", new(2099, 12, 31), new(2099, 12, 31), null);
async Task<(OfflineRuntime, Fake)> Connected(bool read = true)
{
    var f = new Fake();
    var c = new OfflineRuntime(f);
    await c.EnsureBackendAsync("127.0.0.1:6969", default);
    if (read)
        await c.ReadEnvironmentAsync(default);
    return (c, f);
}
var (client, transport) = await Connected(false);
var before = transport.Calls.Count;
await Reject(() => client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default), "apply requires initial read");
Assert(transport.Calls.Count == before, "unread apply sends no IPC");
transport.ReadFault = "lost";
await Reject(() => client.ReadEnvironmentAsync(default), "lost initial read is rejected");
Assert(
    client.EnvironmentChangeUncertain && !client.RecoveryRequired,
    "lost initial read requires settings reconciliation without game recovery"
);
transport.ReadFault = null;
var initial = await client.ReadEnvironmentAsync(default);
Assert(
    initial.SchemaVersion == 1 && initial.Revision == Fake.Revision1 && initial.ServerTimeZone == "UTC",
    "read preserves authoritative schema/revision/timezone"
);
Assert(
    initial.WeatherSeasons.Select(x => x.Value).SequenceEqual(Enumerable.Range(0, 7)) && initial.HolidayEvents.Any(x => x.Id == "none"),
    "catalog retains seven seasons and distinct No event"
);
var result = await client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default);
Assert(result.Revision == Fake.Revision2 && !client.EnvironmentChangeUncertain, "verified apply adopts new revision");
var mutation = transport.Calls.Last();
Assert(
    mutation.Settings == Choice() && mutation.ExpectedRevision == Fake.Revision1 && mutation.SessionId == Fake.Session,
    "apply forwards exact settings/CAS revision/same session"
);
using (var json = JsonDocument.Parse(JsonSerializer.Serialize(mutation, FileRuntimeTransport.Json)))
{
    Assert(
        json.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .SequenceEqual(new[] { "protocol", "requestId", "operation", "sessionId", "settings", "expectedRevision" }.OrderBy(n => n)),
        "environment Apply has only the frozen top-level wire fields"
    );
    var s = json.RootElement.GetProperty("settings");
    Assert(
        s.GetProperty("holidayEndDate").GetString() == "2099-12-31" && s.GetProperty("progressionEndDate").GetString() == "2099-12-31",
        "wire dates are ISO date-only"
    );
    Assert(s.GetProperty("weatherOverrideSeason").ValueKind == JsonValueKind.Null, "wire automatic weather is null");
}
var none = mutation with { Settings = new("none", null, new(2099, 12, 31), 0) };
using (var json = JsonDocument.Parse(JsonSerializer.Serialize(none, FileRuntimeTransport.Json)))
{
    var s = json.RootElement.GetProperty("settings");
    Assert(
        s.GetProperty("holidayEventId").GetString() == "none"
            && s.GetProperty("holidayEndDate").ValueKind == JsonValueKind.Null
            && s.GetProperty("weatherOverrideSeason").GetInt32() == 0,
        "No event/date absence/weather zero remain distinct"
    );
}
foreach (
    var (selection, revision, name) in new[]
    {
        (Choice() with { HolidayEventId = "unknown" }, Fake.Revision1, "unknown holiday"),
        (Choice() with { HolidayEndDate = null }, Fake.Revision1, "holiday missing date"),
        (Choice() with { HolidayEventId = "none" }, Fake.Revision1, "No event with date"),
        (Choice() with { HolidayEndDate = new(2000, 1, 1) }, Fake.Revision1, "past holiday date"),
        (Choice() with { ProgressionEndDate = new(2000, 1, 1) }, Fake.Revision1, "changed past progression date"),
        (Choice() with { WeatherOverrideSeason = 7 }, Fake.Revision1, "unknown weather"),
        (Choice(), "old", "stale revision"),
    }
)
{
    (client, transport) = await Connected();
    before = transport.Calls.Count;
    await Reject(() => client.ApplyEnvironmentAsync(selection, revision, default), name + " rejected");
    Assert(transport.Calls.Count == before && !client.EnvironmentChangeUncertain, name + " rejected before IPC without uncertainty");
}
(client, transport) = await Connected();
await client.LaunchAsync(Profile, directory, default);
before = transport.Calls.Count;
await Reject(() => client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default), "active game blocks settings restart");
Assert(transport.Calls.Count == before, "active game refusal sends no IPC");
foreach (
    var fault in new[]
    {
        "lost",
        "wrong-request",
        "wrong-session",
        "missing-settings",
        "invalid-schema",
        "foreign-backend",
        "short-revision",
        "uppercase-revision",
        "nonhex-revision",
        "unchanged-selection",
        "different-selection",
        "missing-timezone",
        "invalid-timezone",
        "missing-clock",
        "invalid-clock",
    }
)
{
    (client, transport) = await Connected();
    transport.Fault = fault;
    await Reject(() => client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default), fault + " acknowledgement rejected");
    Assert(client.EnvironmentChangeUncertain, fault + " keeps mutation uncertain");
    before = transport.Calls.Count;
    await Reject(() => client.LaunchAsync(Profile, directory, default), fault + " blocks Play");
    await Reject(() => client.ReleaseAsync(default), fault + " blocks release");
    await Reject(() => client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default), fault + " blocks blind retry");
    Assert(transport.Calls.Count == before, fault + " blocked operations send no IPC");
    transport.Fault = null;
    transport.ReadMalformed = true;
    await Reject(() => client.ReadEnvironmentAsync(default), fault + " malformed read cannot reconcile");
    Assert(client.EnvironmentChangeUncertain, fault + " malformed read retains uncertainty");
    transport.ReadMalformed = false;
    result = await client.ReadEnvironmentAsync(default);
    Assert(
        !client.EnvironmentChangeUncertain && result.Revision == Fake.Revision2,
        fault + " authoritative read reconciles changed settings"
    );
    Assert(transport.Calls.Count(x => x.Operation == "applyEnvironment") == 1, fault + " recovery never reapplies");
}
(client, transport) = await Connected();
foreach (
    var fault in new[]
    {
        "lost",
        "wrong-request",
        "wrong-session",
        "missing-settings",
        "invalid-schema",
        "short-revision",
        "uppercase-revision",
        "nonhex-revision",
        "running",
        "cleaned",
        "foreign-backend",
    }
)
{
    transport.ReadFault = fault;
    before = transport.Calls.Count;
    await Reject(() => client.ReadEnvironmentAsync(default), fault + " read after valid snapshot rejected");
    Assert(
        client.EnvironmentChangeUncertain && !client.RecoveryRequired,
        fault + " read blocks settings ownership without inventing game identity"
    );
    var afterRead = transport.Calls.Count;
    await Reject(() => client.LaunchAsync(Profile, directory, default), fault + " read blocks Play");
    await Reject(() => client.ReleaseAsync(default), fault + " read blocks release");
    Assert(transport.Calls.Count == afterRead && afterRead == before + 1, fault + " read refusal sends no follow-up mutation");
    transport.ReadFault = null;
    await client.ReadEnvironmentAsync(default);
    Assert(
        !client.EnvironmentChangeUncertain && !client.RecoveryRequired,
        fault + " read reconciles only through valid same-session snapshot"
    );
}
(client, transport) = await Connected();
transport.Fault = "known-conflict";
try
{
    await client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default);
    throw new FixtureFailure("known conflict accepted");
}
catch (EnvironmentSettingsException ex)
{
    Assert(
        ex.CurrentSettings.Revision == Fake.Revision2 && !client.EnvironmentChangeUncertain,
        "known refusal returns current settings and clears uncertainty"
    );
}
await client.PreflightAsync(directory, default);
Assert(
    transport.Calls.Last().Operation == "preflight" && transport.Calls.Count(x => x.Operation == "applyEnvironment") == 1,
    "known refusal preserves ready backend without retry"
);
(client, transport) = await Connected(false);
transport.ExpireProgression();
initial = await client.ReadEnvironmentAsync(default);
result = await client.ApplyEnvironmentAsync(initial.Selection with { WeatherOverrideSeason = 2 }, initial.Revision, default);
Assert(
    result.ProgressionEndDate == new DateOnly(2000, 1, 1) && result.WeatherOverrideSeason == 2,
    "weather-only apply preserves an unchanged expired progression deadline"
);
(client, transport) = await Connected();
transport.Fault = "lost";
transport.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
transport.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
var applying = client.ApplyEnvironmentAsync(Choice(), Fake.Revision1, default);
await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
var queuedPlay = client.LaunchAsync(Profile, directory, default);
Assert(!queuedPlay.IsCompleted && transport.Calls.All(x => x.Operation != "launch"), "Play waits behind transaction gate");
transport.Continue.SetResult();
await Reject(() => applying, "blocked apply reports lost reply");
await Reject(() => queuedPlay, "queued Play rechecks uncertainty after gate handoff");
Assert(transport.Calls.All(x => x.Operation != "launch"), "uncertain restart cannot launch on gate handoff");
var deadlineSnapshot = Fake.Snapshot(Fake.Revision1, new("none", null, new(2099, 12, 31), null));
var pacificDeadline = deadlineSnapshot.Selection with { ProgressionEndDate = new(2030, 1, 1) };
pacificDeadline.Validate(deadlineSnapshot, new DateTimeOffset(2030, 1, 1, 8, 59, 0, TimeSpan.Zero));
Assert(true, "changed progression deadline remains valid before Pacific 01:00 cutoff");
await Reject(
    () =>
    {
        pacificDeadline.Validate(deadlineSnapshot, new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero));
        return Task.CompletedTask;
    },
    "changed progression deadline is expired at Pacific 01:00 even while UTC date is unchanged"
);
var holidayDeadline = deadlineSnapshot.Selection with { HolidayEventId = "christmas", HolidayEndDate = new(2030, 1, 1) };
holidayDeadline.Validate(deadlineSnapshot, new DateTimeOffset(2030, 1, 1, 23, 58, 0, TimeSpan.Zero));
Assert(true, "holiday deadline uses server UTC 23:59 cutoff");
holidayDeadline.Validate(deadlineSnapshot, new DateTimeOffset(2030, 1, 1, 23, 59, 0, TimeSpan.Zero));
Assert(true, "holiday deadline includes the exact server 23:59:00 cutoff");
await Reject(
    () =>
    {
        holidayDeadline.Validate(deadlineSnapshot, new DateTimeOffset(2030, 1, 1, 23, 59, 1, TimeSpan.Zero));
        return Task.CompletedTask;
    },
    "holiday deadline expires after the inclusive server 23:59 cutoff"
);

var ipcRoot = Path.Combine(Path.GetTempPath(), "environment-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ipcRoot);
var oldLog = Environment.GetEnvironmentVariable("ENVIRONMENT_FIXTURE_LOG");
try
{
    var log = Path.Combine(ipcRoot, "calls.jsonl");
    Environment.SetEnvironmentVariable("ENVIRONMENT_FIXTURE_LOG", log);
    var ipc = new OfflineRuntime(new FileRuntimeTransport(Environment.ProcessPath!));
    await ipc.EnsureBackendAsync("127.0.0.1:6969", default);
    var read = await ipc.ReadEnvironmentAsync(default);
    var saved = await ipc.ApplyEnvironmentAsync(Choice(), read.Revision, default);
    Assert(
        saved.Selection == Choice() && saved.Revision == Fake.Revision2 && !ipc.EnvironmentChangeUncertain,
        "actual child/file IPC roundtrips settings and acknowledges apply"
    );
    Assert(
        saved.ProgressionTimeZone == "America/Los_Angeles" && saved.HolidayEndTime == "23:59:00" && saved.ProgressionEndTime == "01:00:00",
        "actual file IPC retains required timezone/cutoff metadata"
    );
    Environment.SetEnvironmentVariable("ENVIRONMENT_FIXTURE_NUMERIC_STRING", "1");
    try
    {
        await Reject(() => ipc.ReadEnvironmentAsync(default), "actual IPC rejects numeric-string schema instead of coercing it");
    }
    finally
    {
        Environment.SetEnvironmentVariable("ENVIRONMENT_FIXTURE_NUMERIC_STRING", null);
    }
    Assert(ipc.EnvironmentChangeUncertain, "malformed IPC numeric types retain uncertain readiness");
    await ipc.ReadEnvironmentAsync(default);
    var requests = File.ReadAllLines(log).Select(x => JsonSerializer.Deserialize<RuntimeRequest>(x, FileRuntimeTransport.Json)!).ToArray();
    Assert(
        requests
            .Select(x => x.Operation)
            .SequenceEqual(new[] { "ensureBackend", "readEnvironment", "applyEnvironment", "readEnvironment", "readEnvironment" })
            && requests.Skip(1).All(x => x.SessionId == Fake.Session),
        "actual subprocess requests retain one session without release/restart"
    );
    var applied = requests.Single(x => x.Operation == "applyEnvironment");
    Assert(
        applied.Settings == Choice() && applied.Settings!.WeatherOverrideSeason is null && applied.ExpectedRevision == Fake.Revision1,
        "actual file IPC preserves ISO dates, null automatic weather and expected revision"
    );
}
finally
{
    Environment.SetEnvironmentVariable("ENVIRONMENT_FIXTURE_LOG", oldLog);
    Directory.Delete(ipcRoot, true);
}
Console.WriteLine($"{checks} environment client assertions passed; no server or game started.");

sealed class FixtureFailure(string message) : Exception(message);

sealed class Fake : IRuntimeTransport
{
    public static readonly string Revision1 = new('a', 64),
        Revision2 = new('b', 64);
    public const string Session = "fixture-owned-session";
    public List<RuntimeRequest> Calls { get; } = [];
    public string? Fault { get; set; }
    public bool ReadMalformed { get; set; }
    public string? ReadFault { get; set; }
    public TaskCompletionSource? Started { get; set; }
    public TaskCompletionSource? Continue { get; set; }
    private EnvironmentSnapshot current = Snapshot(Fake.Revision1, new("none", null, new(2099, 12, 31), null));

    public void ExpireProgression() => current = current with { ProgressionEndDate = new(2000, 1, 1) };

    public static EnvironmentSnapshot Snapshot(string revision, EnvironmentSelection s) =>
        new(
            1,
            revision,
            "UTC",
            new[] { new HolidayEventOption("none", "No event"), new HolidayEventOption("christmas", "Christmas") },
            Enumerable.Range(0, 7).Select(x => new WeatherSeasonOption(x, "Season " + x)).ToArray(),
            s.HolidayEventId,
            s.HolidayEndDate,
            s.ProgressionEndDate,
            s.WeatherOverrideSeason,
            "America/Los_Angeles",
            "23:59:00",
            "01:00:00"
        );

    public async Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken token)
    {
        Calls.Add(request);
        var reply = new RuntimeReply(1, request.RequestId, Session, "backendReady", BackendUrl: OfflineRuntime.BackendUrl);
        switch (request.Operation)
        {
            case "readEnvironment":
                return ReadFault switch
                {
                    "lost" => throw new IOException("Fixture lost read response"),
                    "wrong-request" => reply with { RequestId = "wrong", Settings = current },
                    "wrong-session" => reply with { SessionId = "replacement", Settings = current },
                    "missing-settings" => reply,
                    "invalid-schema" => reply with { Settings = current with { SchemaVersion = 99 } },
                    "short-revision" => reply with { Settings = current with { Revision = "abc" } },
                    "uppercase-revision" => reply with { Settings = current with { Revision = new string('A', 64) } },
                    "nonhex-revision" => reply with { Settings = current with { Revision = new string('z', 64) } },
                    "running" => reply with { State = "running", Settings = current },
                    "cleaned" => reply with { State = "cleaned", CleanupVerified = true, Settings = current },
                    "foreign-backend" => reply with { BackendUrl = "https://example.invalid", Settings = current },
                    _ => reply with { Settings = ReadMalformed ? current with { SchemaVersion = 99 } : current },
                };
            case "applyEnvironment":
                Started?.TrySetResult();
                if (Continue is not null)
                    await Continue.Task.WaitAsync(token);
                var previous = current;
                current = Snapshot(Fake.Revision2, request.Settings!);
                return Fault switch
                {
                    "lost" => throw new IOException("Fixture lost response after durable mutation"),
                    "wrong-request" => reply with { RequestId = "wrong", Settings = current },
                    "wrong-session" => reply with { SessionId = "replacement", Settings = current },
                    "missing-settings" => reply,
                    "invalid-schema" => reply with { Settings = current with { SchemaVersion = 99 } },
                    "foreign-backend" => reply with { BackendUrl = "https://example.invalid", Settings = current },
                    "known-conflict" => reply with { Error = "Revision changed", Settings = current },
                    "short-revision" => reply with { Settings = current with { Revision = "abc" } },
                    "uppercase-revision" => reply with { Settings = current with { Revision = new string('A', 64) } },
                    "nonhex-revision" => reply with { Settings = current with { Revision = new string('z', 64) } },
                    "unchanged-selection" => reply with { Settings = previous with { Revision = Fake.Revision2 } },
                    "different-selection" => reply with { Settings = current with { WeatherOverrideSeason = 3 } },
                    "missing-timezone" => reply with { Settings = current with { ProgressionTimeZone = null! } },
                    "invalid-timezone" => reply with { Settings = current with { ProgressionTimeZone = "invalid/fixture-timezone" } },
                    "missing-clock" => reply with { Settings = current with { HolidayEndTime = null! } },
                    "invalid-clock" => reply with { Settings = current with { ProgressionEndTime = "24:01:00" } },
                    _ => reply with { Settings = current },
                };
            case "launch":
                return reply with { State = "running", GamePid = 12345, GameCreatedUtc = "2026-10-18T00:00:00Z" };
            case "release":
                return reply with { State = "cleaned", CleanupVerified = true };
            default:
                return reply;
        }
    }
}
