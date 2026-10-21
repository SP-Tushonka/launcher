namespace SPTarkov.Core.Connections;

/// <summary>Keeps the switch tied to observed filter state, including rejected elevation.</summary>
public sealed class ConnectionRestrictionController(IConnectionRestrictionService service)
{
    private int _busy;
    public bool Enabled { get; private set; }
    public bool Available { get; private set; }
    public bool Verified { get; private set; }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public string? Message { get; private set; }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(false, cancellationToken);

    public Task VerifyWithApprovalAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(true, cancellationToken);

    private async Task RefreshCoreAsync(bool withApproval, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            Apply(withApproval ? await service.VerifyWithApprovalAsync(cancellationToken) : await service.ReadAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            Unknown("Administrator approval was canceled. Protection status has not been verified.");
        }
        catch (Exception ex)
        {
            Unknown($"Could not verify connection protection: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (!Verified || !Available || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            // SetEnabledAsync returns observed state, never merely the requested value.
            Apply(await service.SetEnabledAsync(!Enabled, cancellationToken));
        }
        catch (Exception ex)
        {
            string reason =
                ex is OperationCanceledException
                    ? "Administrator approval was canceled."
                    : $"Could not change connection protection: {ex.Message}";
            try
            {
                // Read again even if the operation token was canceled. A failed operation
                // may have changed filters before it reported the error.
                Apply(await service.ReadAsync(CancellationToken.None));
                Message = reason + (Message is null ? "" : " " + Message);
            }
            catch
            {
                Unknown(reason + " Current protection could not be verified. Retry verification before starting the game.");
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Apply(ConnectionRestrictionState state)
    {
        Enabled = state.Enabled;
        Available = state.Available;
        Verified = true;
        Message = state.Message;
    }

    private void Unknown(string message)
    {
        // Retain the last known switch position, explicitly mark it unverified and
        // disable interaction. Never display a failed query as "off" or "on".
        Verified = false;
        Available = false;
        Message = message;
    }
}
