using SPTarkov.Core.Configuration;
using SPTarkov.Core.Connections;
using SPTarkov.Core.Runtime;

namespace SPTarkov.Launcher.Connections;

public sealed class ConnectionRestrictionService(ConfigHelper configuration, OfflineRuntime runtime) : IConnectionRestrictionService
{
    private string GameExecutable => Path.GetFullPath(Path.Combine(configuration.GetGamePath(), "EscapeFromTarkov.exe"));

    public async Task<ConnectionRestrictionState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, "Game connection protection requires Windows.");
        try
        {
            var reply = await GameConnectionBroker.QueryAsync(GameExecutable, token: cancellationToken);
            // Observation must never silently clear a user's required-protection intent.
            // Only an explicit, successfully verified disable operation may clear it.
            if (reply.Enabled)
                configuration.SetRequireGameConnections(true);
            return new(reply.Enabled, true);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                "Administrator approval is needed to check connection protection. Choose Check protection.",
                ex
            );
        }
    }

    public async Task<ConnectionRestrictionState> VerifyWithApprovalAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, "Game connection protection requires Windows.");
        try
        {
            return await ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException || ex.InnerException is TimeoutException)
        {
            await GameConnectionBroker.StartWithApprovalAsync(cancellationToken);
            return await ReadAsync(cancellationToken);
        }
    }

    public async Task<ConnectionRestrictionState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new(false, false, "Game connection protection requires Windows.");
        if (runtime.RecoveryRequired)
            throw new InvalidOperationException("Finish or recover the game session before changing connection protection.");
        var reply = await GameConnectionBroker.QueryAsync(GameExecutable, enabled, cancellationToken);
        if (reply.Enabled != enabled)
            throw new InvalidOperationException("Windows did not confirm the requested protection state.");
        configuration.SetRequireGameConnections(reply.Enabled);
        return new(reply.Enabled, true);
    }
}
