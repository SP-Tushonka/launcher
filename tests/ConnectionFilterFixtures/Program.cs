using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SPT.GameConnections;
using static SPT.GameConnections.WfpConnectionFilter;
using static SPT.GameConnections.WfpNative;

// Pure managed ABI, policy and exact-match tests. Never opens or mutates the WFP engine.
var assertions = 0;
void FixtureCheck(bool condition, string name)
{
    if (!condition)
        throw new Exception("FAILED: " + name);
    assertions++;
}
void Reject(System.Action operation, string name)
{
    try
    {
        operation();
    }
    catch (ArgumentException)
    {
        assertions++;
        return;
    }
    throw new Exception("FAILED: " + name);
}
var directory = Path.Combine(Path.GetTempPath(), "spt-connections-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var executable = Path.Combine(directory, "EscapeFromTarkov.exe");
    File.WriteAllText(executable, "Owned inert fixture; never executed.");
    var policy = ConnectionPolicy.ForSupportedRuntime(executable);
    Reject(() => ConnectionPolicy.ForSupportedRuntime("EscapeFromTarkov.exe"), "relative executable rejected");
    Reject(() => ConnectionPolicy.ForSupportedRuntime(Path.Combine(directory, "Other.exe")), "other executable rejected");
    FixtureCheck(ConnectionPolicy.PolicyRevision == "spt-host-tcp-v2", "host TCP policy revision");
    FixtureCheck(ConnectionPolicy.ExpectedFilterCount == 4, "shared native rule count contract");
    FixtureCheck(policy.Endpoints.Count == 1, "one exact approved host");
    FixtureCheck(policy.Endpoints.All(e => e.Address.ToString() == "127.0.0.1" && e.Protocol == 6), "only loopback TCP");
    FixtureCheck(policy.Endpoints.All(e => e.Port is null), "host permit omits port restriction explicitly");
    var rules = FilterPlan.Build(policy);
    FixtureCheck(FilterPlan.ConnectV4 == new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82"), "SDK IPv4 ALE connect GUID");
    FixtureCheck(FilterPlan.ConnectV6 == new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), "SDK IPv6 ALE connect GUID");
    FixtureCheck(rules.Count == 4, "two exact host permits and two defaults");
    FixtureCheck(rules.Select(r => r.Key).Distinct().Count() == rules.Count, "unique owned keys");
    FixtureCheck(rules.Count(r => r.Permit && r.Layer == FilterPlan.ConnectV4) == 1, "one IPv4 host permit");
    FixtureCheck(
        rules.Count(r => r.Permit && r.Layer == FilterPlan.ConnectV6 && r.Endpoint!.Address.IsIPv4MappedToIPv6) == 1,
        "one mapped IPv6 host permit"
    );
    FixtureCheck(rules.Count(r => !r.Permit && r.Layer == FilterPlan.ConnectV4) == 1, "IPv4 deny default");
    FixtureCheck(rules.Count(r => !r.Permit && r.Layer == FilterPlan.ConnectV6) == 1, "IPv6 deny default");
    FixtureCheck(!rules.Any(r => r.Permit && r.Endpoint!.Protocol == 17), "no UDP exception");
    FixtureCheck(!rules.Any(r => r.Permit && r.Endpoint!.Port == 0), "zero is not a wildcard port value");
    FixtureCheck(!rules.Any(r => r.Permit && r.Endpoint!.Address.ToString() == "127.0.0.2"), "no broader 127/8 host permit");
    FixtureCheck(!rules.Any(r => r.Permit && r.Endpoint!.Address.ToString() == "::1"), "no native IPv6 loopback exception");

    // Independently assert SDK x64 sizes and offsets before any unmanaged use.
    FixtureCheck(Environment.Is64BitProcess, "x64 fixture process");
    FixtureCheck(Marshal.SizeOf<Value>() == 16, "FWP_VALUE0 ABI size");
    FixtureCheck(Marshal.OffsetOf<Value>(nameof(Value.Pointer)).ToInt32() == 8, "FWP union ABI offset");
    FixtureCheck(Marshal.SizeOf<Blob>() == 16, "FWP_BYTE_BLOB ABI size");
    FixtureCheck(Marshal.SizeOf<Condition>() == 40, "FWPM_FILTER_CONDITION0 ABI size");
    FixtureCheck(Marshal.OffsetOf<Condition>(nameof(Condition.Value)).ToInt32() == 24, "condition value ABI offset");
    FixtureCheck(Marshal.SizeOf<Provider>() == 64, "FWPM_PROVIDER0 ABI size");
    FixtureCheck(Marshal.SizeOf<Sublayer>() == 72, "FWPM_SUBLAYER0 ABI size");
    FixtureCheck(Marshal.SizeOf<Filter>() == 200, "FWPM_FILTER0 ABI size");
    FixtureCheck(Marshal.OffsetOf<Filter>(nameof(Filter.Conditions)).ToInt32() == 120, "filter conditions ABI offset");
    FixtureCheck(Marshal.OffsetOf<Filter>(nameof(Filter.Action)).ToInt32() == 128, "filter action ABI offset");
    FixtureCheck(Marshal.OffsetOf<Filter>(nameof(Filter.Context)).ToInt32() == 152, "filter context ABI offset");
    FixtureCheck(Marshal.OffsetOf<Filter>(nameof(Filter.EffectiveWeight)).ToInt32() == 184, "filter effective weight ABI offset");
    var appId = Encoding.Unicode.GetBytes(@"\device\harddiskvolume3\game\escapefromtarkov.exe" + '\0');
    foreach (var rule in rules)
    {
        var conditions = new List<ConditionCopy> { new(AppId, 0, 12, appId) };
        if (rule.Endpoint is { } endpoint)
        {
            var address = endpoint.Address.GetAddressBytes();
            var isV4 = endpoint.Address.AddressFamily == AddressFamily.InterNetwork;
            conditions.Add(
                new(
                    RemoteAddress,
                    0,
                    isV4 ? 3u : 11u,
                    isV4 ? BitConverter.GetBytes(BinaryPrimitives.ReadUInt32BigEndian(address)) : address
                )
            );
            if (endpoint.Port is ushort port)
                conditions.Add(new(RemotePort, 0, 2, BitConverter.GetBytes(port)));
            conditions.Add(new(Protocol, 0, 1, new[] { endpoint.Protocol }));
        }
        var filter = new FilterCopy(
            rule.Key,
            rule.Layer,
            Persistent,
            rule.Permit ? Permit : Block,
            1,
            rule.Permit ? (byte)15 : (byte)0,
            true,
            conditions
        );
        FixtureCheck(MatchesCopy(filter, rule, appId), "exact snapshot accepted: " + rule.Key);
        FixtureCheck(MatchesCopy(filter with { Flags = Persistent | 0x40 }, rule, appId), "native BFE indexed readback accepted");
        FixtureCheck(!MatchesCopy(filter with { Flags = Persistent | 0x20 }, rule, appId), "disabled filter rejected");
        FixtureCheck(!MatchesCopy(filter with { Layer = Guid.NewGuid() }, rule, appId), "wrong layer rejected");
        FixtureCheck(!MatchesCopy(filter with { Action = rule.Permit ? Block : Permit }, rule, appId), "wrong action rejected");
        FixtureCheck(!MatchesCopy(filter with { EmptyContext = false }, rule, appId), "unexpected context rejected");
        FixtureCheck(!MatchesCopy(filter with { Weight = 7 }, rule, appId), "wrong priority rejected");
        FixtureCheck(!MatchesCopy(filter with { Conditions = conditions.Skip(1).ToArray() }, rule, appId), "unscoped AppID rejected");
        FixtureCheck(
            !MatchesCopy(filter with { Conditions = conditions.Append(conditions[0]).ToArray() }, rule, appId),
            "extra condition rejected"
        );
        FixtureCheck(!MatchesCopy(filter, rule, Encoding.Unicode.GetBytes("another game")), "different executable rejected");
        var negated = conditions.Select(c => c.Key == AppId ? c with { Match = 1 } : c).ToArray();
        FixtureCheck(!MatchesCopy(filter with { Conditions = negated }, rule, appId), "non-equality AppID rejected");
        if (rule.Permit)
        {
            FixtureCheck(
                conditions.Count == 3 && conditions.All(condition => condition.Key != RemotePort),
                "permit has exactly AppID/address/TCP and no port condition"
            );
            foreach (var forbiddenPortCondition in new ushort[] { 0, 443, 6969, 65535 })
                FixtureCheck(
                    !MatchesCopy(
                        filter with
                        {
                            Conditions = conditions
                                .Append(new ConditionCopy(RemotePort, 0, 2, BitConverter.GetBytes(forbiddenPortCondition)))
                                .ToArray(),
                        },
                        rule,
                        appId
                    ),
                    "old or fake-wildcard port restriction rejected"
                );
            foreach (var key in new[] { RemoteAddress, Protocol })
            {
                var changed = conditions.Select(c => c.Key == key ? c with { Bytes = new byte[c.Bytes.Length] } : c).ToArray();
                FixtureCheck(!MatchesCopy(filter with { Conditions = changed }, rule, appId), "different destination/protocol rejected");
            }
            var udp = conditions.Select(c => c.Key == Protocol ? c with { Bytes = new byte[] { 17 } } : c).ToArray();
            FixtureCheck(!MatchesCopy(filter with { Conditions = udp }, rule, appId), "UDP cannot match the approved TCP host");
        }
    }
    Console.WriteLine($"PASS {assertions} managed filter assertions; no WFP engine opened, no network changes.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}
