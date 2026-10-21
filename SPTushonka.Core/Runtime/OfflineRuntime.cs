using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SPTarkov.Core.Runtime;

public static class ServerEndpoint
{
    public static bool IsValid(string address)
    {
        try
        {
            Normalize(address);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static string Normalize(string address)
    {
        var value = address?.Trim() ?? "";
        if (
            value.Length == 0
            || value.Length > 512
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > 127)
            || value.Contains('\\')
            || value.Contains('%')
        )
            throw new InvalidOperationException("Enter a server hostname and port, such as server.example:6969.");
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.HostNameType == UriHostNameType.Unknown
            || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.Port is < 1 or > 65535
        )
            throw new InvalidOperationException("Enter an HTTPS server hostname and port without a path or sign-in details.");
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var originalHost = authority.StartsWith('[') ? authority[..(authority.IndexOf(']') + 1)].Trim('[', ']') : authority.Split(':')[0];
        if (
            uri.HostNameType == UriHostNameType.IPv4 && originalHost != uri.Host
            || uri.HostNameType == UriHostNameType.Dns
                && (
                    uri.IdnHost.Length > 253
                    || uri.IdnHost.Split('.').Any(label => !Regex.IsMatch(label, "\\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\z"))
                )
        )
            throw new InvalidOperationException("Enter a valid server hostname or IP address.");
        var host =
            uri.HostNameType == UriHostNameType.IPv6 ? "[" + CanonicalV6(uri.Host.Trim('[', ']')) + "]" : uri.IdnHost.ToLowerInvariant();
        return "https://" + host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsLocal(string origin) => Normalize(origin) == OfflineRuntime.BackendUrl;

    public static bool IsFingerprint(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");

    private static string CanonicalV6(string host)
    {
        var bytes = System.Net.IPAddress.Parse(host).GetAddressBytes();
        var groups = Enumerable.Range(0, 8).Select(i => (bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToArray();
        var bestStart = -1;
        var bestLength = 1;
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i] != 0)
                continue;
            var start = i;
            while (i < groups.Length && groups[i] == 0)
                i++;
            if (i - start > bestLength)
            {
                bestStart = start;
                bestLength = i - start;
            }
        }
        string Join(IEnumerable<int> values) => string.Join(":", values.Select(n => n.ToString("x", CultureInfo.InvariantCulture)));
        return bestStart < 0 ? Join(groups) : Join(groups.Take(bestStart)) + "::" + Join(groups.Skip(bestStart + bestLength));
    }
}

public sealed class ServerCertificateTrustException(string backendUrl, string fingerprint)
    : InvalidOperationException("This server's certificate needs your approval before connecting.")
{
    public string BackendUrl { get; } = backendUrl;
    public string Fingerprint { get; } = fingerprint;
}

public static class RuntimeUserMessages
{
    public const string StartFailed = "The game could not finish starting. See the launcher log for details.";
    public const string PreparationFailed =
        "The game could not be prepared. Check the game folder and connection protection in Settings, then try Play again. Details are in the launcher log.";
    public const string RecoveryFailed =
        "The launcher could not finish closing the game or restoring its files. Close the game if it is still open, then try Stop game and recover again. Details are in the launcher log.";
    public const string MonitoringFailed =
        "The launcher could not confirm the game's status. Close the game, then select Stop game and recover. Details are in the launcher log.";

    public static string Starting(bool recoveryRequired) =>
        StartFailed + (recoveryRequired ? " Select Stop game and recover to try restoring the game files." : " You can try Play again.");
}

public record ManagedHostSelection(string Id, string ManifestSha256);

public record ManagedHostCapability(string Id, string ManifestSha256, string DisplayName);

public record RuntimeRequest(
    int Protocol,
    string RequestId,
    string Operation,
    string? SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfileId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GameDirectory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RequireGameConnections = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentSelection? Settings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManagedHostSelection? ManagedHostRecipe = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowServerStart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackendUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BackendTlsSha256 = null
);

public record RuntimeReply(
    int Protocol,
    string RequestId,
    string? SessionId,
    string State,
    string? BackendUrl = null,
    int? GamePid = null,
    string? GameCreatedUtc = null,
    bool CleanupVerified = false,
    string? Error = null,
    bool GameCleanupVerified = false,
    bool GameConnectionsV1 = false,
    bool GameConnectionsVerified = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentSnapshot? Settings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManagedHostCapability? ManagedHostCapability = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManagedHostSelection? ManagedHostRecipe = null,
    bool ManagedHostReady = false,
    string? BackendTlsSha256 = null,
    bool CertificateTrustRequired = false,
    bool? EnvironmentAvailable = null
);

public interface IRuntimeTransport
{
    Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken);
}

/// <summary>One short-lived CLI command talks to a persistent, independently guarded runtime owner.</summary>
public sealed class FileRuntimeTransport(string helperPath) : IRuntimeTransport
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.Strict };

    public async Task<RuntimeReply> SendAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(helperPath))
            throw new InvalidOperationException("Install the matching offline runtime beside the launcher: runtime/eft-runtime.exe.");
        var dir = Path.Combine(Path.GetTempPath(), "eft-launcher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "request.json");
        var output = Path.Combine(dir, "response.json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request, Json), cancellationToken);
            var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--request");
            start.ArgumentList.Add(input);
            start.ArgumentList.Add("--response");
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Runtime command could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation is "readEnvironment" or "applyEnvironment" ? 240 : 130));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"Runtime command failed ({process.ExitCode}); inspect runtime session logs.");
            var reply =
                JsonSerializer.Deserialize<RuntimeReply>(await File.ReadAllTextAsync(output, cancellationToken), Json)
                ?? throw new InvalidOperationException("Runtime returned an empty response.");
            if (reply.Protocol != 1 || reply.RequestId != request.RequestId)
                throw new InvalidOperationException("Runtime protocol or request identity mismatch.");
            return reply;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class OfflineRuntime
{
    public const string BackendUrl = "https://127.0.0.1:6969";
    private readonly IRuntimeTransport transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? sessionId;
    private string? lastState;
    private bool gameConnectionsV1;
    private volatile bool gameInProgress;
    private volatile bool environmentChangeUncertain;
    private EnvironmentSnapshot? environmentSnapshot;
    public ManagedHostCapability? ManagedHostCapability { get; private set; }
    private ManagedHostSelection? preflightManagedHost;
    private ManagedHostSelection? runningManagedHost;
    private string? managedPreflightDirectory;
    private string? managedPreflightSession;
    public bool EnvironmentChangeUncertain => environmentChangeUncertain;

    // A pending or acknowledged game must be reconciled before profile actions resume.
    public bool RecoveryRequired => gameInProgress;
    private volatile bool idleLeaseLost;
    public bool IdleLeaseLost => idleLeaseLost;
    private int? gamePid;
    private DateTimeOffset gameCreated;
    private int startupBackendAttempted;
    private string? sessionBackendUrl;
    private string? requestedBackendTlsSha256;
    public string? BoundBackendUrl { get; private set; }
    public string? VerifiedBackendTlsSha256 { get; private set; }
    public bool IsExternalBackend => BoundBackendUrl is not null && !ServerEndpoint.IsLocal(BoundBackendUrl);
    public bool EnvironmentAvailable { get; private set; } = true;

    public bool RequiresServerDisconnect(string address, string? certificateSha256) =>
        sessionBackendUrl is not null && sessionBackendUrl != ServerEndpoint.Normalize(address)
        || BoundBackendUrl is not null && certificateSha256 != requestedBackendTlsSha256 && certificateSha256 != VerifiedBackendTlsSha256;

    public OfflineRuntime(IRuntimeTransport transport)
    {
        this.transport = transport;
    }

    public OfflineRuntime()
        : this(new FileRuntimeTransport(Path.Combine(AppContext.BaseDirectory, "runtime", "eft-runtime.exe"))) { }

    // All state and transport access is serialized, including window shutdown from another thread.
    private async Task<RuntimeReply> Command(
        string operation,
        CancellationToken token,
        string? profile = null,
        string? directory = null,
        bool requireGameConnections = false,
        ManagedHostSelection? managedHost = null,
        bool? allowServerStart = null,
        string? backendUrl = null,
        string? backendTlsSha256 = null
    )
    {
        var request = new RuntimeRequest(
            1,
            Guid.NewGuid().ToString("N"),
            operation,
            sessionId,
            profile,
            directory,
            requireGameConnections,
            ManagedHostRecipe: managedHost,
            AllowServerStart: allowServerStart,
            BackendUrl: backendUrl,
            BackendTlsSha256: backendTlsSha256
        );
        ManagedHostCapability = null;
        if (operation == "ensureBackend")
        {
            managedPreflightSession = null;
            managedPreflightDirectory = null;
        }
        lastState = null; // Lost/malformed replies cannot leave a stale idle-release permission.
        var reply = await transport.SendAsync(request, token);
        if (reply.Protocol != 1 || reply.RequestId != request.RequestId)
            throw new InvalidOperationException("Runtime protocol or request identity mismatch.");
        if (string.IsNullOrWhiteSpace(reply.SessionId) || (sessionId is not null && reply.SessionId != sessionId))
            throw new InvalidOperationException("Runtime session identity mismatch.");
        sessionId = reply.SessionId;
        if (reply.EnvironmentAvailable is { } environmentAvailable)
            EnvironmentAvailable = environmentAvailable;
        var expectedUrl = backendUrl ?? sessionBackendUrl ?? BackendUrl;
        if (reply.CertificateTrustRequired)
        {
            if (
                operation != "ensureBackend"
                || reply.State != "failed"
                || reply.BackendUrl != expectedUrl
                || !ServerEndpoint.IsFingerprint(reply.BackendTlsSha256)
            )
                throw new InvalidOperationException("The server certificate response could not be verified.");
            sessionBackendUrl = expectedUrl;
            throw new ServerCertificateTrustException(expectedUrl, reply.BackendTlsSha256!);
        }
        if (reply.BackendUrl is not null && reply.BackendUrl != expectedUrl)
            throw new InvalidOperationException("The server endpoint changed. Disconnect before choosing another server.");
        if (reply.BackendTlsSha256 is not null && !ServerEndpoint.IsFingerprint(reply.BackendTlsSha256))
            throw new InvalidOperationException("The server returned an invalid certificate fingerprint.");
        if (operation == "ensureBackend")
            sessionBackendUrl = expectedUrl;
        if (
            operation != "ensureBackend"
            && reply.State != "cleaned"
            && VerifiedBackendTlsSha256 is not null
            && reply.BackendTlsSha256 is not null
            && reply.BackendTlsSha256 != VerifiedBackendTlsSha256
        )
            throw new InvalidOperationException("The server certificate changed. Disconnect and connect again to review it.");
        gameConnectionsV1 = reply.GameConnectionsV1;
        if (reply.State == "failed" || reply.Error is not null)
            throw new InvalidOperationException(reply.Error ?? "Offline runtime failed.");
        if (reply.State is not ("waiting" or "backendReady" or "starting" or "running" or "stopping" or "cleaned"))
            throw new InvalidOperationException("Unexpected runtime lifecycle state: " + reply.State);
        if (reply.State == "backendReady" && (reply.BackendUrl != expectedUrl || reply.CleanupVerified))
            throw new InvalidOperationException("Runtime did not retain the owned local backend.");
        UpdateManagedHostCapability(reply);
        lastState = reply.State;
        return reply;
    }

    private void UpdateManagedHostCapability(RuntimeReply reply)
    {
        ManagedHostCapability = null;
        if (reply.ManagedHostCapability is { } capability)
        {
            if (
                !Regex.IsMatch(capability.Id ?? "", "\\A[a-z0-9][a-z0-9._-]{0,79}\\z")
                || !Regex.IsMatch(capability.ManifestSha256 ?? "", "\\A[a-f0-9]{64}\\z")
                || string.IsNullOrWhiteSpace(capability.DisplayName)
                || capability.DisplayName.Length > 120
            )
                throw new InvalidOperationException("Runtime returned an invalid managed host capability.");
            if (reply.State == "backendReady")
                ManagedHostCapability = capability;
        }
    }

    private void RequireIdleBackend()
    {
        if (environmentChangeUncertain)
            throw new InvalidOperationException("Reload server settings to verify the previous change before continuing.");
        if (gameInProgress)
            throw new InvalidOperationException(
                "A game launch or cleanup is still pending. Resolve the owned runtime session before another Play."
            );
        if (sessionId is null || lastState != "backendReady")
            throw new InvalidOperationException("Connect to the owned local server first.");
    }

    public async Task EnsureBackendOnStartupAsync(
        bool allowServerStart,
        CancellationToken token,
        string address = "127.0.0.1:6969",
        string? backendTlsSha256 = null
    )
    {
        if (Interlocked.Exchange(ref startupBackendAttempted, 1) != 0)
            return;
        await EnsureBackendAsync(address, token, allowServerStart, backendTlsSha256);
    }

    public async Task EnsureBackendAsync(
        string address,
        CancellationToken token,
        bool allowServerStart = true,
        string? backendTlsSha256 = null
    )
    {
        var origin = ServerEndpoint.Normalize(address);
        if (backendTlsSha256 is not null && !ServerEndpoint.IsFingerprint(backendTlsSha256))
            throw new InvalidOperationException(
                "The saved server certificate is invalid. Clear its certificate in Edit server and connect again."
            );
        await gate.WaitAsync(token);
        try
        {
            if (gameInProgress || environmentChangeUncertain)
                RequireIdleBackend();
            if (RequiresServerDisconnect(origin, backendTlsSha256))
                throw new InvalidOperationException("Disconnect before changing the server address or trusted certificate.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            // User-triggered reconnect reconciles an uncertain idle lease first.
            // Never discard unknown ownership or create another backend on a timer.
            if (idleLeaseLost && sessionId is not null)
                await ReleaseCoreAsync(deadline.Token);
            var reply = await Command(
                "ensureBackend",
                deadline.Token,
                allowServerStart: allowServerStart && ServerEndpoint.IsLocal(origin),
                backendUrl: origin,
                backendTlsSha256: backendTlsSha256
            );
            sessionBackendUrl = origin;
            while (reply.State == "waiting")
            {
                await Task.Delay(1000, deadline.Token);
                reply = await Command("observe", deadline.Token);
            }
            if (reply.State != "backendReady")
                throw new InvalidOperationException("Owned local backend is not ready; no profile requests were sent.");
            if (!ServerEndpoint.IsLocal(origin) && !ServerEndpoint.IsFingerprint(reply.BackendTlsSha256))
                throw new InvalidOperationException("The server certificate could not be verified. No profile requests were sent.");
            if (backendTlsSha256 is not null && reply.BackendTlsSha256 != backendTlsSha256)
                throw new InvalidOperationException("The server did not confirm the trusted certificate.");
            if (BoundBackendUrl is not null && VerifiedBackendTlsSha256 is not null && reply.BackendTlsSha256 != VerifiedBackendTlsSha256)
                throw new ServerCertificateTrustException(origin, reply.BackendTlsSha256!);
            BoundBackendUrl = origin;
            VerifiedBackendTlsSha256 = reply.BackendTlsSha256;
            requestedBackendTlsSha256 = backendTlsSha256;
            EnvironmentAvailable = reply.EnvironmentAvailable ?? ServerEndpoint.IsLocal(origin);
            idleLeaseLost = false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out waiting for the owned backend. Runtime retains cleanup responsibility; inspect its session logs."
            );
        }
        finally
        {
            gate.Release();
        }
    }

    private ManagedHostSelection? SelectManagedHost(bool enabled)
    {
        if (!enabled)
            return null;
        var capability =
            ManagedHostCapability
            ?? throw new InvalidOperationException(
                "Connect to a local runtime with verified BepInEx mod support before enabling this option."
            );
        return new(capability.Id, capability.ManifestSha256);
    }

    private static void VerifyManagedHost(RuntimeReply reply, ManagedHostSelection? selected, bool requireReady)
    {
        if (
            reply.ManagedHostRecipe != selected
            || (selected is not null && requireReady && !reply.ManagedHostReady)
            || (selected is null && reply.ManagedHostReady)
        )
            throw new InvalidOperationException(
                "Runtime did not verify the selected managed host; recover the owned session before retrying."
            );
    }

    public async Task PreflightAsync(string directory, CancellationToken token, bool enableManagedHost = false)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireIdleBackend();
            preflightManagedHost = null;
            managedPreflightDirectory = null;
            managedPreflightSession = null;
            var selected = SelectManagedHost(enableManagedHost);
            var reply = await Command("preflight", token, directory: Path.GetFullPath(directory), managedHost: selected);
            VerifyManagedHost(reply, selected, false);
            if (reply.State != "backendReady")
                throw new InvalidOperationException("Supported game preflight did not pass.");
            preflightManagedHost = selected;
            managedPreflightDirectory = Path.GetFullPath(directory);
            managedPreflightSession = sessionId;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RenewIdleLeaseAsync(CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token))
            return;
        try
        {
            if (sessionId is null || gameInProgress || idleLeaseLost || environmentChangeUncertain)
                return;
            var reply = await Command("observe", token);
            if (reply.State != "backendReady")
            {
                if (reply.State is "waiting" or "starting" or "running" or "stopping")
                    gameInProgress = true;
                if (reply.State == "cleaned" && reply.CleanupVerified)
                    sessionId = null;
                throw new InvalidOperationException("The local server session is no longer ready. Reconnect to the server.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            idleLeaseLost = true;
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RuntimeReply> LaunchAsync(
        string profile,
        string directory,
        CancellationToken token,
        IProgress<string>? progress = null,
        bool requireGameConnections = false,
        bool enableManagedHost = false
    )
    {
        if (!Regex.IsMatch(profile, "\\A[0-9a-fA-F]{24}\\z"))
            throw new InvalidOperationException("Select a valid local profile.");
        var gameDirectory = Path.GetFullPath(directory);
        await gate.WaitAsync(token);
        try
        {
            RequireIdleBackend();
            if (requireGameConnections && !gameConnectionsV1)
                throw new InvalidOperationException("Update the offline runtime before launching with game connection protection.");
            var selectedHost = SelectManagedHost(enableManagedHost);
            if (
                selectedHost != preflightManagedHost
                || (
                    selectedHost is not null
                    && (
                        managedPreflightSession != sessionId
                        || !string.Equals(managedPreflightDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
                throw new InvalidOperationException("Managed host selection changed; run preflight again before Play.");
            runningManagedHost = selectedHost;
            managedPreflightSession = null;
            managedPreflightDirectory = null;
            gameInProgress = true; // Never clear on timeout/error: the command may already have launched.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var reply = await Command("launch", deadline.Token, profile, gameDirectory, requireGameConnections, selectedHost);
            if (requireGameConnections && reply.State == "running" && !reply.GameConnectionsVerified)
                throw new InvalidOperationException("Runtime did not verify game connection protection; recover the owned game session.");
            while (reply.State is "waiting" or "starting")
            {
                progress?.Report(reply.State == "waiting" ? "Waiting for the game slotÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦" : "Starting gameÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦");
                await Task.Delay(1000, deadline.Token);
                reply = await Command("observe", deadline.Token);
                if (requireGameConnections && reply.State == "running" && !reply.GameConnectionsVerified)
                    throw new InvalidOperationException(
                        "Runtime did not verify game connection protection; recover the owned game session."
                    );
            }
            if (reply.State != "running" || reply.GamePid is not > 0 || !DateTimeOffset.TryParse(reply.GameCreatedUtc, out gameCreated))
                throw new InvalidOperationException(
                    RuntimeUserMessages.StartFailed,
                    new InvalidOperationException(
                        $"No identity-verified game-start acknowledgement. State={reply.State}; GamePid={reply.GamePid}; GameCreatedUtc={reply.GameCreatedUtc}; GameCleanupVerified={reply.GameCleanupVerified}; CleanupVerified={reply.CleanupVerified}. Runtime retains cleanup ownership."
                    )
                );
            VerifyManagedHost(reply, selectedHost, true);
            gamePid = reply.GamePid;
            progress?.Report("Game running");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                RuntimeUserMessages.StartFailed,
                new TimeoutException("Timed out waiting for game start. Runtime retains cleanup ownership.")
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RuntimeReply> LaunchWithRecoveryAsync(
        string profile,
        string directory,
        CancellationToken token,
        IProgress<string>? progress = null,
        bool requireGameConnections = false,
        bool enableManagedHost = false
    )
    {
        try
        {
            return await LaunchAsync(profile, directory, token, progress, requireGameConnections, enableManagedHost);
        }
        catch (Exception startError) when (RecoveryRequired)
        {
            progress?.Report("Restoring game filesÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦");
            try
            {
                // The existing stop path verifies the exact session/process and
                // restores only recorded files. It retains the local server.
                await StopAsync(CancellationToken.None);
            }
            catch (Exception recoveryError)
            {
                throw new InvalidOperationException(
                    RuntimeUserMessages.StartFailed,
                    new AggregateException("Game startup failed and automatic recovery could not complete.", startError, recoveryError)
                );
            }
            throw;
        }
    }

    public async Task MonitorAsync(CancellationToken token)
    {
        while (true)
        {
            await gate.WaitAsync(token);
            try
            {
                if (!gameInProgress || gamePid is null)
                    throw new InvalidOperationException("No acknowledged game is available to monitor.");
                var reply = await Command("observe", token);
                if (reply.State == "backendReady")
                {
                    CompleteGameCleanup(reply);
                    return; // Keep the same backend/session for profile refresh and the next Play.
                }
                if (reply.State is not ("running" or "stopping"))
                    throw new InvalidOperationException("Unexpected runtime lifecycle state: " + reply.State);
                if (reply.State == "running")
                    VerifyManagedHost(reply, runningManagedHost, true);
                if (
                    reply.State == "running"
                    && (
                        reply.GamePid != gamePid
                        || !DateTimeOffset.TryParse(reply.GameCreatedUtc, out var created)
                        || created != gameCreated
                    )
                )
                    throw new InvalidOperationException("Runtime game process identity changed during monitoring.");
            }
            finally
            {
                gate.Release();
            }
            await Task.Delay(1500, token);
        }
    }

    public async Task ReleaseIfIdleAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (environmentChangeUncertain)
                throw new InvalidOperationException("Reload server settings to verify the previous change before exiting.");
            if (sessionId is null)
                return;
            if (gameInProgress)
            {
                await Command("releaseWhenIdle", token);
                return;
            }
            if (lastState != "backendReady")
            {
                RuntimeReply? observed = null;
                try
                {
                    observed = await Command("observe", token);
                }
                catch (InvalidOperationException) when (!token.IsCancellationRequested)
                {
                    // The original session still owns reconciliation. A release
                    // must independently return its verified cleanup receipt.
                }
                if (observed is { State: "cleaned", CleanupVerified: true, GameCleanupVerified: true })
                {
                    sessionId = null;
                    sessionBackendUrl = BoundBackendUrl = VerifiedBackendTlsSha256 = requestedBackendTlsSha256 = null;
                    EnvironmentAvailable = true;
                    lastState = null;
                    gamePid = null;
                    environmentSnapshot = null;
                    ManagedHostCapability = null;
                    return;
                }
            }
            await ReleaseCoreAsync(token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RuntimeReply> StopAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (sessionId is null)
                throw new InvalidOperationException("No owned runtime session to stop.");
            if (environmentChangeUncertain)
                throw new InvalidOperationException("Reload server settings to reconcile the pending change before stopping the session.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var reply = await Command("stop", deadline.Token);
            while (reply.State == "stopping")
            {
                await Task.Delay(1000, deadline.Token);
                reply = await Command("observe", deadline.Token);
            }
            if (reply.State == "backendReady")
                CompleteGameCleanup(reply);
            else
                throw new InvalidOperationException("Runtime did not acknowledge the stop request.");
            return reply;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out verifying game cleanup. Recovery is still required; inspect runtime session logs before retrying recovery."
            );
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (gameInProgress)
                throw new InvalidOperationException("Game cleanup is still pending; cannot disconnect the owned backend.");
            if (environmentChangeUncertain)
                throw new InvalidOperationException("A server settings change is unverified; reload settings before disconnecting.");
            if (sessionId is not null)
                await ReleaseCoreAsync(token);
        }
        finally
        {
            gate.Release();
        }
    }

    private void CompleteGameCleanup(RuntimeReply reply)
    {
        if (!reply.GameCleanupVerified)
            throw new InvalidOperationException("Runtime game cleanup is unverified; another game must not start.");
        gameInProgress = false;
        runningManagedHost = null;
        preflightManagedHost = null;
        gamePid = null;
        idleLeaseLost = false;
    }

    private async Task ReleaseCoreAsync(CancellationToken token)
    {
        var reply = await Command("release", token);
        if (reply.State != "cleaned" || !reply.CleanupVerified)
            throw new InvalidOperationException("Runtime has not released the session safely.");
        sessionId = null;
        sessionBackendUrl = BoundBackendUrl = VerifiedBackendTlsSha256 = requestedBackendTlsSha256 = null;
        EnvironmentAvailable = true;
        lastState = null;
        gamePid = null;
        environmentSnapshot = null;
        ManagedHostCapability = null;
        preflightManagedHost = null;
        runningManagedHost = null;
    }

    public async Task<EnvironmentSnapshot> ReadEnvironmentAsync(CancellationToken token)
    {
        if (IsExternalBackend || !EnvironmentAvailable)
            throw new InvalidOperationException("Server settings are managed on the server computer.");
        await gate.WaitAsync(token);
        try
        {
            if (sessionId is null || gameInProgress || idleLeaseLost)
                throw new InvalidOperationException("Connect to the local server and finish game cleanup before loading settings.");
            environmentChangeUncertain = true;
            lastState = null;
            return await EnvironmentCommandAsync("readEnvironment", null, null, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EnvironmentSnapshot> ApplyEnvironmentAsync(
        EnvironmentSelection settings,
        string expectedRevision,
        CancellationToken token
    )
    {
        if (IsExternalBackend || !EnvironmentAvailable)
            throw new InvalidOperationException("Server settings are managed on the server computer.");
        await gate.WaitAsync(token);
        try
        {
            RequireIdleBackend();
            if (environmentSnapshot is null || expectedRevision != environmentSnapshot.Revision)
                throw new InvalidOperationException("Reload server settings before applying this draft.");
            settings.Validate(environmentSnapshot, DateTimeOffset.UtcNow);
            environmentChangeUncertain = true;
            lastState = null;
            return await EnvironmentCommandAsync("applyEnvironment", settings, expectedRevision, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EnvironmentSnapshot> EnvironmentCommandAsync(
        string operation,
        EnvironmentSelection? settings,
        string? revision,
        CancellationToken token
    )
    {
        ManagedHostCapability = null;
        managedPreflightSession = null;
        managedPreflightDirectory = null;
        var request = new RuntimeRequest(
            1,
            Guid.NewGuid().ToString("N"),
            operation,
            sessionId,
            Settings: settings,
            ExpectedRevision: revision
        );
        // A lost apply reply is not permission to retry, launch, or disconnect. Only an authoritative read clears it.
        var reply = await transport.SendAsync(request, token);
        if (reply.Protocol != 1 || reply.RequestId != request.RequestId || reply.SessionId != sessionId)
            throw new InvalidOperationException("Server settings reply identity mismatch. Reload settings before continuing.");
        if (
            VerifiedBackendTlsSha256 is not null
            && reply.BackendTlsSha256 is not null
            && reply.BackendTlsSha256 != VerifiedBackendTlsSha256
        )
            throw new InvalidOperationException("The server certificate changed. Disconnect and connect again to review it.");
        if (
            reply.State != "backendReady"
            || reply.BackendUrl != (sessionBackendUrl ?? BackendUrl)
            || reply.CleanupVerified
            || reply.Settings is null
        )
        {
            if (operation == "applyEnvironment" || environmentChangeUncertain)
                lastState = null;
            throw new InvalidOperationException(
                reply.Error ?? "The matching runtime could not verify server settings. Reload settings to reconcile the session."
            );
        }
        UpdateManagedHostCapability(reply);
        reply.Settings.Validate();
        if (operation == "applyEnvironment" && reply.Error is null && reply.Settings.Selection != settings)
            throw new InvalidOperationException("The server did not confirm the requested settings. Reload settings to verify the result.");
        environmentSnapshot = reply.Settings;
        environmentChangeUncertain = false;
        lastState = "backendReady";
        if (reply.Error is not null)
            throw new EnvironmentSettingsException(reply.Error, reply.Settings);
        return reply.Settings;
    }
}

public record HolidayEventOption(string Id, string Label);

public record WeatherSeasonOption(int Value, string Label);

public record EnvironmentSelection(string HolidayEventId, DateOnly? HolidayEndDate, DateOnly ProgressionEndDate, int? WeatherOverrideSeason)
{
    public void Validate(EnvironmentSnapshot current, DateTimeOffset now)
    {
        current.Validate();
        if (!current.HolidayEvents.Any(e => e.Id == HolidayEventId))
            throw new InvalidOperationException("Choose a holiday event supported by this server.");
        if (HolidayEventId == "none" ? HolidayEndDate is not null : HolidayEndDate is null)
            throw new InvalidOperationException("Choose an end date for the holiday, or select No event.");
        if (WeatherOverrideSeason is not null && !current.WeatherSeasons.Any(s => s.Value == WeatherOverrideSeason))
            throw new InvalidOperationException("Choose a weather season supported by this server.");
        static bool Expired(DateOnly date, string zone, string clock, DateTimeOffset instant, bool inclusive)
        {
            var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone));
            var today = DateOnly.FromDateTime(local.DateTime);
            var time = TimeOnly.FromDateTime(local.DateTime);
            var cutoff = TimeOnly.ParseExact(clock, "HH:mm:ss", CultureInfo.InvariantCulture);
            return date < today || (date == today && (inclusive ? time > cutoff : time >= cutoff));
        }
        if (HolidayEndDate is not null && Expired(HolidayEndDate.Value, current.ServerTimeZone, current.HolidayEndTime, now, true))
            throw new InvalidOperationException("Choose a holiday end date whose server cutoff has not passed.");
        if (
            ProgressionEndDate == default
            || (
                ProgressionEndDate != current.ProgressionEndDate
                && Expired(ProgressionEndDate, current.ProgressionTimeZone, current.ProgressionEndTime, now, false)
            )
        )
            throw new InvalidOperationException("Choose a progression deadline whose cutoff has not passed.");
    }
}

public record EnvironmentSnapshot(
    int SchemaVersion,
    string Revision,
    string ServerTimeZone,
    IReadOnlyList<HolidayEventOption> HolidayEvents,
    IReadOnlyList<WeatherSeasonOption> WeatherSeasons,
    string HolidayEventId,
    DateOnly? HolidayEndDate,
    DateOnly ProgressionEndDate,
    int? WeatherOverrideSeason,
    string ProgressionTimeZone,
    string HolidayEndTime,
    string ProgressionEndTime
)
{
    [JsonIgnore]
    public EnvironmentSelection Selection => new(HolidayEventId, HolidayEndDate, ProgressionEndDate, WeatherOverrideSeason);

    public void Validate()
    {
        if (
            SchemaVersion != 1
            || string.IsNullOrWhiteSpace(Revision)
            || !Regex.IsMatch(Revision, "\\A[a-f0-9]{64}\\z")
            || string.IsNullOrWhiteSpace(ServerTimeZone)
            || string.IsNullOrWhiteSpace(ProgressionTimeZone)
            || HolidayEndTime != "23:59:00"
            || ProgressionEndTime != "01:00:00"
            || HolidayEvents is null
            || WeatherSeasons is null
            || HolidayEvents.Count is < 1 or > 32
            || WeatherSeasons.Count is < 1 or > 7
            || HolidayEvents.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Label))
            || HolidayEvents.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != HolidayEvents.Count
            || !HolidayEvents.Any(e => e.Id == "none")
            || !HolidayEvents.Any(e => e.Id == HolidayEventId)
            || WeatherSeasons.Any(s => s is null || s.Value is < 0 or > 6 || string.IsNullOrWhiteSpace(s.Label))
            || WeatherSeasons.Select(s => s.Value).Distinct().Count() != WeatherSeasons.Count
            || (WeatherOverrideSeason is not null && !WeatherSeasons.Any(s => s.Value == WeatherOverrideSeason))
            || (HolidayEventId == "none" ? HolidayEndDate is not null : HolidayEndDate is null)
            || ProgressionEndDate == default
        )
            throw new InvalidOperationException("The server returned invalid event or season settings.");
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(ServerTimeZone);
            _ = TimeZoneInfo.FindSystemTimeZoneById(ProgressionTimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InvalidOperationException("The server settings time zone is unsupported.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new InvalidOperationException("The server settings time zone is invalid.");
        }
    }
}

public sealed class EnvironmentSettingsException(string message, EnvironmentSnapshot currentSettings) : InvalidOperationException(message)
{
    public EnvironmentSnapshot CurrentSettings { get; } = currentSettings;
}
