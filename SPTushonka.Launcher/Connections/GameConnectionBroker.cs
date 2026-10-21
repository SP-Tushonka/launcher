using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SPT.GameConnections;

namespace SPTarkov.Launcher.Connections;

/// <summary>Elevated, local-only mode of SPT.Launcher.exe. No service, driver or TCP control port.</summary>
public static class GameConnectionBroker
{
    private const int MaximumMessage = 16384;
    private static readonly Lazy<string> StartupCodeIdentity = new(ComputeCodeIdentity);

    internal sealed record Request(
        int Protocol,
        string RequestId,
        string Operation,
        string GameExecutable,
        bool Enabled = false,
        string? CodeIdentity = null,
        string? SessionId = null
    );

    internal sealed record Reply(
        int Protocol,
        string RequestId,
        string GameExecutable,
        string PolicyRevision,
        bool Verified,
        bool Enabled,
        int OwnedFilterCount,
        string? Error = null,
        string? CodeIdentity = null,
        string? SessionId = null,
        int? BrokerPid = null,
        long? BrokerCreatedTicks = null,
        string? LifecycleState = null
    );

    internal sealed record Acknowledgement(int Protocol, string RequestId);

    public static void CaptureCodeIdentity() => _ = StartupCodeIdentity.Value;

    private static string ComputeCodeIdentity()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this launcher."));
        if (!string.Equals(Path.GetFileName(executable), "SPT.Launcher.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the built SPT.Launcher.exe to verify broker code identity.");
        var directory = Path.GetDirectoryName(executable)!;
        var paths = new[]
        {
            executable,
            Path.Combine(directory, "SPT.Launcher.dll"),
            Path.Combine(directory, "SPTarkov.Core.dll"),
            Path.Combine(directory, "SPT.GameConnections.dll"),
        };
        var identities = paths.Select(path =>
        {
            if (!File.Exists(path))
                return Path.GetFileName(path) + ":absent";
            using var stream = File.OpenRead(path);
            return Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(stream));
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", identities))));
    }

    private static string CurrentSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Game connection protection requires Windows.");
        return WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
    }

    private static string PipeName(string ownerSid)
    {
        var identity = Path.GetFullPath(Environment.ProcessPath!).ToUpperInvariant() + "|" + ownerSid;
        return "SPT.GameConnections." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    public static int Run(string ownerSid, string? sessionId = null, string? gameExecutable = null)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return 20;
        try
        {
            CaptureCodeIdentity();
            ownerSid = new SecurityIdentifier(ownerSid).Value;
            ProtectionSessionState? session = sessionId is null
                ? null
                : new(
                    sessionId,
                    ValidateGamePath(gameExecutable!),
                    () => ReadProtectionSnapshot(gameExecutable!),
                    () => GameConnectionHost.MutateSession(gameExecutable!, true),
                    () => GameConnectionHost.MutateSession(gameExecutable!, false)
                );
            RunAsync(ownerSid, session).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 20;
        }
    }

    private static async Task RunAsync(string ownerSid, ProtectionSessionState? session)
    {
        // The read/write ACL is for the requesting user, administrators, and SYSTEM only.
        // PIPE_REJECT_REMOTE_CLIENTS prevents remote named-pipe access even with matching credentials.
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{ownerSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        NamedPipeServerStream server;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(
                @"\\.\pipe\" + PipeName(ownerSid),
                0x40000003 | 0x00080000,
                0x00000008,
                1,
                MaximumMessage,
                MaximumMessage,
                5000,
                ref attributes
            );
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            server = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
        using (server)
        {
            // The singleton pipe is owned before policy creation. A failed initialization
            // unwinds Run and closes this broker instead of leaving an unowned worker.
            if (session is not null)
                session.Execute("session-start", session.SessionId, session.GameExecutable);
            while (true)
            {
                using var idle = new CancellationTokenSource();
                if (session is null)
                    idle.CancelAfter(TimeSpan.FromMinutes(60));
                try
                {
                    await server.WaitForConnectionAsync(idle.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    using var clientProcess = OpenTrustedPeer(clientPid);
                    var request = await ReadAsync<Request>(server, deadline.Token);
                    if (
                        !GetExitCodeProcess(clientProcess, out var clientExit)
                        || clientExit != 259
                        || !GetNamedPipeClientProcessId(server.SafePipeHandle, out var finalClientPid)
                        || finalClientPid != clientPid
                    )
                        throw new InvalidOperationException("The connection protection client identity changed.");
                    Reply reply;
                    try
                    {
                        ValidateRequest(request, StartupCodeIdentity.Value);
                        var requestedPath = ValidateGamePath(request.GameExecutable);
                        if (session is not null)
                        {
                            var observed = session.Execute(request.Operation, request.SessionId, requestedPath);
                            reply = new(
                                1,
                                request.RequestId,
                                requestedPath,
                                GameConnectionHost.PolicyRevision,
                                true,
                                observed.Enabled,
                                observed.OwnedFilterCount,
                                CodeIdentity: StartupCodeIdentity.Value,
                                SessionId: session.SessionId,
                                BrokerPid: Environment.ProcessId,
                                BrokerCreatedTicks: Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                                LifecycleState: session.LifecycleState
                            );
                            await WriteReplyAsync(server, reply, deadline.Token);
                            if (session.ShutdownRequested)
                                return;
                            continue;
                        }
                        if (request.SessionId is not null || request.Operation.StartsWith("session-", StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "A legacy GUI broker cannot be adopted as a protection session. Close it explicitly before initialization."
                            );
                        if (request.Operation == "set")
                            GameConnectionHost.Mutate(requestedPath, request.Enabled);
                        var filter = new WfpConnectionFilter();
                        var owned = filter.Inspect();
                        if (owned.OwnedFilterCount == 0)
                        {
                            var fullPath = ValidateGamePath(request.GameExecutable);
                            reply = new(
                                1,
                                request.RequestId,
                                fullPath,
                                GameConnectionHost.PolicyRevision,
                                true,
                                false,
                                0,
                                CodeIdentity: StartupCodeIdentity.Value
                            );
                            await WriteReplyAsync(server, reply, deadline.Token);
                            continue;
                        }
                        var policy = ConnectionPolicy.ForSupportedRuntime(request.GameExecutable);
                        var state = filter.Verify(policy);
                        if (!state.IsEnabled && owned.OwnedFilterCount != 0)
                            throw new InvalidOperationException(state.Detail);
                        reply = new(
                            1,
                            request.RequestId,
                            policy.GameExecutable,
                            GameConnectionHost.PolicyRevision,
                            true,
                            state.IsEnabled,
                            owned.OwnedFilterCount,
                            CodeIdentity: StartupCodeIdentity.Value
                        );
                    }
                    catch (Exception ex)
                    {
                        if (session?.ShutdownRequested is true)
                            return;
                        reply = new(
                            1,
                            request.RequestId,
                            request.GameExecutable,
                            GameConnectionHost.PolicyRevision,
                            false,
                            false,
                            0,
                            ex.Message,
                            StartupCodeIdentity.Value,
                            session?.SessionId,
                            Environment.ProcessId,
                            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                            session?.LifecycleState
                        );
                    }
                    await WriteReplyAsync(server, reply, deadline.Token);
                }
                catch (Exception ex)
                    when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or Win32Exception)
                {
                    // A dropped or malformed client has no effect on the installed persistent policy.
                    if (session?.ShutdownRequested is true)
                        return;
                }
                finally
                {
                    if (server.IsConnected)
                        server.Disconnect();
                }
            }
        }
    }

    internal static async Task<Reply> QueryAsync(string executable, bool? enabled = null, CancellationToken token = default)
    {
        var expectedCodeIdentity = StartupCodeIdentity.Value;
        // Older brokers ignore unknown request fields. Verify their code-bearing
        // read-only reply before any mutation is sent to them.
        if (enabled is not null)
            await QueryAsync(executable, null, token);
        return await ExchangeAsync(
            executable,
            enabled is null ? "query" : "set",
            enabled ?? false,
            null,
            Guid.NewGuid().ToString("N"),
            token
        );
    }

    internal static Task<Reply> SessionAsync(
        string executable,
        string sessionId,
        string operation,
        string requestId,
        CancellationToken token = default
    ) => ExchangeAsync(executable, operation, false, sessionId, requestId, token);

    private static async Task<Reply> ExchangeAsync(
        string executable,
        string operation,
        bool enabled,
        string? sessionId,
        string nonce,
        CancellationToken token
    )
    {
        var expectedCodeIdentity = StartupCodeIdentity.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var pipe = new NamedPipeClientStream(".", PipeName(CurrentSid()), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var serverProcess = OpenTrustedPeer(pid);
        var fullPath = Path.GetFullPath(executable);
        await WriteAsync(pipe, new Request(1, nonce, operation, fullPath, enabled, expectedCodeIdentity, sessionId), timeout.Token);
        var reply = await ReadAsync<Reply>(pipe, timeout.Token);
        if (!GetExitCodeProcess(serverProcess, out var exit))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Connection protection broker process identity could not be inspected.");
        if (exit != 259)
            throw new InvalidOperationException("Connection protection broker exited during the response.");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var finalPid))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Connection protection pipe identity could not be inspected after the response."
            );
        if (finalPid != pid)
            throw new InvalidOperationException("Connection protection pipe server process changed.");
        ValidateReply(reply, nonce, fullPath, expectedCodeIdentity);
        if (sessionId is not null)
        {
            if (reply.SessionId != sessionId || reply.BrokerPid != checked((int)pid) || reply.BrokerCreatedTicks is not > 0)
                throw new InvalidOperationException("Protection session broker identity mismatch; no session was adopted.");
            if (
                !GetProcessTimes(serverProcess, out var created, out _, out _, out _)
                || DateTime.FromFileTimeUtc(unchecked((long)created)).Ticks != reply.BrokerCreatedTicks
            )
                throw new InvalidOperationException("Protection session broker creation identity mismatch.");
            if (operation == "session-stop" ? reply.LifecycleState != "stopped" : reply.LifecycleState != "active")
                throw new InvalidOperationException("Protection session lifecycle acknowledgement mismatch.");
        }
        // Keep the server connected through native peer and correlated code checks.
        // A one-way response allowed its Disconnect to race the final PID query.
        await WriteAsync(pipe, new Acknowledgement(1, nonce), timeout.Token);
        if (operation == "session-stop")
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                if (!GetExitCodeProcess(serverProcess, out var status))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the owned broker exit.");
                if (status != 259)
                    break;
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("Protection was removed but the owned session broker has not exited.");
                await Task.Delay(50, timeout.Token);
            }
        }
        return reply;
    }

    internal static void ValidateReply(Reply reply, string nonce, string gameExecutable, string expectedCodeIdentity)
    {
        if (reply.Protocol != 1)
            throw new InvalidOperationException("Connection protection response protocol mismatch.");
        if (reply.RequestId != nonce)
            throw new InvalidOperationException("Connection protection response request identity mismatch.");
        if (reply.PolicyRevision != GameConnectionHost.PolicyRevision)
            throw new InvalidOperationException("Connection protection response policy revision mismatch.");
        if (!string.Equals(reply.GameExecutable, gameExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connection protection response game executable mismatch.");
        if (string.IsNullOrEmpty(expectedCodeIdentity) || reply.CodeIdentity != expectedCodeIdentity)
            throw new InvalidOperationException("Connection protection broker code identity mismatch.");
        if (!reply.Verified)
            throw new InvalidOperationException(reply.Error ?? "Windows could not verify connection protection.");
        if (
            reply.Error is not null
            || (reply.Enabled ? reply.OwnedFilterCount != ConnectionPolicy.ExpectedFilterCount : reply.OwnedFilterCount != 0)
        )
            throw new InvalidOperationException("Connection protection response state is inconsistent.");
    }

    internal static void ValidateRequest(Request request, string expectedCodeIdentity)
    {
        if (
            request.Protocol != 1
            || !Guid.TryParseExact(request.RequestId, "N", out _)
            || string.IsNullOrEmpty(expectedCodeIdentity)
            || request.CodeIdentity != expectedCodeIdentity
        )
            throw new InvalidOperationException("Invalid connection protection request or code identity.");
        if (request.Operation is "session-start" or "session-verify" or "session-stop")
        {
            if (!Guid.TryParseExact(request.SessionId, "N", out _))
                throw new InvalidOperationException("A protection session identity is required.");
        }
        else if (request.Operation != "query" && request.Operation != "set")
            throw new InvalidOperationException("Unknown connection protection operation.");
    }

    private static ProtectionSnapshot ReadProtectionSnapshot(string gameExecutable)
    {
        var filter = new WfpConnectionFilter();
        var owned = filter.Inspect();
        if (owned.OwnedFilterCount == 0)
            return new(false, 0);
        var verified = filter.Verify(ConnectionPolicy.ForSupportedRuntime(gameExecutable));
        return new(verified.IsEnabled, owned.OwnedFilterCount);
    }

    internal static bool SessionBrokerIsAbsent()
    {
        if (WaitNamedPipe(@"\\.\pipe\" + PipeName(CurrentSid()), 1))
            return false;
        return Marshal.GetLastWin32Error() == 2; // Only FILE_NOT_FOUND permits one new elevation.
    }

    internal static async Task StartSessionWithApprovalAsync(string gameExecutable, string sessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this launcher.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (
            var value in new[]
            {
                "--protection-session-broker",
                "--owner-sid",
                CurrentSid(),
                "--game-path",
                gameExecutable,
                "--session-id",
                sessionId,
            }
        )
            start.ArgumentList.Add(value);
        try
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The protection session broker could not start.");
            await Task.Delay(100, token);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator approval was canceled.", ex, token);
        }
    }

    internal static void ValidateAcknowledgement(Acknowledgement acknowledgement, string nonce)
    {
        if (acknowledgement.Protocol != 1 || acknowledgement.RequestId != nonce)
            throw new InvalidOperationException("Connection protection response acknowledgement mismatch.");
    }

    private static async Task WriteReplyAsync(Stream stream, Reply reply, CancellationToken token)
    {
        await WriteAsync(stream, reply, token);
        var acknowledgement = await ReadAsync<Acknowledgement>(stream, token);
        ValidateAcknowledgement(acknowledgement, reply.RequestId);
    }

    private static string ValidateGamePath(string path)
    {
        if (
            !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFileName(path), "EscapeFromTarkov.exe", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException("An absolute EscapeFromTarkov.exe path is required.");
        return Path.GetFullPath(path);
    }

    private static SafeProcessHandle OpenTrustedPeer(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        try
        {
            if (process.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            if (
                !QueryFullProcessImageName(process, 0, path, ref length)
                || !string.Equals(
                    Path.GetFullPath(path.ToString()),
                    Path.GetFullPath(Environment.ProcessPath!),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidOperationException("The connection protection peer is not this launcher executable.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static async Task StartWithApprovalAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this launcher.");
        if (!string.Equals(Path.GetFileName(executable), "SPT.Launcher.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run SPT.Launcher.exe to request administrator approval.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--game-connections-broker");
        start.ArgumentList.Add("--owner-sid");
        start.ArgumentList.Add(CurrentSid());
        try
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The protection broker could not start.");
            // Do not wait for its lifetime; the caller retries the bounded pipe connection.
            await Task.Delay(300, token);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator approval was canceled.", ex, token);
        }
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumMessage)
            throw new IOException("Connection protection message exceeds its limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size <= 0 || size > MaximumMessage)
            throw new IOException("Invalid connection protection message length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new JsonException("Empty connection protection message.");
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
        ExactSpelling = true,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string text,
        uint revision,
        out IntPtr descriptor,
        out uint size
    );

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint access,
        uint mode,
        uint instances,
        int outSize,
        int inSize,
        uint timeout,
        ref SecurityAttributes attributes
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out ulong created,
        out ulong exited,
        out ulong kernel,
        out ulong user
    );

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeout);
}
