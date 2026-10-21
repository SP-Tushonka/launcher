using System.Text.Json;
using SPTarkov.Core.Runtime;

const string FirstProfile = "0123456789abcdef01234567",
    SecondProfile = "fedcba9876543210fedcba98";
var directory = Path.GetTempPath();
int checks = 0;
void Assert(bool condition, string name)
{
    if (!condition)
        throw new FixtureFailure(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        Assert(true, name);
        return;
    }
    throw new FixtureFailure(name + ": invalid behavior accepted");
}
async Task<(OfflineRuntime, Fake)> Connected(ManagedHostCapability? capability, bool absent = false)
{
    var fake = new Fake { Capability = absent ? null : capability ?? Fake.Valid };
    var client = new OfflineRuntime(fake);
    await client.EnsureBackendAsync("127.0.0.1:6969", default);
    return (client, fake);
}
var (client, fake) = await Connected(null, absent: true);
var before = fake.Calls.Count;
await Reject(
    () => client.PreflightAsync(directory, default, enableManagedHost: true),
    "missing capability rejects opt-in before preflight IPC"
);
await Reject(
    () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
    "missing capability rejects opt-in Play before IPC"
);
Assert(fake.Calls.Count == before && !client.RecoveryRequired, "missing capability neither starts nor latches a game");
await client.PreflightAsync(directory, default);
await client.LaunchAsync(FirstProfile, directory, default);
Assert(
    fake.Calls.Where(x => x.Operation is "preflight" or "launch").All(x => x.ManagedHostRecipe is null),
    "default-off works without a managed-host capability"
);
await client.MonitorAsync(default);

foreach (
    var capability in new[]
    {
        Fake.Valid with
        {
            Id = "../host",
        },
        Fake.Valid with
        {
            Id = "",
        },
        Fake.Valid with
        {
            ManifestSha256 = "abc",
        },
        Fake.Valid with
        {
            ManifestSha256 = new string('A', 64),
        },
        Fake.Valid with
        {
            ManifestSha256 = new string('z', 64),
        },
        Fake.Valid with
        {
            DisplayName = " ",
        },
        Fake.Valid with
        {
            DisplayName = new string('x', 121),
        },
    }
)
{
    var malformed = new Fake { Capability = capability };
    var invalidClient = new OfflineRuntime(malformed);
    await Reject(
        () => invalidClient.EnsureBackendAsync("127.0.0.1:6969", default),
        "malformed capability is refused: " + capability.Id + "/" + capability.DisplayName.Length
    );
    Assert(
        malformed.Calls.Count == 1 && malformed.Calls[0].Operation == "ensureBackend",
        "malformed capability never reaches preflight or launch"
    );
}

(client, fake) = await Connected(null);
await client.PreflightAsync(directory, default, enableManagedHost: true);
await client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true);
var launches = fake.Calls.Where(x => x.Operation == "launch").ToArray();
var selected = new ManagedHostSelection(Fake.Valid.Id, Fake.Valid.ManifestSha256);
Assert(
    launches.Single().ProfileId == FirstProfile && launches.Single().ManagedHostRecipe == selected,
    "opt-in forwards actual selected account and exact approved recipe"
);
Assert(fake.Calls.Single(x => x.Operation == "preflight").ManagedHostRecipe == selected, "preflight carries the same recipe as launch");
using (var json = JsonDocument.Parse(JsonSerializer.Serialize(launches[0], FileRuntimeTransport.Json)))
{
    var recipe = json.RootElement.GetProperty("managedHostRecipe");
    Assert(
        recipe.GetProperty("id").GetString() == Fake.Valid.Id
            && recipe.GetProperty("manifestSha256").GetString() == Fake.Valid.ManifestSha256
            && !recipe.TryGetProperty("displayName", out _),
        "wire selection contains immutable recipe identity and hash without UI label"
    );
}
await client.MonitorAsync(default);
Assert(!client.RecoveryRequired, "verified game cleanup returns to retained backend");
await client.PreflightAsync(directory, default, enableManagedHost: false);
await client.LaunchAsync(SecondProfile, directory, default, enableManagedHost: false);
launches = fake.Calls.Where(x => x.Operation == "launch").ToArray();
Assert(
    launches[1].ProfileId == SecondProfile && launches[1].ManagedHostRecipe is null,
    "second Play explicitly OFF retains second selected account without stale recipe"
);
using (var json = JsonDocument.Parse(JsonSerializer.Serialize(launches[1], FileRuntimeTransport.Json)))
    Assert(!json.RootElement.TryGetProperty("managedHostRecipe", out _), "OFF request omits the optional recipe field entirely");
Assert(
    fake.Calls.Count(x => x.Operation == "ensureBackend") == 1
        && fake.Calls.All(x => x.Operation != "release")
        && launches.All(x => x.SessionId == Fake.Session),
    "ON then OFF second Play reuses session without backend restart or release"
);
await client.MonitorAsync(default);

foreach (var mode in new[] { "preflight-wrong-id", "preflight-wrong-hash", "preflight-missing-echo" })
{
    (client, fake) = await Connected(null);
    fake.Mode = mode;
    await Reject(() => client.PreflightAsync(directory, default, enableManagedHost: true), mode + " refused");
    before = fake.Calls.Count;
    await Reject(() => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true), mode + " cannot proceed to Play");
    Assert(fake.Calls.Count == before && !client.RecoveryRequired, mode + " refusal sends no game launch");
}
foreach (var mode in new[] { "launch-wrong-id", "launch-wrong-hash", "launch-missing-echo", "launch-not-ready" })
{
    (client, fake) = await Connected(null);
    await client.PreflightAsync(directory, default, enableManagedHost: true);
    fake.Mode = mode;
    await Reject(
        () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
        mode + " fails running acknowledgement"
    );
    Assert(client.RecoveryRequired, mode + " keeps owned game pending for recovery");
    before = fake.Calls.Count;
    await Reject(() => client.ReleaseAsync(default), mode + " prevents unsafe release");
    Assert(fake.Calls.Count == before, mode + " release refusal sends no helper command");
}
foreach (var mode in new[] { "observe-wrong-hash", "observe-not-ready", "observe-missing-echo" })
{
    (client, fake) = await Connected(null);
    await client.PreflightAsync(directory, default, enableManagedHost: true);
    await client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true);
    fake.Mode = mode;
    await Reject(() => client.MonitorAsync(default), mode + " detects stale host state during running observation");
    Assert(client.RecoveryRequired, mode + " preserves game latch after observation failure");
}
foreach (var initiallyOn in new[] { false, true })
{
    (client, fake) = await Connected(null);
    await client.PreflightAsync(directory, default, enableManagedHost: initiallyOn);
    before = fake.Calls.Count;
    await Reject(
        () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: !initiallyOn),
        "changing checkbox after preflight requires new preflight"
    );
    Assert(fake.Calls.Count == before && !client.RecoveryRequired, "changed selection refusal is local and does not claim a game");
}
(client, fake) = await Connected(null);
fake.Mode = "preflight-capability-changed";
await client.PreflightAsync(directory, default, enableManagedHost: true);
before = fake.Calls.Count;
await Reject(
    () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
    "changed advertised manifest after preflight is refused"
);
Assert(fake.Calls.Count == before, "new capability hash never silently replaces preflight selection");
(client, fake) = await Connected(null);
await client.PreflightAsync(directory, default, enableManagedHost: true);
before = fake.Calls.Count;
await Reject(
    () => client.LaunchAsync(FirstProfile, Path.Combine(directory, "different-game-directory"), default, enableManagedHost: true),
    "ON preflight is bound to the exact normalized game directory"
);
Assert(fake.Calls.Count == before && !client.RecoveryRequired, "changed directory refusal sends no game launch");
(client, fake) = await Connected(null);
await client.PreflightAsync(directory, default, enableManagedHost: true);
await client.EnsureBackendAsync("127.0.0.1:6969", default);
before = fake.Calls.Count;
await Reject(
    () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
    "explicit backend reconnection invalidates the prior ON preflight stamp"
);
Assert(fake.Calls.Count == before && !client.RecoveryRequired, "reconnected session needs a fresh preflight before launch");
(client, fake) = await Connected(null);
await client.PreflightAsync(directory, default);
fake.Mode = "off-stale-host";
await Reject(() => client.LaunchAsync(FirstProfile, directory, default), "OFF refuses a stale host echo or unexpected ready flag");
Assert(client.RecoveryRequired, "unexpected active host under OFF retains game cleanup ownership");

(client, fake) = await Connected(null);
fake.Mode = "preflight-not-ready";
await Reject(
    () => client.PreflightAsync(directory, default, enableManagedHost: true),
    "correct recipe echo without backendReady is not a successful preflight"
);
fake.Mode = null;
await client.RenewIdleLeaseAsync(default);
before = fake.Calls.Count;
await Reject(
    () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
    "later idle readiness cannot turn a failed preflight into an ON launch stamp"
);
Assert(fake.Calls.Count == before && !client.RecoveryRequired, "failed preflight remains local after backend readiness recovers");

foreach (var operation in new[] { "readEnvironment", "applyEnvironment" })
foreach (var mode in new[] { "remove", "change", "malformed" })
{
    (client, fake) = await Connected(null);
    var environment = await client.ReadEnvironmentAsync(default);
    await client.PreflightAsync(directory, default, enableManagedHost: true);
    fake.EnvironmentMode = mode;
    Func<Task> settingsOperation =
        operation == "readEnvironment"
            ? () => client.ReadEnvironmentAsync(default)
            : () => client.ApplyEnvironmentAsync(environment.Selection with { WeatherOverrideSeason = 2 }, environment.Revision, default);
    if (mode == "malformed")
    {
        await Reject(settingsOperation, operation + " validates malformed managed-host capability");
        Assert(
            client.EnvironmentChangeUncertain && client.ManagedHostCapability is null,
            operation + " malformed capability keeps settings uncertain and clears stale capability"
        );
        before = fake.Calls.Count;
        await Reject(
            () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
            operation + " malformed capability blocks Play pending authoritative recovery"
        );
        Assert(fake.Calls.Count == before, operation + " malformed metadata sends no game launch");
        fake.EnvironmentMode = null;
        await client.ReadEnvironmentAsync(default);
    }
    else
    {
        await settingsOperation();
        Assert(!client.EnvironmentChangeUncertain, operation + " valid capability update preserves authoritative settings");
        Assert(
            mode == "remove" ? client.ManagedHostCapability is null : client.ManagedHostCapability?.ManifestSha256 == new string('b', 64),
            operation + " replaces rather than retaining stale advertised capability"
        );
        fake.EnvironmentMode = null;
    }
    // Restore the original advertised recipe through a normal idle observation.
    // Its hash now matches the old preflight again: invalidated session/path
    // proof, rather than simple hash inequality, must still prevent Play.
    await client.RenewIdleLeaseAsync(default);
    before = fake.Calls.Count;
    await Reject(
        () => client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true),
        operation + " invalidates prior preflight even when the original capability returns"
    );
    Assert(
        fake.Calls.Count == before && !client.RecoveryRequired,
        operation + " stale preflight cannot launch after environment interaction"
    );
    await client.PreflightAsync(directory, default, enableManagedHost: true);
    await client.LaunchAsync(FirstProfile, directory, default, enableManagedHost: true);
    Assert(
        fake.Calls.Last().ProfileId == FirstProfile,
        operation + " fresh preflight restores selected-account launch without substitution"
    );
}

Console.WriteLine($"{checks} managed-host client assertions passed; no server or game started.");

sealed class FixtureFailure(string message) : Exception(message);

sealed class Fake : IRuntimeTransport
{
    public const string Session = "fixture-owned-session";
    public static readonly ManagedHostCapability Valid = new("supported-host", new string('a', 64), "Reviewed BepInEx host");
    public ManagedHostCapability? Capability { get; set; } = Valid;
    public string? Mode { get; set; }
    public string? EnvironmentMode { get; set; }
    public List<RuntimeRequest> Calls { get; } = [];
    private ManagedHostSelection? running;
    private EnvironmentSnapshot environment = new(
        1,
        new string('c', 64),
        "UTC",
        new[] { new HolidayEventOption("none", "No event"), new HolidayEventOption("christmas", "Christmas") },
        Enumerable.Range(0, 7).Select(x => new WeatherSeasonOption(x, "Season " + x)).ToArray(),
        "none",
        null,
        new DateOnly(2099, 12, 31),
        null,
        "America/Los_Angeles",
        "23:59:00",
        "01:00:00"
    );

    public Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add(request);
        var reply = new RuntimeReply(
            1,
            request.RequestId,
            Session,
            "backendReady",
            BackendUrl: OfflineRuntime.BackendUrl,
            ManagedHostCapability: Capability
        );
        if (request.Operation == "preflight")
        {
            reply = reply with { ManagedHostRecipe = request.ManagedHostRecipe };
            reply = Mode switch
            {
                "preflight-wrong-id" => reply with { ManagedHostRecipe = request.ManagedHostRecipe! with { Id = "other-host" } },
                "preflight-wrong-hash" => reply with
                {
                    ManagedHostRecipe = request.ManagedHostRecipe! with { ManifestSha256 = new string('b', 64) },
                },
                "preflight-missing-echo" => reply with { ManagedHostRecipe = null },
                "preflight-capability-changed" => reply with
                {
                    ManagedHostCapability = Valid with { ManifestSha256 = new string('b', 64) },
                },
                "preflight-not-ready" => reply with { State = "waiting" },
                _ => reply,
            };
        }
        if (request.Operation == "launch")
        {
            running = request.ManagedHostRecipe;
            reply = reply with
            {
                State = "running",
                GamePid = 12345,
                GameCreatedUtc = "2026-10-19T00:00:00Z",
                ManagedHostRecipe = running,
                ManagedHostReady = running is not null,
            };
            reply = Mode switch
            {
                "launch-wrong-id" => reply with { ManagedHostRecipe = running! with { Id = "other-host" } },
                "launch-wrong-hash" => reply with { ManagedHostRecipe = running! with { ManifestSha256 = new string('b', 64) } },
                "launch-missing-echo" => reply with { ManagedHostRecipe = null },
                "launch-not-ready" => reply with { ManagedHostReady = false },
                _ => reply,
            };
            if (Mode == "off-stale-host")
                reply = reply with { ManagedHostRecipe = new(Valid.Id, Valid.ManifestSha256), ManagedHostReady = true };
        }
        if (request.Operation == "observe")
        {
            reply = reply with { GameCleanupVerified = true };
            if (Mode?.StartsWith("observe-", StringComparison.Ordinal) == true)
            {
                reply = reply with
                {
                    State = "running",
                    GamePid = 12345,
                    GameCreatedUtc = "2026-10-19T00:00:00Z",
                    ManagedHostRecipe = running,
                    ManagedHostReady = true,
                    GameCleanupVerified = false,
                };
                reply = Mode switch
                {
                    "observe-wrong-hash" => reply with { ManagedHostRecipe = running! with { ManifestSha256 = new string('b', 64) } },
                    "observe-not-ready" => reply with { ManagedHostReady = false },
                    "observe-missing-echo" => reply with { ManagedHostRecipe = null },
                    _ => reply,
                };
            }
        }
        if (request.Operation is "readEnvironment" or "applyEnvironment")
        {
            if (request.Settings is { } settings)
                environment = environment with
                {
                    Revision = new string('d', 64),
                    HolidayEventId = settings.HolidayEventId,
                    HolidayEndDate = settings.HolidayEndDate,
                    ProgressionEndDate = settings.ProgressionEndDate,
                    WeatherOverrideSeason = settings.WeatherOverrideSeason,
                };
            reply = reply with { Settings = environment };
            reply = EnvironmentMode switch
            {
                "remove" => reply with { ManagedHostCapability = null },
                "change" => reply with { ManagedHostCapability = Valid with { ManifestSha256 = new string('b', 64) } },
                "malformed" => reply with { ManagedHostCapability = Valid with { ManifestSha256 = "not-a-valid-manifest" } },
                _ => reply,
            };
        }
        return Task.FromResult(reply);
    }
}
