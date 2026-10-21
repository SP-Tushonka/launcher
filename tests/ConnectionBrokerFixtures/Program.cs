using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SPTarkov.Launcher.Connections;

int checks = 0;
const string id = "0123456789abcdef0123456789abcdef";
var game = Path.GetFullPath("EscapeFromTarkov.exe");
const string codeIdentity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
var valid = new GameConnectionBroker.Reply(1, id, game, GameConnectionHost.PolicyRevision, true, true, 4, CodeIdentity: codeIdentity);
void Check(bool result, string description)
{
    if (!result)
        throw new InvalidOperationException(description);
    checks++;
}
void RejectReply(GameConnectionBroker.Reply reply, string description)
{
    try
    {
        GameConnectionBroker.ValidateReply(reply, id, game, codeIdentity);
    }
    catch (InvalidOperationException)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException(description);
}
Task Write<T>(Stream stream, T value, CancellationToken token = default) =>
    (Task)
        typeof(GameConnectionBroker)
            .GetMethod("WriteAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T))
            .Invoke(null, [stream, value, token])!;
Task<T> Read<T>(Stream stream, CancellationToken token = default) =>
    (Task<T>)
        typeof(GameConnectionBroker)
            .GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T))
            .Invoke(null, [stream, token])!;
async Task RejectRead(Stream stream, string description)
{
    try
    {
        await Read<GameConnectionBroker.Reply>(stream);
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException(description);
}
MemoryStream Frame(int size, string payload = "")
{
    var stream = new MemoryStream();
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, size);
    stream.Write(prefix);
    stream.Write(Encoding.UTF8.GetBytes(payload));
    stream.Position = 0;
    return stream;
}

GameConnectionBroker.ValidateReply(valid, id, game, codeIdentity);
Check(true, "Matching verified enabled reply accepted");
Check(GameConnectionHost.PolicyRevision == "spt-host-tcp-v2", "Host advertises the approved host-wide TCP v2 revision");
GameConnectionBroker.ValidateReply(valid with { Enabled = false, OwnedFilterCount = 0 }, id, game, codeIdentity);
Check(true, "Verified absent filter state accepted");
var observedForWire = valid with { OwnedFilterCount = 17 };
using (var wire = JsonDocument.Parse(GameConnectionHost.SerializeVerificationReply(id, game, observedForWire)))
{
    var root = wire.RootElement;
    Check(
        root.GetProperty("ownedFilterCount").ValueKind == JsonValueKind.Number
            && root.GetProperty("ownedFilterCount").GetInt32() == observedForWire.OwnedFilterCount,
        "Verifier JSON preserves actual typed broker filter count"
    );
    Check(
        root.GetProperty("codeIdentity").ValueKind == JsonValueKind.String
            && root.GetProperty("codeIdentity").GetString() == valid.CodeIdentity,
        "Verifier JSON preserves actual broker startup code identity"
    );
    Check(
        root.GetProperty("requestId").GetString() == id && root.GetProperty("gameExecutable").GetString() == game,
        "Verifier JSON retains outer request correlation and exact target"
    );
    Check(root.GetProperty("policyRevision").GetString() == "spt-host-tcp-v2", "Verifier JSON advertises the host-wide TCP v2 revision");
}
using (
    var wire = JsonDocument.Parse(
        GameConnectionHost.SerializeVerificationReply(id, game, valid with { Enabled = false, OwnedFilterCount = 0 })
    )
)
    Check(
        wire.RootElement.GetProperty("ownedFilterCount").GetInt32() == 0 && wire.RootElement.GetProperty("enabled").GetBoolean() == false,
        "Verifier JSON reports the broker's actual empty filter state"
    );
GameConnectionBroker.ValidateReply(valid with { GameExecutable = game.ToUpperInvariant() }, id, game, codeIdentity);
Check(true, "Windows image path case tolerated");
RejectReply(valid with { Protocol = 2 }, "Wrong protocol");
RejectReply(valid with { RequestId = Guid.NewGuid().ToString("N") }, "Uncorrelated request");
RejectReply(valid with { RequestId = id.ToUpperInvariant() }, "Nonce comparison must be exact");
RejectReply(valid with { PolicyRevision = "spt-local-v1" }, "Legacy fixed-port policy revision cannot satisfy the v2 gate");
RejectReply(valid with { GameExecutable = Path.Combine(Path.GetDirectoryName(game)!, "Other.exe") }, "Wrong target executable");
RejectReply(valid with { Verified = false, Error = "unverified" }, "Unverified result");
RejectReply(valid with { Error = "unexpected" }, "Verified result cannot carry error");
RejectReply(valid with { OwnedFilterCount = 0 }, "Enabled cannot have no filters");
RejectReply(valid with { OwnedFilterCount = -1 }, "Negative count");
RejectReply(valid with { OwnedFilterCount = 6 }, "Legacy six-rule response cannot satisfy the four-rule v2 policy");
RejectReply(valid with { OwnedFilterCount = 5 }, "Wrong positive filter count is rejected");
RejectReply(valid with { Enabled = false }, "Off cannot carry owned filters");
RejectReply(valid with { CodeIdentity = null }, "Older broker without code identity rejected");
RejectReply(valid with { CodeIdentity = new string('F', 64) }, "Stale broker build rejected");
RejectReply(valid with { CodeIdentity = codeIdentity.ToLowerInvariant() }, "Code hash must match exact canonical identity");
var validRequest = new GameConnectionBroker.Request(1, id, "set", game, true, codeIdentity);
GameConnectionBroker.ValidateRequest(validRequest, codeIdentity);
Check(true, "Matching-code mutation request accepted");
foreach (
    var invalidRequest in new[]
    {
        validRequest with
        {
            CodeIdentity = null,
        },
        validRequest with
        {
            CodeIdentity = new string('F', 64),
        },
        validRequest with
        {
            Protocol = 2,
        },
        validRequest with
        {
            RequestId = "uncorrelated",
        },
        validRequest with
        {
            Operation = "unexpected",
        },
    }
)
{
    try
    {
        GameConnectionBroker.ValidateRequest(invalidRequest, codeIdentity);
        throw new Exception("Invalid request accepted");
    }
    catch (InvalidOperationException)
    {
        checks++;
    }
}
GameConnectionBroker.ValidateAcknowledgement(new(1, id), id);
Check(true, "Correlated acknowledgement accepted");
foreach (
    var invalidAcknowledgement in new[]
    {
        new GameConnectionBroker.Acknowledgement(2, id),
        new GameConnectionBroker.Acknowledgement(1, id.ToUpperInvariant()),
        new GameConnectionBroker.Acknowledgement(1, "wrong"),
    }
)
{
    try
    {
        GameConnectionBroker.ValidateAcknowledgement(invalidAcknowledgement, id);
        throw new Exception("Invalid acknowledgement accepted");
    }
    catch (InvalidOperationException)
    {
        checks++;
    }
}

using (var stream = new MemoryStream())
{
    await Write(stream, valid);
    Check(
        stream.Length > 4 && BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()) == stream.Length - 4,
        "Little-endian byte length prefixes exact serialized payload"
    );
    stream.Position = 0;
    Check(await Read<GameConnectionBroker.Reply>(stream) == valid, "Reply roundtrip");
    using var fragmented = new FragmentedStream(stream.ToArray());
    Check(await Read<GameConnectionBroker.Reply>(fragmented) == valid, "Split header and payload reads are reconstructed");
}
await RejectRead(Frame(0), "Zero length");
await RejectRead(Frame(-1), "Negative length");
await RejectRead(Frame(16385), "Over maximum length");
await RejectRead(new MemoryStream([1, 2, 3]), "Truncated header");
await RejectRead(Frame(8, "{}"), "Truncated payload");
await RejectRead(Frame(4, "null"), "Null JSON object");
await RejectRead(Frame(1, "{"), "Malformed JSON");
using (var stream = new MemoryStream())
{
    try
    {
        await Write(stream, new string('x', 16384));
        throw new Exception("Oversize write accepted");
    }
    catch (IOException)
    {
        Check(stream.Length == 0, "Oversize write rejected before prefix is written");
    }
}
using (var stream = Frame(16384, "\"" + new string('x', 16382) + "\""))
    Check((await Read<string>(stream)).Length == 16382, "Maximum bounded payload accepted");
using (var stream = new NeverReadableStream())
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
{
    try
    {
        await Read<GameConnectionBroker.Reply>(stream, cancellation.Token);
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException)
    {
        checks++;
    }
}
var nameMethod = typeof(GameConnectionBroker).GetMethod("PipeName", BindingFlags.NonPublic | BindingFlags.Static)!;
var firstName = (string)nameMethod.Invoke(null, ["S-1-5-21-1"])!;
Check(firstName == (string)nameMethod.Invoke(null, ["S-1-5-21-1"])!, "Pipe identity is stable for one owner/image");
Check(firstName != (string)nameMethod.Invoke(null, ["S-1-5-21-2"])!, "Pipe identity binds requesting owner SID");
Check(
    firstName.StartsWith("SPT.GameConnections.") && firstName.Length == "SPT.GameConnections.".Length + 32,
    "Pipe name has bounded hashed identity"
);
Console.WriteLine($"{checks} broker framing and correlation checks passed; no native pipe, WFP or game calls.");

sealed class FragmentedStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}

sealed class NeverReadableStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
