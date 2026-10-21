using System.Buffers.Binary;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static SPT.GameConnections.WfpNative;

namespace SPT.GameConnections;

/// <summary>
/// Machine-wide persistent WFP objects scoped to one executable AppID. Mutations require
/// elevation; this class never requests UAC or starts a process. Read failures are unknown,
/// never interpreted as protection being disabled or enabled.
/// </summary>
public sealed class WfpConnectionFilter
{
    private const string ProviderName = "SPT Launcher Game Connections";
    private const string SublayerName = "SPT game server allowlist v1";

    public ConnectionVerification Inspect()
    {
        try
        {
            using var engine = new Engine();
            return ReadSnapshot(
                engine.Handle,
                filters => new ConnectionVerification(
                    false,
                    filters.Count == 0
                        ? "No game connection filters are installed."
                        : "Game connection filters exist; verify the selected game and server before claiming protection.",
                    filters.Count
                )
            );
        }
        catch (Win32Exception error)
        {
            // Throwing distinguishes an inaccessible BFE/read ACL from a verified disabled state.
            throw new InvalidOperationException("Cannot determine game connection protection state.", error);
        }
    }

    public ConnectionVerification Verify(ConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var engine = new Engine();
        Check(FwpmGetAppIdFromFileName0(policy.GameExecutable, out var appId), "Resolve game AppID");
        try
        {
            return ReadSnapshot(engine.Handle, filters => VerifySnapshot(engine.Handle, policy, appId, filters));
        }
        finally
        {
            FwpmFreeMemory0(ref appId);
        }
    }

    /// <summary>Atomic replace. Existing filters stay active if any operation or verification fails.</summary>
    public ConnectionVerification Install(ConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var engine = new Engine();
        Check(FwpmGetAppIdFromFileName0(policy.GameExecutable, out var appId), "Resolve game AppID");
        try
        {
            Check(FwpmTransactionBegin0(engine.Handle, 0), "Begin protection update");
            var committed = false;
            try
            {
                ValidateProviderIfPresent(engine.Handle, allowDisabled: true);
                ValidateSublayerIfPresent(engine.Handle);
                var existing = EnumerateOwned(engine.Handle, filter => filter.Key);
                DeleteKnownFilters(engine.Handle, existing);
                // Recreate our empty provider as well: BFE marks service-less providers disabled
                // after reboot. Re-adding clears that state without touching another provider.
                var deleteLayer = FwpmSubLayerDeleteByKey0(engine.Handle, FilterPlan.Sublayer);
                if (deleteLayer != SublayerNotFound)
                    Check(deleteLayer, "Replace game protection sublayer");
                var deleteProvider = FwpmProviderDeleteByKey0(engine.Handle, FilterPlan.Provider);
                if (deleteProvider != ProviderNotFound)
                    Check(deleteProvider, "Replace game protection provider");
                EnsureProvider(engine.Handle);
                EnsureSublayer(engine.Handle);
                foreach (var rule in FilterPlan.Build(policy))
                    Add(engine.Handle, rule, appId);
                var plan = FilterPlan.Build(policy);
                var state = EnumerateOwned(engine.Handle, Copy);
                var appBytes = BlobBytes(Marshal.PtrToStructure<Blob>(appId));
                var mismatched = state.FirstOrDefault(filter => !plan.Any(rule => MatchesCopy(filter, rule, appBytes)));
                if (state.Count != plan.Count || mismatched is not null)
                    throw new InvalidOperationException(
                        "The pending protection rules did not match the requested policy: "
                            + $"count={state.Count}/{plan.Count}; "
                            + (
                                mismatched is null
                                    ? "rule set incomplete"
                                    : $"key={mismatched.Key}; flags={mismatched.Flags:x}; action={mismatched.Action:x}; weight={mismatched.WeightType}:{mismatched.Weight}; emptyContext={mismatched.EmptyContext}; conditions="
                                        + string.Join(
                                            ',',
                                            mismatched.Conditions.Select(condition =>
                                                $"{condition.Key}:{condition.Match}:{condition.Type}:{condition.Bytes.Length}"
                                            )
                                        )
                            )
                    );
                Check(FwpmTransactionCommit0(engine.Handle), "Commit protection update");
                committed = true;
            }
            finally
            {
                if (!committed)
                    FwpmTransactionAbort0(engine.Handle);
            }
        }
        finally
        {
            FwpmFreeMemory0(ref appId);
        }
        var verification = Verify(policy);
        if (!verification.IsEnabled)
            throw new InvalidOperationException("Protection was committed but verification failed: " + verification.Detail);
        return verification;
    }

    /// <summary>Deletes only this provider's exact version-1 filter keys and owned sublayer.</summary>
    public ConnectionVerification Remove()
    {
        using var engine = new Engine();
        Check(FwpmTransactionBegin0(engine.Handle, 0), "Begin protection removal");
        var committed = false;
        try
        {
            ValidateProviderIfPresent(engine.Handle, allowDisabled: true);
            ValidateSublayerIfPresent(engine.Handle);
            DeleteKnownFilters(engine.Handle, EnumerateOwned(engine.Handle, filter => filter.Key));
            var code = FwpmSubLayerDeleteByKey0(engine.Handle, FilterPlan.Sublayer);
            if (code != SublayerNotFound)
                Check(code, "Remove game protection sublayer");
            code = FwpmProviderDeleteByKey0(engine.Handle, FilterPlan.Provider);
            if (code != ProviderNotFound)
                Check(code, "Remove game protection provider");
            Check(FwpmTransactionCommit0(engine.Handle), "Commit protection removal");
            committed = true;
        }
        finally
        {
            if (!committed)
                FwpmTransactionAbort0(engine.Handle);
        }
        return ReadSnapshot(
            engine.Handle,
            filters =>
            {
                if (filters.Count != 0)
                    throw new InvalidOperationException("Game protection removal could not be verified.");
                return new ConnectionVerification(false, "Game connection protection is off.", 0);
            }
        );
    }

    private static T ReadSnapshot<T>(IntPtr engine, Func<IReadOnlyList<FilterCopy>, T> action)
    {
        // Snapshot enumeration and object queries share one read-only transaction.
        Check(
            FwpmTransactionBegin0(
                engine,
                1 /* FWPM_TXN_READ_ONLY */
            ),
            "Begin protection inspection"
        );
        try
        {
            return action(EnumerateOwned(engine, Copy));
        }
        finally
        {
            Check(FwpmTransactionAbort0(engine), "End protection inspection");
        }
    }

    private static ConnectionVerification VerifySnapshot(
        IntPtr engine,
        ConnectionPolicy policy,
        IntPtr appId,
        IReadOnlyList<FilterCopy> filters
    )
    {
        if (!ValidateProviderIfPresent(engine) || !ValidateSublayerIfPresent(engine))
            return new(false, "Game connection protection is not installed.", filters.Count);
        var plan = FilterPlan.Build(policy);
        var appBytes = BlobBytes(Marshal.PtrToStructure<Blob>(appId));
        if (filters.Count != plan.Count || filters.Select(f => f.Key).Distinct().Count() != plan.Count)
            return new(false, "Installed game connection rule set is incomplete or contains unexpected rules.", filters.Count);
        foreach (var filter in filters)
        {
            var rule = plan.SingleOrDefault(rule => rule.Key == filter.Key);
            if (rule is null || !MatchesCopy(filter, rule, appBytes))
                return new(false, "Installed rules do not match the selected game and SPT server.", filters.Count);
        }
        return new(true, "The game client can connect only to the approved SPT host over TCP.", filters.Count);
    }

    private static void DeleteKnownFilters(IntPtr engine, IReadOnlyList<Guid> keys)
    {
        var known = Enumerable.Range(0, FilterPlan.MaximumRules).Select(FilterPlan.Key).ToHashSet();
        if (keys.Any(key => !known.Contains(key)))
            throw new InvalidOperationException("Unexpected owned filters found. Refusing to delete an unknown policy version.");
        foreach (var key in keys)
            Check(FwpmFilterDeleteByKey0(engine, key), "Remove previous game protection rule");
    }

    private static void EnsureProvider(IntPtr engine)
    {
        if (ValidateProviderIfPresent(engine))
            return;
        var provider = new Provider
        {
            Key = FilterPlan.Provider,
            Display = new Display { Name = ProviderName },
            Flags = Persistent,
        };
        Check(FwpmProviderAdd0(engine, ref provider, IntPtr.Zero), "Create game protection provider");
    }

    private static bool ValidateProviderIfPresent(IntPtr engine, bool allowDisabled = false)
    {
        var code = FwpmProviderGetByKey0(engine, FilterPlan.Provider, out var pointer);
        if (code == ProviderNotFound)
            return false;
        Check(code, "Read game protection provider");
        try
        {
            var provider = Marshal.PtrToStructure<Provider>(pointer);
            if (
                provider.Key != FilterPlan.Provider
                || provider.Display.Name != ProviderName
                || (provider.Flags != Persistent && !(allowDisabled && provider.Flags == (Persistent | 0x10)))
                || provider.ServiceName != IntPtr.Zero
                || provider.Data.Size != 0
            )
                throw new InvalidOperationException("The game protection provider key is occupied by an unexpected object.");
            return true;
        }
        finally
        {
            FwpmFreeMemory0(ref pointer);
        }
    }

    private static void EnsureSublayer(IntPtr engine)
    {
        if (ValidateSublayerIfPresent(engine))
            return;
        using var arena = new Arena();
        var sublayer = new Sublayer
        {
            Key = FilterPlan.Sublayer,
            Display = new Display { Name = SublayerName },
            Flags = Persistent,
            Provider = arena.Structure(FilterPlan.Provider),
            Weight = ushort.MaxValue,
        };
        Check(FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero), "Create game protection sublayer");
    }

    private static bool ValidateSublayerIfPresent(IntPtr engine)
    {
        var code = FwpmSubLayerGetByKey0(engine, FilterPlan.Sublayer, out var pointer);
        if (code == SublayerNotFound)
            return false;
        Check(code, "Read game protection sublayer");
        try
        {
            var layer = Marshal.PtrToStructure<Sublayer>(pointer);
            if (
                layer.Key != FilterPlan.Sublayer
                || layer.Display.Name != SublayerName
                || layer.Flags != Persistent
                || layer.Weight != ushort.MaxValue
                || layer.Data.Size != 0
                || !GuidAt(layer.Provider, FilterPlan.Provider)
            )
                throw new InvalidOperationException("The game protection sublayer key is occupied by an unexpected object.");
            return true;
        }
        finally
        {
            FwpmFreeMemory0(ref pointer);
        }
    }

    private static void Add(IntPtr engine, FilterRule rule, IntPtr appId)
    {
        using var arena = new Arena();
        var conditions = new List<Condition>
        {
            new()
            {
                Key = AppId,
                Value = Value.Ptr(
                    12 /* FWP_BYTE_BLOB_TYPE */
                    ,
                    appId
                ),
            },
        };
        if (rule.Endpoint is { } endpoint)
        {
            var address = endpoint.Address.GetAddressBytes();
            conditions.Add(
                new Condition
                {
                    Key = RemoteAddress,
                    Value =
                        endpoint.Address.AddressFamily == AddressFamily.InterNetwork
                            ? Value.U32(BinaryPrimitives.ReadUInt32BigEndian(address))
                            : Value.Ptr(
                                11 /* FWP_BYTE_ARRAY16_TYPE */
                                ,
                                arena.Bytes(address)
                            ),
                }
            );
            if (endpoint.Port is ushort port)
                conditions.Add(new Condition { Key = RemotePort, Value = Value.U16(port) });
            conditions.Add(new Condition { Key = Protocol, Value = Value.U8(endpoint.Protocol) });
        }
        var filter = new Filter
        {
            Key = rule.Key,
            Display = new Display { Name = rule.Permit ? "SPT game approved endpoint" : "SPT game deny other destinations" },
            Flags = Persistent,
            Provider = arena.Structure(FilterPlan.Provider),
            Layer = rule.Layer,
            Sublayer = FilterPlan.Sublayer,
            Weight = Value.U8(rule.Permit ? (byte)15 : (byte)0),
            ConditionCount = (uint)conditions.Count,
            Conditions = arena.Conditions(conditions.ToArray()),
            Action = new WfpNative.Action { Type = rule.Permit ? Permit : Block },
        };
        Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _), "Install game protection rule");
    }

    private static IReadOnlyList<T> EnumerateOwned<T>(IntPtr engine, Func<Filter, T> copy)
    {
        Check(FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out var enumerator), "Enumerate filtering rules");
        try
        {
            var result = new List<T>();
            while (true)
            {
                Check(FwpmFilterEnum0(engine, enumerator, 256, out var entries, out var count), "Read filtering rules");
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var filter = Marshal.PtrToStructure<Filter>(Marshal.ReadIntPtr(entries, i * IntPtr.Size));
                        if (GuidAt(filter.Provider, FilterPlan.Provider))
                        {
                            if (filter.Sublayer != FilterPlan.Sublayer)
                                throw new InvalidOperationException("Unexpected game protection rule uses a different sublayer.");
                            result.Add(copy(filter));
                        }
                        else if (
                            filter.Sublayer == FilterPlan.Sublayer
                            || Enumerable.Range(0, FilterPlan.MaximumRules).Any(slot => filter.Key == FilterPlan.Key(slot))
                        )
                            throw new InvalidOperationException("A game protection key is occupied by another provider.");
                    }
                }
                finally
                {
                    FwpmFreeMemory0(ref entries);
                }
                if (count < 256)
                    return result.AsReadOnly();
            }
        }
        finally
        {
            Check(FwpmFilterDestroyEnumHandle0(engine, enumerator), "Finish filtering rule enumeration");
        }
    }

    private static bool GuidAt(IntPtr pointer, Guid expected) =>
        pointer != IntPtr.Zero && Marshal.PtrToStructure<Guid>(pointer) == expected;

    internal sealed record ConditionCopy(Guid Key, uint Match, uint Type, byte[] Bytes);

    internal sealed record FilterCopy(
        Guid Key,
        Guid Layer,
        uint Flags,
        uint Action,
        uint WeightType,
        byte Weight,
        bool EmptyContext,
        IReadOnlyList<ConditionCopy> Conditions
    );

    private static FilterCopy Copy(Filter filter)
    {
        if (filter.ConditionCount > 16)
            throw new InvalidOperationException("Unexpected number of conditions on game protection rule.");
        var conditions = new List<ConditionCopy>();
        for (var i = 0; i < filter.ConditionCount; i++)
        {
            var condition = Marshal.PtrToStructure<Condition>(filter.Conditions + i * Marshal.SizeOf<Condition>());
            byte[] bytes = condition.Value.Type switch
            {
                1 => new[] { condition.Value.UInt8 },
                2 => BitConverter.GetBytes(condition.Value.UInt16),
                3 => BitConverter.GetBytes(condition.Value.UInt32),
                11 => ReadBytes(condition.Value.Pointer, 16),
                12 => BlobBytes(Marshal.PtrToStructure<Blob>(condition.Value.Pointer)),
                _ => Array.Empty<byte>(),
            };
            conditions.Add(new(condition.Key, condition.Match, condition.Value.Type, bytes));
        }
        return new(
            filter.Key,
            filter.Layer,
            filter.Flags,
            filter.Action.Type,
            filter.Weight.Type,
            filter.Weight.UInt8,
            filter.Context.Raw == 0 && filter.Data.Size == 0,
            conditions.AsReadOnly()
        );
    }

    private static bool MatchesRule(Filter filter, IReadOnlyList<FilterRule> plan, IntPtr appId)
    {
        var rule = plan.SingleOrDefault(rule => rule.Key == filter.Key);
        return rule is not null && MatchesCopy(Copy(filter), rule, BlobBytes(Marshal.PtrToStructure<Blob>(appId)));
    }

    internal static bool MatchesCopy(FilterCopy filter, FilterRule rule, byte[] appId)
    {
        // BFE automatically marks eligible native filters INDEXED (0x40). This changes
        // lookup implementation only; every semantic/disabled/action-right flag must match.
        if (
            filter.Key != rule.Key
            || filter.Layer != rule.Layer
            || (filter.Flags & ~0x40u) != Persistent
            || filter.Action != (rule.Permit ? Permit : Block)
            || !filter.EmptyContext
            || filter.WeightType != 1
            || filter.Weight != (rule.Permit ? 15 : 0)
        )
            return false;
        var expected = new List<ConditionCopy> { new(AppId, 0, 12, appId) };
        if (rule.Endpoint is { } endpoint)
        {
            var v4 = endpoint.Address.AddressFamily == AddressFamily.InterNetwork;
            expected.Add(
                new(
                    RemoteAddress,
                    0,
                    v4 ? 3u : 11u,
                    v4
                        ? BitConverter.GetBytes(BinaryPrimitives.ReadUInt32BigEndian(endpoint.Address.GetAddressBytes()))
                        : endpoint.Address.GetAddressBytes()
                )
            );
            if (endpoint.Port is ushort port)
                expected.Add(new(RemotePort, 0, 2, BitConverter.GetBytes(port)));
            expected.Add(new(Protocol, 0, 1, new[] { endpoint.Protocol }));
        }
        return filter.Conditions.Count == expected.Count
            && expected.All(condition =>
                filter.Conditions.Count(actual =>
                    actual.Key == condition.Key
                    && actual.Match == condition.Match
                    && actual.Type == condition.Type
                    && actual.Bytes.SequenceEqual(condition.Bytes)
                ) == 1
            );
    }

    private static byte[] ReadBytes(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0 || length > 65536)
            throw new InvalidOperationException("Invalid WFP value.");
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static byte[] BlobBytes(Blob blob) => ReadBytes(blob.Data, checked((int)blob.Size));
}
