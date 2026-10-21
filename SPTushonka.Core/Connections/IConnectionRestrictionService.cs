namespace SPTarkov.Core.Connections;

/// <summary>A verified snapshot of the filters for the currently selected game and server.</summary>
public sealed record ConnectionRestrictionState(bool Enabled, bool Available, string? Message = null);

public interface IConnectionRestrictionService
{
    Task<ConnectionRestrictionState> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Explicit user action: may request elevation to query verified state.</summary>
    Task<ConnectionRestrictionState> VerifyWithApprovalAsync(CancellationToken cancellationToken = default);
    Task<ConnectionRestrictionState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
