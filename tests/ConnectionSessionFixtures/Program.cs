using SPTarkov.Launcher.Connections;

public static class SessionFixtures
{
    public static async Task Main()
    {
        int n = 0;
        void Check(bool v)
        {
            if (!v)
                throw new Exception("check " + n);
            n++;
        }
        void Reject(Action a)
        {
            try
            {
                a();
            }
            catch (InvalidOperationException)
            {
                n++;
                return;
            }
            throw new Exception("expected rejection " + n);
        }
        var id = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var path = Path.GetFullPath("EscapeFromTarkov.exe");
        int count = 0,
            installs = 0,
            removes = 0;
        bool live = false;
        var state = new ProtectionSessionState(
            id,
            path,
            () => new(count == 4, count),
            () =>
            {
                installs++;
                count = 4;
            },
            () =>
            {
                if (live)
                    throw new InvalidOperationException("live");
                removes++;
                count = 0;
            }
        );
        Check(state.LifecycleState == "bound");
        Reject(() => state.Execute("session-verify", id, path));
        Check(state.Execute("session-start", id, path).Enabled);
        Check(installs == 1);
        Check(state.LifecycleState == "active");
        state.Execute("session-start", id, path);
        Check(installs == 1);
        Check(state.Execute("query", null, path).OwnedFilterCount == 4);
        Reject(() => state.Execute("set", null, path));
        Reject(() => state.Execute("session-stop", Guid.NewGuid().ToString("N"), path));
        Reject(() => state.Execute("query", null, Path.Combine(Path.GetDirectoryName(path)!, "other", "EscapeFromTarkov.exe")));
        live = true;
        Reject(() => state.Execute("session-stop", id, path));
        Check(count == 4 && removes == 0 && state.LifecycleState == "active");
        live = false;
        count = 3;
        Reject(() => state.Execute("session-verify", id, path));
        Check(removes == 0);
        count = 4;
        Check(!state.Execute("session-stop", id, path).Enabled);
        Check(state.ShutdownRequested && count == 0 && removes == 1);
        state.Execute("session-stop", id, path);
        Check(removes == 1);
        count = 4;
        var orphan = new ProtectionSessionState(id, path, () => new(true, count), () => installs++, () => removes++);
        Reject(() => orphan.Execute("session-start", id, path));
        Check(count == 4 && installs == 1 && removes == 1);
        Reject(() => orphan.Execute("session-stop", id, path));
        var args = new[] { "--start-protection-session", "--game-path", path, "--session-id", id, "--request-id", nonce };
        Check(ProtectionSessionHost.Parse(args).Operation == "start");
        Check(ProtectionSessionHost.Parse(args).RequestId == nonce);
        int uac = 0,
            calls = 0;
        var reply = new GameConnectionBroker.Reply(1, nonce, path, GameConnectionHost.PolicyRevision, true, true, 4);
        var result = await ProtectionSessionHost.InitializeAsync(
            () =>
            {
                calls++;
                if (calls == 1)
                    throw new TimeoutException();
                return Task.FromResult(reply);
            },
            () => true,
            () =>
            {
                uac++;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask
        );
        Check(result == reply && uac == 1 && calls == 2);
        uac = 0;
        try
        {
            await ProtectionSessionHost.InitializeAsync(
                () => throw new InvalidOperationException("foreign"),
                () => true,
                () =>
                {
                    uac++;
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask
            );
        }
        catch (InvalidOperationException)
        {
            Check(uac == 0);
        }
        try
        {
            await ProtectionSessionHost.InitializeAsync(
                () => throw new TimeoutException(),
                () => false,
                () =>
                {
                    uac++;
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask
            );
        }
        catch (TimeoutException)
        {
            Check(uac == 0);
        }
        int queries = 0;
        GameConnectionHost.RefuseSessionOwnedMutation(
            () => true,
            () =>
            {
                queries++;
                return reply;
            }
        );
        Check(queries == 0);
        GameConnectionHost.RefuseSessionOwnedMutation(
            () => false,
            () =>
            {
                queries++;
                return reply;
            }
        );
        Check(queries == 1);
        Reject(() => GameConnectionHost.RefuseSessionOwnedMutation(() => false, () => reply with { SessionId = id }));
        Reject(() => GameConnectionHost.RefuseSessionOwnedMutation(() => false, () => throw new InvalidOperationException("peer")));
        const string code = "fixture-code";
        var request = new GameConnectionBroker.Request(1, nonce, "session-start", path, CodeIdentity: code, SessionId: id);
        GameConnectionBroker.ValidateRequest(request, code);
        Check(true);
        Reject(() => GameConnectionBroker.ValidateRequest(request with { RequestId = "bad" }, code));
        Reject(() => GameConnectionBroker.ValidateRequest(request with { SessionId = null }, code));
        Reject(() => GameConnectionBroker.ValidateRequest(request with { CodeIdentity = "stale" }, code));
        var trusted = reply with { CodeIdentity = code };
        GameConnectionBroker.ValidateReply(trusted, nonce, path, code);
        Check(true);
        Reject(() => GameConnectionBroker.ValidateReply(trusted, Guid.NewGuid().ToString("N"), path, code));
        Reject(() => GameConnectionBroker.ValidateReply(trusted with { OwnedFilterCount = 3 }, nonce, path, code));
        Reject(() => GameConnectionBroker.ValidateReply(trusted with { Verified = false, Error = "session mismatch" }, nonce, path, code));
        calls = 0;
        uac = 0;
        try
        {
            await ProtectionSessionHost.InitializeAsync(
                () =>
                {
                    calls++;
                    throw new TimeoutException();
                },
                () => true,
                () =>
                {
                    uac++;
                    return Task.CompletedTask;
                },
                () => Task.CompletedTask,
                1
            );
        }
        catch (TimeoutException)
        {
            Check(uac == 1 && calls == 3);
        }
        uac = 0;
        try
        {
            await ProtectionSessionHost.InitializeAsync(
                () => throw new TimeoutException(),
                () => true,
                () =>
                {
                    uac++;
                    throw new OperationCanceledException();
                },
                () => Task.CompletedTask
            );
        }
        catch (OperationCanceledException)
        {
            Check(uac == 1);
        }
        // Fault injection into the production lifecycle controller. No BFE service changes.
        foreach (var fault in new[] { "unavailable", "disabled", "missing", "partial" })
        {
            bool broken = false;
            int added = 0,
                deleted = 0,
                approvals = 0;
            ProtectionSnapshot Read() =>
                !broken
                    ? new(true, 4)
                    : fault switch
                    {
                        "unavailable" => throw new InvalidOperationException("BFE unavailable"),
                        "disabled" => new(false, 4),
                        "missing" => new(false, 0),
                        _ => new(true, 3),
                    };
            // Initialization starts from no policy, then the real install delegate publishes four.
            bool installed = false;
            var candidate = new ProtectionSessionState(
                id,
                path,
                () => installed ? Read() : new(false, 0),
                () =>
                {
                    added++;
                    installed = true;
                },
                () => deleted++
            );
            candidate.Execute("session-start", id, path);
            broken = true;
            Reject(() => candidate.Execute("session-verify", id, path));
            Reject(() => candidate.Execute("session-start", id, path));
            Check(added == 1 && deleted == 0);
            try
            {
                await ProtectionSessionHost.InitializeAsync(
                    () =>
                    {
                        candidate.Execute("session-start", id, path);
                        return Task.FromResult(reply);
                    },
                    () => true,
                    () =>
                    {
                        approvals++;
                        return Task.CompletedTask;
                    },
                    () => Task.CompletedTask
                );
                throw new Exception("Fault was accepted");
            }
            catch (InvalidOperationException)
            {
                Check(approvals == 0 && added == 1 && deleted == 0);
            }
        }
        bool faultInstalled = false;
        int faultAdds = 0,
            faultDeletes = 0;
        var failedStart = new ProtectionSessionState(
            id,
            path,
            () => faultInstalled ? throw new InvalidOperationException("Readback unavailable") : new(false, 0),
            () =>
            {
                faultAdds++;
                faultInstalled = true;
            },
            () => faultDeletes++
        );
        Reject(() => failedStart.Execute("session-start", id, path));
        Check(failedStart.LifecycleState == "faulted");
        Reject(() => failedStart.Execute("session-verify", id, path));
        Reject(() => failedStart.Execute("session-start", id, path));
        Check(faultAdds == 1 && faultDeletes == 0);
        Console.WriteLine($"PASS {n} session checks");
    }
}
