using SPTarkov.Core.Connections;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    checks++;
}

var service = new FakeService();
var ui = new ConnectionRestrictionController(service);
Check(!ui.Verified && !ui.Available, "Unqueried state must not be actionable");
await ui.ToggleAsync();
Check(service.Changes == 0, "Cannot toggle before verified state");
await ui.RefreshAsync();
Check(ui.Verified && !ui.Enabled && ui.Available, "Initial state is observed");
await ui.ToggleAsync();
Check(ui.Enabled && service.Installed, "Successful install displays verified enabled state");
service.ChangeError = new OperationCanceledException();
await ui.ToggleAsync();
Check(ui.Enabled && ui.Verified && ui.Message!.Contains("canceled"), "Rejected removal retains observed installed state");
service.ChangeError = null;
await ui.ToggleAsync();
Check(!ui.Enabled, "Successful removal updates switch");
service.ChangeError = new OperationCanceledException();
await ui.ToggleAsync();
Check(!ui.Enabled && ui.Verified, "Rejected installation retains observed disabled state");
service.ChangeError = new InvalidOperationException("failure after commit");
service.ChangeBeforeError = true;
await ui.ToggleAsync();
Check(ui.Enabled && ui.Verified, "Failure after commit re-reads actual changed state");
service.ReadError = new IOException("query failed");
await ui.ToggleAsync();
Check(!ui.Verified && !ui.Available && ui.Enabled, "Failed reconciliation disables uncertain switch and retains last known state");
await ui.ToggleAsync();
Check(service.Changes == 6, "Unverified state blocks additional mutation");
service.ReadError = null;
service.ChangeError = null;
await ui.RefreshAsync();
Check(ui.Verified && !ui.Enabled, "Retry reconciles actual state");
service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var pending = ui.ToggleAsync();
Check(ui.Busy && !ui.Enabled, "Busy operation does not optimistically switch");
await ui.ToggleAsync();
Check(service.Changes == 7, "Duplicate click cannot trigger second elevated operation");
service.Gate.SetResult();
await pending;
Check(ui.Enabled && !ui.Busy, "Pending operation verifies result before switch changes");
service.Supported = false;
await ui.RefreshAsync();
Check(!ui.Available, "Unsupported platform disables switch");
service.Supported = true;
service.ReadError = new UnauthorizedAccessException("Administrator approval is required to query filters.");
await ui.RefreshAsync();
Check(!ui.Verified && ui.Enabled, "Unprivileged query failure does not imply filters disabled");
service.ApprovalError = new OperationCanceledException();
await ui.VerifyWithApprovalAsync();
Check(
    !ui.Verified && ui.Enabled && ui.Message!.Contains("canceled"),
    "Canceled read-only elevation retains last known state and marks unknown"
);
service.ApprovalError = null;
service.ReadError = null;
await ui.VerifyWithApprovalAsync();
Check(ui.Verified && ui.Enabled && service.Approvals == 2, "Explicit approved read verifies installed state without changing it");
Check(service.Changes == 7, "Read-only approval never mutates filters");
Console.WriteLine($"{checks} UI state checks passed.");

sealed class FakeService : IConnectionRestrictionService
{
    public bool Installed,
        Supported = true,
        ChangeBeforeError;
    public int Changes,
        Approvals;
    public Exception? ChangeError,
        ReadError,
        ApprovalError;
    public TaskCompletionSource? Gate;

    public Task<ConnectionRestrictionState> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (ReadError is not null)
            throw ReadError;
        return Task.FromResult(new ConnectionRestrictionState(Installed, Supported));
    }

    public Task<ConnectionRestrictionState> VerifyWithApprovalAsync(CancellationToken cancellationToken = default)
    {
        Approvals++;
        if (ApprovalError is not null)
            throw ApprovalError;
        return ReadAsync(cancellationToken);
    }

    public async Task<ConnectionRestrictionState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Changes++;
        if (Gate is not null)
            await Gate.Task;
        if (ChangeBeforeError)
            Installed = enabled;
        if (ChangeError is not null)
            throw ChangeError;
        Installed = enabled;
        return await ReadAsync(cancellationToken);
    }
}
