using System.Text.Json;

namespace SPTarkov.Launcher.Connections;

internal static class ProtectionSessionHost
{
    internal sealed record Options(string Operation, string GameExecutable, string SessionId, string RequestId);

    internal static Options Parse(string[] args)
    {
        if (
            args.Length != 7
            || args[0] is not ("--start-protection-session" or "--verify-protection-session" or "--stop-protection-session")
        )
            throw new ArgumentException("Expected a protection session command with --game-path, --session-id and --request-id.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (args[i] is not ("--game-path" or "--session-id" or "--request-id") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown or duplicate protection session argument.");
        var path = values["--game-path"];
        if (
            !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFileName(path), "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException("An absolute EscapeFromTarkov.exe path is required.");
        if (!Guid.TryParseExact(values["--session-id"], "N", out _) || !Guid.TryParseExact(values["--request-id"], "N", out _))
            throw new ArgumentException("Session and request identities must use GUID-N format.");
        return new(
            args[0].Replace("--", "").Replace("-protection-session", ""),
            Path.GetFullPath(path),
            values["--session-id"],
            values["--request-id"]
        );
    }

    // Only a native FILE_NOT_FOUND result authorizes the single elevation attempt.
    internal static async Task<GameConnectionBroker.Reply> InitializeAsync(
        Func<Task<GameConnectionBroker.Reply>> exchange,
        Func<bool> absent,
        Func<Task> elevate,
        Func<Task> readinessDelay,
        int readinessAttempts = 20
    )
    {
        try
        {
            return await exchange();
        }
        catch (TimeoutException) when (absent()) { }
        await elevate();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await exchange();
            }
            catch (TimeoutException) when (attempt < readinessAttempts)
            {
                await readinessDelay();
            }
        }
    }

    internal static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
            return false;
        if (args[0] == "--protection-session-broker")
        {
            if (args.Length != 7 || args[1] != "--owner-sid" || args[3] != "--game-path" || args[5] != "--session-id")
            {
                exitCode = 20;
                return true;
            }
            exitCode = GameConnectionBroker.Run(args[2], args[6], args[4]);
            return true;
        }
        if (args[0] is not ("--start-protection-session" or "--verify-protection-session" or "--stop-protection-session"))
            return false;
        Options? options = null;
        try
        {
            options = Parse(args);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Protection sessions require Windows.");
            var operation = "session-" + options.Operation;
            Task<GameConnectionBroker.Reply> Exchange() =>
                GameConnectionBroker.SessionAsync(options.GameExecutable, options.SessionId, operation, options.RequestId);
            var reply = (
                options.Operation == "start"
                    ? InitializeAsync(
                        Exchange,
                        GameConnectionBroker.SessionBrokerIsAbsent,
                        () => GameConnectionBroker.StartSessionWithApprovalAsync(options.GameExecutable, options.SessionId, default),
                        () => Task.Delay(100),
                        10
                    )
                    : Exchange()
            )
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        protocol = 1,
                        requestId = reply.RequestId,
                        sessionId = reply.SessionId,
                        gameExecutable = reply.GameExecutable,
                        policyRevision = reply.PolicyRevision,
                        verified = reply.Verified,
                        enabled = reply.Enabled,
                        ownedFilterCount = reply.OwnedFilterCount,
                        codeIdentity = reply.CodeIdentity,
                        brokerPid = reply.BrokerPid,
                        brokerCreatedTicks = reply.BrokerCreatedTicks,
                        lifecycleState = reply.LifecycleState,
                    }
                )
            );
        }
        catch (Exception ex)
        {
            exitCode = 20;
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        protocol = 1,
                        requestId = options?.RequestId,
                        sessionId = options?.SessionId,
                        verified = false,
                        lifecycleState = "unavailable",
                        error = "Protection session unavailable: " + ex.Message,
                    }
                )
            );
        }
        return true;
    }
}
