using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ComponentAce.Compression.Libs.zlib;
using Microsoft.Extensions.Logging.Abstractions;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;
using SPTarkov.Core.Runtime;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Requests;
using SPTarkov.Core.SPT.Responses;

// Actual Core HTTP and session code, compressed bodies, in-memory handler. No socket/server/game.
var checks = 0;
void Check(bool value, string label)
{
    if (!value)
        throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
using (var f = new Fixture())
{
    var pending = f.Session.ConnectToServerAsync(Server.Local);
    await f.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!pending.IsCompleted && f.Handler.Calls.Count == 0, "HTTP waits for runtime owned-server acknowledgement");
    f.Transport.Ready.TrySetResult();
    Check((await pending).Success, "actual SessionHelper connects using compressed HTTP responses");
    Check(
        f.Handler.Calls.Select(x => x.Path).SequenceEqual(new[] { Urls.Ping, Urls.Profiles, Urls.Types, Urls.ModPages }),
        "existing connection endpoint order preserved"
    );
    Check(
        f.State.ProfileList.Single().ProfileId == Fixture.ProfileId
            && f.State.ProfileTypes["fixture"] == "Fixture edition"
            && f.State.ModPages.Single().Name == "Fixture mod",
        "profiles, types and mod pages populate actual state"
    );
    Check(
        f.Handler.Calls.All(x => x.Method == HttpMethod.Get && x.Address == OfflineRuntime.BackendUrl),
        "connection uses acknowledged fixed local endpoint"
    );
    var profile = f.State.ProfileList.Single();
    Check(
        await f.Session.LoginToProfileAsync(profile) && ReferenceEquals(f.State.SelectedProfile, profile),
        "profile login selects requested profile"
    );
    var details = await f.Session.LoginWithDetailsAsync(Fixture.Username);
    Check(details?.ProfileId == Fixture.ProfileId && f.State.SelectedProfile == details, "details login selects server-returned local ID");
    Check(
        f.Handler.Calls.Where(x => x.Path is Urls.Login or Urls.Profile)
            .All(x => x.Method == HttpMethod.Put && JsonSerializer.Deserialize<SPTLoginRequest>(x.Body!)?.Username == Fixture.Username),
        "real compressed login PUT preserves username"
    );
    var registration = await f.Http.GameServerPut<SPTRegisterResponse>(
        Urls.Register,
        new SPTRegisterRequest { Username = "new fixture user", Edition = "fixture" },
        CancellationToken.None
    );
    var registered = JsonSerializer.Deserialize<SPTRegisterRequest>(f.Handler.Calls.Last().Body!);
    Check(
        f.Handler.Calls.Last().Path == Urls.Register
            && registered?.Username == "new fixture user"
            && registered.Edition == "fixture"
            && registration?.Response == true
            && registration.Profiles.Single().ProfileId == Fixture.ProfileId,
        "existing registration DTO and HTTP path preserve fields and returned profiles"
    );
    await f.Runtime.PreflightAsync(Environment.CurrentDirectory, CancellationToken.None);
    await f.Runtime.LaunchAsync(f.State.SelectedProfile!.ProfileId, Environment.CurrentDirectory, CancellationToken.None);
    await f.Runtime.MonitorAsync(CancellationToken.None);
    f.Handler.Level = 12;
    var refreshed = await f.Http.GameServerPut<SPTProfileResponse>(
        Urls.Profile,
        new SPTLoginRequest { Username = Fixture.Username },
        CancellationToken.None
    );
    Check(refreshed?.Response?.CurrentLevel == 12, "actual HTTP profile refresh reads changed data after game cleanup");
    await f.Runtime.LaunchAsync(f.State.SelectedProfile!.ProfileId, Environment.CurrentDirectory, CancellationToken.None);
    Check(
        f.Transport.Calls.Count(x => x.Operation == "launch") == 2
            && f.Transport.Calls.Where(x => x.Operation == "launch")
                .All(x => x.ProfileId == Fixture.ProfileId && x.SessionId == "owned-fixture")
            && f.Transport.Calls.Count(x => x.Operation == "ensureBackend") == 1
            && f.Transport.Calls.All(x => x.Operation != "release"),
        "second Play uses selected profile and same backend lease without release"
    );
    f.Handler.LoginAllowed = false;
    var selected = f.State.SelectedProfile;
    Check(
        !await f.Session.LoginToProfileAsync(new MiniProfile { Username = "rejected" }) && f.State.SelectedProfile == selected,
        "rejected login retains selected profile"
    );
    var before = f.Handler.Calls.Count;
    Check(
        await f.Session.LoginWithDetailsAsync("rejected") is null && f.Handler.Calls.Skip(before).All(x => x.Path == Urls.Login),
        "rejected details login never fetches profile"
    );
}
using (var f = new Fixture())
{
    f.Transport.Failure = "owned backend refused";
    f.Transport.Ready.TrySetResult();
    var result = await f.Session.ConnectToServerAsync(Server.Local);
    Check(!result.Success && result.FailedStep == ConnectStep.Runtime && f.Handler.Calls.Count == 0, "runtime failure blocks all HTTP");
}
using (var f = new Fixture())
{
    var runtime = new OfflineRuntime(
        new FileRuntimeTransport(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"))
    );
    var session = new SessionHelper(NullLogger<SessionHelper>.Instance, f.Http, f.State, runtime);
    var result = await session.ConnectToServerAsync(Server.Local);
    Check(
        !result.Success
            && result.FailedStep == ConnectStep.Runtime
            && f.Handler.Calls.Count == 0
            && result.ErrorMessage?.Contains("Could not connect") == true,
        "missing executable fails before HTTP with actionable error"
    );
}
using (var f = new Fixture())
{
    var result = await f.Session.ConnectToServerAsync(new Server { IpAddress = "https://user@192.0.2.1:6969" });
    Check(
        !result.Success && result.FailedStep == ConnectStep.Runtime && f.Handler.Calls.Count == 0 && f.Transport.Calls.Count == 0,
        "credential-bearing endpoint refused before helper and HTTP"
    );
}
using (var f = new Fixture())
using (var cancellation = new CancellationTokenSource())
{
    var pending = f.Session.ConnectToServerAsync(Server.Local, cancellationToken: cancellation.Token);
    await f.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    var result = await pending;
    Check(
        !result.Success && result.FailedStep == ConnectStep.Runtime && f.Handler.Calls.Count == 0,
        "cancelled ownership wait sends no HTTP"
    );
}
foreach (
    var (path, step, count) in new[]
    {
        (Urls.Ping, ConnectStep.Ping, 1),
        (Urls.Profiles, ConnectStep.Profiles, 2),
        (Urls.Types, ConnectStep.Types, 3),
        (Urls.ModPages, ConnectStep.ModPages, 4),
    }
)
{
    using var f = new Fixture();
    f.Transport.Ready.TrySetResult();
    f.Handler.FailurePath = path;
    var result = await f.Session.ConnectToServerAsync(Server.Local);
    Check(
        !result.Success && result.FailedStep == step && f.Handler.Calls.Count == count,
        $"HTTP {step} failure stops subsequent connection requests"
    );
}
foreach (var (path, step, count) in new[] { (Urls.Profiles, ConnectStep.Profiles, 2), (Urls.Types, ConnectStep.Types, 3) })
{
    using var f = new Fixture();
    f.Transport.Ready.TrySetResult();
    f.Handler.NullPath = path;
    var result = await f.Session.ConnectToServerAsync(Server.Local);
    Check(
        !result.Success
            && result.FailedStep == step
            && f.Handler.Calls.Count == count
            && f.State.ProfileList is not null
            && f.State.ProfileTypes is not null,
        $"null {step} fails safely without null collections"
    );
}
using (var f = new Fixture())
{
    f.Transport.Ready.TrySetResult();
    f.Handler.NullPath = Urls.ModPages;
    Check(
        (await f.Session.ConnectToServerAsync(Server.Local)).Success && f.State.ModPages.Count == 0,
        "optional null mod pages retains empty fallback"
    );
}
using (var f = new Fixture())
using (var cancellation = new CancellationTokenSource())
{
    f.Transport.Ready.TrySetResult();
    f.Handler.BlockPath = Urls.Profiles;
    var pending = f.Session.ConnectToServerAsync(Server.Local, cancellationToken: cancellation.Token);
    await f.Handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    var result = await pending;
    Check(
        !result.Success && result.FailedStep == ConnectStep.Profiles && f.Handler.Calls.Count == 2,
        "in-flight HTTP cancellation stops at Profiles"
    );
}
using (var key = RSA.Create(2048))
using (
    var certificate = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddMinutes(5)
    )
)
{
    var fingerprint = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    Check(
        !HttpHelper.ValidateServerCertificate(certificate, SslPolicyErrors.RemoteCertificateChainErrors, null),
        "untrusted certificate without a saved or verified pin is refused"
    );
    Check(HttpHelper.ValidateServerCertificate(certificate, SslPolicyErrors.None, null), "valid PKI remains available without a pin");
    Check(
        HttpHelper.ValidateServerCertificate(certificate, SslPolicyErrors.RemoteCertificateChainErrors, fingerprint),
        "explicit verified fingerprint admits the exact self-signed certificate"
    );
    Check(
        !HttpHelper.ValidateServerCertificate(certificate, SslPolicyErrors.None, new string('b', 64)),
        "saved pin mismatch is refused even when PKI is valid"
    );
    Check(
        !HttpHelper.ValidateServerCertificate(null, SslPolicyErrors.None, fingerprint),
        "missing peer certificate is never proof of a pin"
    );
}
Console.WriteLine($"{checks} session/HTTP assertions passed; no server or game started.");

sealed class Fixture : IDisposable
{
    public const string ProfileId = "0123456789abcdef01234567";
    public const string Username = "fixture selected user";
    public StateHelper State { get; } = new(NullLogger<StateHelper>.Instance);
    public OwnedTransport Transport { get; } = new();
    public MemoryHttp Handler { get; } = new();
    private readonly HttpClient client;
    public OfflineRuntime Runtime { get; }
    public HttpHelper Http { get; }
    public SessionHelper Session { get; }

    public Fixture()
    {
        Runtime = new OfflineRuntime(Transport);
        client = new HttpClient(Handler);
        Http = new HttpHelper(NullLogger<HttpHelper>.Instance, State, client);
        Session = new SessionHelper(NullLogger<SessionHelper>.Instance, Http, State, Runtime);
    }

    public void Dispose() => client.Dispose();
}

sealed class OwnedTransport : IRuntimeTransport
{
    public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<RuntimeRequest> Calls { get; } = [];
    public string? Failure { get; set; }

    public async Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (request.Operation == "ensureBackend")
        {
            Requested.TrySetResult();
            await Ready.Task.WaitAsync(cancellationToken);
        }
        return new RuntimeReply(
            1,
            request.RequestId,
            "owned-fixture",
            Failure is not null ? "failed"
                : request.Operation == "launch" ? "running"
                : "backendReady",
            OfflineRuntime.BackendUrl,
            request.Operation == "launch" ? 1234 : null,
            request.Operation == "launch" ? "2026-10-16T00:00:00Z" : null,
            Error: Failure,
            GameCleanupVerified: request.Operation == "observe"
        );
    }
}

record HttpCall(string Path, HttpMethod Method, string Address, string? Body);

sealed class MemoryHttp : HttpMessageHandler
{
    public List<HttpCall> Calls { get; } = [];
    public string? FailurePath { get; set; }
    public string? NullPath { get; set; }
    public string? BlockPath { get; set; }
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool LoginAllowed { get; set; } = true;
    public int Level { get; set; } = 1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
        var body = request.Content is null ? null : SimpleZlib.Decompress(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        Calls.Add(new HttpCall(uri.AbsolutePath, request.Method, uri.GetLeftPart(UriPartial.Authority), body));
        if (uri.AbsolutePath == FailurePath)
            throw new HttpRequestException("Fixture endpoint failed");
        if (uri.AbsolutePath == BlockPath)
        {
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var profile = new MiniProfile
        {
            Username = Fixture.Username,
            ProfileId = Fixture.ProfileId,
            CurrentLevel = Level,
        };
        object response = uri.AbsolutePath switch
        {
            Urls.Ping => new SPTPingResponse { Response = "Pong!" },
            Urls.Profiles => new SPTProfilesResponse { Response = NullPath == Urls.Profiles ? null : [profile] },
            Urls.Types => new SPTTypesResponse { Response = NullPath == Urls.Types ? null : new() { ["fixture"] = "Fixture edition" } },
            Urls.ModPages => new SPTModPagesResponse
            {
                Response = NullPath == Urls.ModPages ? null : [new ModPage { Name = "Fixture mod", HomePage = "/fixture" }],
            },
            Urls.Login => new SPTLoginResponse { Response = LoginAllowed },
            Urls.Profile => new SPTProfileResponse { Response = profile },
            Urls.Register => new SPTRegisterResponse { Response = true, Profiles = [profile] },
            _ => throw new InvalidOperationException("Unexpected HTTP request: " + uri.AbsolutePath),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                SimpleZlib.CompressToBytes(JsonSerializer.Serialize(response, response.GetType()), zlibConst.Z_BEST_COMPRESSION)
            ),
        };
    }
}
