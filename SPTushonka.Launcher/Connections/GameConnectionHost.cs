using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SPT.GameConnections;

namespace SPTarkov.Launcher.Connections;

/// <summary>Headless modes of the launcher itself; dispatch before GUI and single-instance setup.</summary>
public static class GameConnectionHost
{
    public const string PolicyRevision = ConnectionPolicy.PolicyRevision;
    public const string MutexName = @"Global\SPT.GameConnections.LaunchGate";

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (ProtectionSessionHost.TryHandle(args, out exitCode))
            return true;
        if (args.Length > 0 && args[0] == "--game-connections-broker")
        {
            exitCode = args.Length == 3 && args[1] == "--owner-sid" ? GameConnectionBroker.Run(args[2]) : 20;
            return true;
        }
        if (
            args.Length == 0
            || (args[0] != "--verify-game-connections" && args[0] != "--set-game-connections" && args[0] != "--request-game-connections")
        )
            return false;
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Game connection protection requires Windows.");
            var options = Parse(args);
            if (options.Enabled is bool enabled)
            {
                if (
                    args[0] != "--request-game-connections"
                    && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
                )
                {
                    RefuseSessionOwnedMutation(
                        GameConnectionBroker.SessionBrokerIsAbsent,
                        () => GameConnectionBroker.QueryAsync(options.GameExecutable).GetAwaiter().GetResult()
                    );
                    Mutate(options.GameExecutable, enabled);
                }
                else
                {
                    var state = GameConnectionBroker.QueryAsync(options.GameExecutable, enabled).GetAwaiter().GetResult();
                    if (state.Enabled != enabled)
                        throw new InvalidOperationException("Windows did not confirm the requested protection state.");
                }
            }
            else
            {
                var policy = ConnectionPolicy.ForSupportedRuntime(options.GameExecutable);
                // Runtime already holds the cross-process gate through its actual CreateProcess boundary.
                var verified = GameConnectionBroker.QueryAsync(policy.GameExecutable).GetAwaiter().GetResult();
                Console.WriteLine(SerializeVerificationReply(options.RequestId!, policy.GameExecutable, verified));
                exitCode = verified.Enabled ? 0 : 10;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 20;
        }
        return true;
    }

    internal sealed record Options(string GameExecutable, string? RequestId, bool? Enabled);

    internal static void RefuseSessionOwnedMutation(Func<bool> absent, Func<GameConnectionBroker.Reply> query)
    {
        if (absent())
            return;
        var reply = query(); // Authentication, code identity and bound game errors propagate.
        if (reply.SessionId is not null)
            throw new InvalidOperationException("The framework owns protection; stop its protection session before changing it.");
    }

    internal static string SerializeVerificationReply(string requestId, string gameExecutable, GameConnectionBroker.Reply verified) =>
        JsonSerializer.Serialize(
            new
            {
                protocol = 1,
                requestId,
                gameExecutable,
                policyRevision = PolicyRevision,
                enabled = verified.Enabled,
                ownedFilterCount = verified.OwnedFilterCount,
                codeIdentity = verified.CodeIdentity,
                detail = "Verified through the elevated launcher broker.",
            }
        );

    internal static void Mutate(string gameExecutable, bool enabled)
    {
        using var gate = EnterMutationGate();
        RefuseRunningGames();
        var filter = new WfpConnectionFilter();
        var policy = enabled ? ConnectionPolicy.ForSupportedRuntime(gameExecutable) : null;
        if (enabled)
            filter.Install(policy!);
        else
            filter.Remove();
        var verified = policy is not null ? filter.Verify(policy) : null;
        var remaining = filter.Inspect();
        if (enabled ? verified?.IsEnabled != true : remaining.OwnedFilterCount != 0)
            throw new InvalidOperationException("Windows did not confirm the requested connection protection state.");
    }

    internal static void MutateSession(string gameExecutable, bool enabled)
    {
        using var gate = EnterMutationGate();
        RefuseRunningGames();
        var filter = new WfpConnectionFilter();
        var before = filter.Inspect();
        if (enabled)
        {
            if (before.OwnedFilterCount != 0)
                throw new InvalidOperationException(
                    "Protection session unavailable; restore isolation and recover protection. Existing filters were not adopted or removed."
                );
            var policy = ConnectionPolicy.ForSupportedRuntime(gameExecutable);
            filter.Install(policy);
            if (!filter.Verify(policy).IsEnabled)
                throw new InvalidOperationException("Session protection installation is unverified; recover protection.");
        }
        else
        {
            if (before.OwnedFilterCount != 0)
            {
                if (!filter.Verify(ConnectionPolicy.ForSupportedRuntime(gameExecutable)).IsEnabled)
                    throw new InvalidOperationException("Session policy changed; refusing unrelated protection removal.");
                filter.Remove();
            }
            if (filter.Inspect().OwnedFilterCount != 0)
                throw new InvalidOperationException("Session protection removal is unverified.");
        }
    }

    internal static Options Parse(string[] args)
    {
        if (
            args.Length == 0
            || (args[0] != "--set-game-connections" && args[0] != "--verify-game-connections" && args[0] != "--request-game-connections")
        )
            throw new ArgumentException("Unknown game connection command.");
        var mutate = args[0] != "--verify-game-connections";
        if (args.Length != 5)
            throw new ArgumentException("Invalid game connection command arguments.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Duplicate game connection argument.");
        if (!values.TryGetValue("--game-path", out var path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute game executable path is required.");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(path), "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only EscapeFromTarkov.exe is supported.");
        if (mutate)
        {
            if (!values.TryGetValue("--enabled", out var value) || (value != "true" && value != "false"))
                throw new ArgumentException("The enabled argument must be true or false.");
            return new(path, null, value == "true");
        }
        if (!values.TryGetValue("--request-id", out var id) || !Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("A correlated verification request ID is required.");
        return new(path, id, null);
    }

    private static void RefuseRunningGames()
    {
        foreach (var process in Process.GetProcessesByName("EscapeFromTarkov"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        continue;
                    // Read identity rather than relying on a name or stale PID alone.
                    _ = process.MainModule?.FileName ?? throw new InvalidOperationException("Game identity is unavailable.");
                    _ = process.StartTime;
                    if (!process.HasExited)
                        throw new InvalidOperationException("Exit the game before changing connection protection.");
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Cannot verify that the game has stopped; connection protection was not changed.",
                        ex
                    );
                }
            }
        }
    }

    private static IDisposable EnterMutationGate()
    {
        const string sddl = "D:P(A;;0x00100001;;;SY)(A;;0x00100001;;;BA)(A;;0x00100001;;;AU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateMutexEx(ref attributes, MutexName, 0, 0x00100001);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var mutex = new Mutex(false);
            var old = mutex.SafeWaitHandle;
            mutex.SafeWaitHandle = new SafeWaitHandle(handle, true);
            old.Dispose();
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Game launch is busy; protection was not changed.");
                return new HeldMutex(mutex);
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
                throw new InvalidOperationException("The previous game launch gate was abandoned. Reconcile the runtime before retrying.");
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string text,
        uint revision,
        out IntPtr descriptor,
        out uint size
    );

    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
