// Compile-only boundary: any attempt to call filtering causes this fixture to fail.
namespace SPT.GameConnections;

public sealed record ConnectionVerification(bool IsEnabled, string Detail, int OwnedFilterCount);

public sealed class ConnectionPolicy
{
    public const string PolicyRevision = "spt-host-tcp-v2";
    public const int ExpectedFilterCount = 4;
    public string GameExecutable => throw new InvalidOperationException("Fixture must not create a game policy.");

    public static ConnectionPolicy ForSupportedRuntime(string gameExecutable) =>
        throw new InvalidOperationException("Fixture must not create a game policy.");
}

public sealed class WfpConnectionFilter
{
    public void Install(ConnectionPolicy policy) => throw new InvalidOperationException("Fixture must not install filters.");

    public void Remove() => throw new InvalidOperationException("Fixture must not remove filters.");

    public ConnectionVerification Verify(ConnectionPolicy policy) => throw new InvalidOperationException("Fixture must not read WFP.");

    public ConnectionVerification Inspect() => throw new InvalidOperationException("Fixture must not read WFP.");
}
