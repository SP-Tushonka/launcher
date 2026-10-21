using SPTarkov.Core.Configuration;
using SPTarkov.Core.Runtime;
using SPTarkov.Launcher.Connections;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Service intent fixture exercises the Windows branch.");
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
    checks++;
}
async Task Reject(Func<Task> operation)
{
    try
    {
        await operation();
    }
    catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException("Expected operation to be rejected");
}
var config = new ConfigHelper();
var runtime = new OfflineRuntime();
var service = new ConnectionRestrictionService(config, runtime);
Check(!config.Required, "New installation intent defaults off");
GameConnectionBroker.Enabled = false;
await service.ReadAsync();
Check(!config.Required, "Observed off preserves default off");
config.Required = true;
await service.ReadAsync();
Check(config.Required, "Unexpected missing filters never clear protected launch intent");
GameConnectionBroker.Enabled = true;
config.Required = false;
await service.ReadAsync();
Check(config.Required, "Discovering verified installed filters restores required intent");
await service.SetEnabledAsync(true);
Check(config.Required, "Verified explicit enable records required intent");
await Reject(() => service.SetEnabledAsync(false));
Check(config.Required, "Unconfirmed disable cannot clear required intent");
GameConnectionBroker.Enabled = false;
await service.SetEnabledAsync(false);
Check(!config.Required, "Verified explicit disable clears intent");
config.Required = true;
GameConnectionBroker.QueryError = new InvalidOperationException("unverified broker");
await Reject(() => service.ReadAsync());
Check(config.Required, "Read error preserves required intent");
GameConnectionBroker.QueryError = null;
runtime.RecoveryRequired = true;
var before = GameConnectionBroker.Queries;
await Reject(() => service.SetEnabledAsync(false));
Check(config.Required && GameConnectionBroker.Queries == before, "Active recovery rejects removal before IPC and preserves intent");
runtime.RecoveryRequired = false;
GameConnectionBroker.QueryError = new TimeoutException("broker absent");
GameConnectionBroker.ApprovalError = new OperationCanceledException();
await Reject(() => service.VerifyWithApprovalAsync());
Check(config.Required, "Canceled read approval preserves required intent");
GameConnectionBroker.ApprovalError = null;
await service.VerifyWithApprovalAsync();
Check(config.Required && GameConnectionBroker.Approvals == 2, "Verified read-only off after approval cannot clear required intent");
Console.WriteLine($"{checks} service intent checks passed; all broker/settings/session interactions are in-memory fakes.");
