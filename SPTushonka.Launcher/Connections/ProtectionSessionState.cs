namespace SPTarkov.Launcher.Connections;

internal sealed record ProtectionSnapshot(bool Enabled, int OwnedFilterCount);

/// <summary>Immutable session ownership. Unexpected termination never removes persistent filters.</summary>
internal sealed class ProtectionSessionState
{
    internal string SessionId { get; }
    internal string GameExecutable { get; }
    internal string LifecycleState { get; private set; } = "bound";
    internal bool ShutdownRequested => LifecycleState == "stopped";
    private bool ownsInstallation;
    private readonly Func<ProtectionSnapshot> read;
    private readonly Action install;
    private readonly Action remove;

    internal ProtectionSessionState(string sessionId, string gameExecutable, Func<ProtectionSnapshot> read, Action install, Action remove)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("A GUID-N protection session ID is required.");
        if (
            !Path.IsPathFullyQualified(gameExecutable)
            || !string.Equals(Path.GetFileName(gameExecutable), "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException("An absolute EscapeFromTarkov.exe path is required.");
        SessionId = sessionId;
        GameExecutable = Path.GetFullPath(gameExecutable);
        this.read = read;
        this.install = install;
        this.remove = remove;
    }

    internal ProtectionSnapshot Execute(string operation, string? sessionId, string gameExecutable)
    {
        if (!string.Equals(Path.GetFullPath(gameExecutable), GameExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Protection session is bound to a different game executable.");
        if (operation == "set")
            throw new InvalidOperationException("Generic mutation is disabled while a protection session owns the broker.");
        if (operation == "query")
        {
            if (sessionId is not null && sessionId != SessionId)
                throw new InvalidOperationException("Protection session identity mismatch.");
            return VerifyActive();
        }
        if (sessionId != SessionId)
            throw new InvalidOperationException("Protection session identity mismatch.");
        if (operation == "session-start")
        {
            if (LifecycleState == "active")
                return VerifyActive();
            if (LifecycleState != "bound")
                throw new InvalidOperationException(
                    "Protection session is not available for initialization; recover protection explicitly."
                );
            var before = read();
            if (before.OwnedFilterCount != 0)
                throw new InvalidOperationException(
                    "Protection session unavailable; restore isolation and recover protection. Existing filters were not adopted or removed."
                );
            try
            {
                install();
                ownsInstallation = true;
                var verified = ReadActive();
                LifecycleState = "active";
                return verified;
            }
            catch
            {
                LifecycleState = "faulted";
                throw;
            }
        }
        if (operation == "session-verify")
            return VerifyActive();
        if (operation == "session-stop")
        {
            if (LifecycleState == "stopped")
                return new(false, 0);
            if (!ownsInstallation)
                throw new InvalidOperationException("This session has no verified installation to remove; recover protection explicitly.");
            // The adapter holds the actual launch/mutation gate and refuses live games.
            remove();
            var after = read();
            if (after.OwnedFilterCount != 0)
                throw new InvalidOperationException("Protection removal is unverified; the session remains owned.");
            LifecycleState = "stopped";
            return new(false, 0);
        }
        throw new InvalidOperationException("Unknown protection session operation.");
    }

    private ProtectionSnapshot VerifyActive()
    {
        if (LifecycleState != "active")
            throw new InvalidOperationException("Protection session is not active; initialize or recover it before launching.");
        return ReadActive();
    }

    private ProtectionSnapshot ReadActive()
    {
        var snapshot = read();
        if (!snapshot.Enabled || snapshot.OwnedFilterCount != SPT.GameConnections.ConnectionPolicy.ExpectedFilterCount)
            throw new InvalidOperationException("Protection session policy is not verified; restore isolation and recover protection.");
        return snapshot;
    }
}
