using ReVerse.Capture.Logging;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ReVerse.Capture.Signaling;

namespace Provider.Tests;

internal static class Assert
{
    internal static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static T NotNull<T>(T? value, string message) where T : class =>
        value ?? throw new InvalidOperationException(message);

    internal static void Bytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message) =>
        That(expected.SequenceEqual(actual), message);

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}


internal static class Bytes
{
    internal static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    internal static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    internal static byte[] Sized(byte[] bytes) => [.. U16(checked((ushort)bytes.Length)), .. bytes];
    internal static byte[] Text(string value) => Sized(Encoding.ASCII.GetBytes(value));
    internal static byte[] Block(uint sender, uint destination, bool secondary = true) =>
        [2, 1, .. U32(sender), .. U32(destination), 0, 4, 0, (byte)(secondary ? 3 : 0),
            0, 0xff, 0x80, 0x42, .. (secondary ? new byte[] { 0xbe, 0xef, 0x10, 0, 0xfe } : [])];
    internal static byte[] Route(ushort[] destinations, byte[] block) =>
        [.. U16(checked((ushort)destinations.Length)), .. destinations.SelectMany(U16), .. Sized(block)];

    internal static byte[] Change(byte[] bytes, int position, byte value)
    {
        var copy = bytes.ToArray();
        copy[position] = value;
        return copy;
    }
}

internal sealed record Credential(string Identity, string Psk, string Secret)
{
    internal byte[] IdentityBytes => Encoding.ASCII.GetBytes(Identity);
    internal byte[] SecretBytes => Encoding.ASCII.GetBytes(Secret);
    internal static Credential From(JsonObject descriptor) => new(
        descriptor["endpoints"]![0]!["identityHint"]!.GetValue<string>(),
        descriptor["endpoints"]![0]!["psk"]!.GetValue<string>(), descriptor["secret"]!.GetValue<string>());
}

internal sealed class Room : IDisposable
{
    internal const string Session = "room-1";
    internal static readonly SignalingMember[] Members =
        [new("carol", 0x10203040), new("alice", 0x01020304), new("bob", 0xaabbccdd)];
    internal SignalingOptions Options { get; }
    internal SignalingDirectory Directory { get; }
    internal Dictionary<string, Credential> Credentials { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SignalingConversation> Conversations { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SignalingSnapshot> Peers { get; } = new(StringComparer.Ordinal);
    internal List<string> Stages { get; } = [];
    internal List<SignalingPayloadAudit> PayloadAudits { get; } = [];

    internal static SignalingOptions NewOptions() => new()
    {
        Enabled = true, BindAddress = "127.0.0.1", Port = 5070, PublicHost = "127.0.0.1", PublicPort = 5070,
        ExperimentalReplies = true, Reply19 = 1, Reply21 = 1, RegistrationFieldBIsPeerNumber = true
    };

    internal Room(SignalingOptions? options = null, IReadOnlyList<SignalingMember>? members = null, UdpTrafficLog? udp = null)
    {
        Options = options ?? NewOptions();
        Options.Validate();
        Directory = new(Options);
        Directory.SetListening(true);
        members ??= Members;
        Assert.That(Directory.Allocate(Session, "carol", members, DateTimeOffset.UtcNow.AddSeconds(30)), "fixture allocation");
        foreach (var member in members)
        {
            var credential = Credential.From(Assert.NotNull(Directory.Descriptor(Session, member.Account), "fixture descriptor"));
            Credentials.Add(member.Account, credential);
            var snapshot = Assert.NotNull(Directory.Attach(credential.IdentityBytes, Connection(member.Account)), "fixture attach");
            Peers.Add(member.Account, snapshot);
            Conversations.Add(member.Account, new(Options, Directory, snapshot, Connection(member.Account), Stages.Add, PayloadAudits.Add, udp));
        }
    }

    internal static string Connection(string account) => "connection/" + account;
    internal SignalingDelivery? Take(string account) => Directory.Dequeue(Session, account, Connection(account));
    internal void Startup(string account)
    {
        var conversation = Conversations[account];
        conversation.Control(new WireControl(3, []));
        conversation.Control(new WireSettings([new(0, 5000), new(1, 3), new(2, 25)]));
        conversation.Control(new WireControl(0x12, [1, 1]));
        conversation.Control(new WireControl(0x14, [1]));
    }

    internal void Register(string account) =>
        Conversations[account].Application(1, 1, 1, Bytes.Sized(Credentials[account].SecretBytes));

    internal void Ready()
    {
        foreach (var account in Credentials.Keys) { Startup(account); Register(account); }
        Assert.That(Directory.IsActive(Session), "fixture did not activate");
        foreach (var account in Credentials.Keys)
        {
            Assert.That(Take(account) is { Family: 1, Operation: 1, Qualifier: 2 }, "missing registration reply");
            Assert.That(Take(account) is { Family: 3, Operation: 7, Qualifier: 0x10 }, "missing readiness");
        }
        Empty();
    }

    internal void Empty()
    {
        foreach (var account in Credentials.Keys) Assert.That(Take(account) is null, "unexpected delivery for " + account);
    }

    public void Dispose() => Directory.Remove(Session);
}
