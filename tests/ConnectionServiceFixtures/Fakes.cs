namespace SPTarkov.Core.Configuration
{
    public sealed class ConfigHelper
    {
        public bool Required;

        public string GetGamePath() => Path.GetTempPath();

        public void SetRequireGameConnections(bool required) => Required = required;
    }
}

namespace SPTarkov.Core.Runtime
{
    public sealed class OfflineRuntime
    {
        public bool RecoveryRequired { get; set; }
    }
}

namespace SPTarkov.Launcher.Connections
{
    public static class GameConnectionBroker
    {
        public sealed record Reply(bool Enabled);

        public static bool Enabled;
        public static int Queries,
            Approvals;
        public static Exception? QueryError,
            ApprovalError;

        public static Task<Reply> QueryAsync(string executable, bool? enabled = null, CancellationToken token = default)
        {
            Queries++;
            if (QueryError is not null)
                throw QueryError;
            return Task.FromResult(new Reply(Enabled));
        }

        public static Task StartWithApprovalAsync(CancellationToken token)
        {
            Approvals++;
            if (ApprovalError is not null)
                throw ApprovalError;
            QueryError = null;
            return Task.CompletedTask;
        }
    }
}
