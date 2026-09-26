using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ReVerse.Capture.Signaling;

public sealed record SignalingMember(string Account, uint Nonce);
public sealed record SignalingSnapshot(string Session, string Account, ushort Number, uint Nonce,
    ushort Representative, IReadOnlyList<SignalingPeerInfo> Peers);
public sealed record SignalingPeerInfo(string Account, ushort Number, uint Nonce);
public sealed record SignalingDelivery(byte Family, byte Operation, byte Qualifier, byte[] Body, byte Descriptor = 0x1c, string? ForwardingId = null, string? SenderAccount = null);


public sealed class SignalingDirectory(SignalingOptions options)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Allocation> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Peer> identities = new(StringComparer.Ordinal);
    private bool listening;

    private sealed class Allocation(string session, string representative, DateTimeOffset deadline)
    {
        public string Session { get; } = session;
        public string Representative { get; } = representative;
        public DateTimeOffset Deadline { get; } = deadline;
        public DateTimeOffset HardDeadline { get; set; }
        public bool Active { get; set; }
        public bool Failed { get; set; }
        public bool TopologyPending { get; set; }
        public HashSet<ushort> RetiredNumbers { get; } = [];
        public List<Peer> Peers { get; } = [];
    }

    private sealed class Peer(Allocation allocation, SignalingMember member, ushort number)
    {
        public Allocation Allocation { get; } = allocation;
        public SignalingMember Member { get; } = member;
        public ushort Number { get; } = number;
        public string Identity { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        public byte[] Psk { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] Secret { get; } = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        public string? Connection { get; set; }
        public bool Registered { get; set; }
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public Channel<SignalingDelivery> Out { get; } = Channel.CreateBounded<SignalingDelivery>(128);
    }

    public bool IsListening { get { lock (gate) return listening; } }
    public void SetListening(bool value) { lock (gate) listening = value; }

    public bool Allocate(string session, string representative, IReadOnlyList<SignalingMember> members, DateTimeOffset deadline)
    {
        lock (gate)
        {
            if (!listening) return false;
            if (sessions.ContainsKey(session)) return true;
            ValidateMembers(representative, members);
            if (identities.Count + members.Count > options.MaxConnections) return false;
            sessions.Add(session, Create(session, representative, members, deadline));
            return true;
        }
    }

    public bool Replace(string session, string representative, IReadOnlyList<SignalingMember> members, DateTimeOffset deadline)
    {
        lock (gate)
        {
            if (!listening) return false;
            ValidateMembers(representative, members, allowSingle: true);
            if (!sessions.TryGetValue(session, out var allocation) || !Valid(allocation)) return false;
            if (allocation.Representative != representative) throw new InvalidDataException("Invalid signaling representative.");
            var requested = members.ToDictionary(member => member.Account, StringComparer.Ordinal);
            foreach (var peer in allocation.Peers)
                if (requested.TryGetValue(peer.Member.Account, out var member) && member.Nonce != peer.Member.Nonce)
                    throw new InvalidDataException("Signaling member nonce changed.");
            var removed = allocation.Peers.Where(peer => !requested.ContainsKey(peer.Member.Account)).ToArray();
            var added = members.Where(member => allocation.Peers.All(peer => peer.Member.Account != member.Account)).ToArray();
            if (identities.Count - removed.Length + added.Length > options.MaxConnections) return false;
            foreach (var peer in removed)
            {
                allocation.RetiredNumbers.Add(peer.Number);
                allocation.Peers.Remove(peer);
                RemovePeer(peer);
            }
            var used = allocation.Peers.Select(peer => peer.Number).ToHashSet();
            foreach (var member in added)
            {
                var number = Enumerable.Range(1, 10).Select(value => (ushort)value).First(value => !used.Contains(value));
                used.Add(number);
                allocation.RetiredNumbers.Remove(number);
                var peer = new Peer(allocation, member, number);
                allocation.Peers.Add(peer);
                identities.Add(peer.Identity, peer);
            }
            allocation.TopologyPending = added.Length != 0;
            return true;
        }
    }

    public JsonObject? Descriptor(string session, string account)
    {
        lock (gate)
        {
            if (!listening) return null;
            if (!sessions.TryGetValue(session, out var allocation) || !Valid(allocation)) return null;
            var peer = allocation.Peers.SingleOrDefault(p => p.Member.Account == account);
            return peer is null ? null : new JsonObject
            {
                ["endpoints"] = new JsonArray(new JsonObject
                {
                    ["protocol"] = "DTLS", ["identityHint"] = peer.Identity, ["psk"] = Convert.ToHexString(peer.Psk),
                    ["server"] = new JsonObject { ["host"] = options.PublicHost, ["port"] = options.PublicPort }
                }),
                ["secret"] = Encoding.ASCII.GetString(peer.Secret)
            };
        }
    }

    public byte[]? LookupPsk(byte[] identity)
    {
        lock (gate)
            return TryPeer(identity, out var peer) && Valid(peer.Allocation) ? peer.Psk.ToArray() : null;
    }

    public SignalingSnapshot? Attach(byte[] identity, string connection)
    {
        lock (gate)
        {
            if (!TryPeer(identity, out var peer) || !Valid(peer.Allocation) || peer.Connection is not null) return null;
            peer.Connection = connection;
            peer.LastSeen = DateTimeOffset.UtcNow;
            return Snapshot(peer);
        }
    }

    public bool Register(string session, string account, string connection, byte[] secret, SignalingDelivery reply)
    {
        lock (gate)
        {
            var peer = Find(session, account, connection);
            if (peer is null || !CryptographicOperations.FixedTimeEquals(peer.Secret, secret)) return false;
            if (!peer.Out.Writer.TryWrite(reply)) { peer.Allocation.Failed = true; return false; }
            peer.Registered = true;
            peer.LastSeen = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool AllRegistered(string session, string? connection = null)
    {
        lock (gate)
        {
            var allocation = Select(session, connection);
            return allocation is not null && allocation.Peers.All(p => p.Registered);
        }
    }

    public bool Activate(string session, string? connection = null)
    {
        lock (gate)
        {
            var allocation = Select(session, connection);
            if (allocation is null || !allocation.Peers.All(p => p.Registered)) return false;
            if (!allocation.Active)
            {
                allocation.Active = true;
                allocation.TopologyPending = false;
                return true;
            }
            if (!allocation.TopologyPending) return false;
            allocation.TopologyPending = false;
            return true;
        }
    }

    public bool Touch(string session, string account, string connection)
    {
        lock (gate)
        {
            var peer = Find(session, account, connection);
            if (peer is null) return false;
            peer.LastSeen = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool IsAlive(string session)
    {
        lock (gate) return sessions.TryGetValue(session, out var allocation) && Valid(allocation);
    }

    public bool IsActive(string session)
    {
        lock (gate) return sessions.TryGetValue(session, out var allocation) && Valid(allocation) && allocation.Active;
    }

    public bool IsAttached(string session, string account, string connection)
    { lock (gate) return Find(session, account, connection) is not null; }

    public bool Queue(string session, string account, SignalingDelivery delivery, string? connection = null)
    {
        lock (gate)
        {
            var allocation = Select(session, connection);
            if (allocation is null) return false;
            var peer = allocation.Peers.SingleOrDefault(p => p.Member.Account == account);
            if (peer is null || !peer.Registered) return false;
            if (peer.Out.Writer.TryWrite(delivery)) return true;
            allocation.Failed = true;
            return false;
        }
    }

    public SignalingDelivery? Dequeue(string session, string account, string connection)
    {
        lock (gate)
            return Find(session, account, connection) is { } peer && peer.Out.Reader.TryRead(out var message) ? message : null;
    }

    public void Detach(string session, string account, string connection)
    {
        lock (gate)
        {
            var peer = Find(session, account, connection);
            if (peer is not null) peer.Allocation.Failed = true;
        }
    }

    public void Remove(string session)
    {
        lock (gate)
        {
            if (sessions.Remove(session, out var allocation)) RemovePeers(allocation);
        }
    }

    private Allocation Create(string session, string representative, IReadOnlyList<SignalingMember> members, DateTimeOffset deadline)
    {
        var allocation = new Allocation(session, representative, deadline)
        { HardDeadline = DateTimeOffset.UtcNow.AddSeconds(options.MaxSessionSeconds) };
        ushort number = 1;
        foreach (var member in members.OrderBy(m => m.Account, StringComparer.Ordinal))
        {
            var peer = new Peer(allocation, member, number++);
            allocation.Peers.Add(peer);
            identities.Add(peer.Identity, peer);
        }
        return allocation;
    }

    private void RemovePeers(Allocation allocation)
    {
        foreach (var peer in allocation.Peers) RemovePeer(peer);
    }

    private void RemovePeer(Peer peer)
    {
        identities.Remove(peer.Identity);
        CryptographicOperations.ZeroMemory(peer.Psk);
        CryptographicOperations.ZeroMemory(peer.Secret);
        peer.Out.Writer.TryComplete();
    }

    private static void ValidateMembers(string representative, IReadOnlyList<SignalingMember> members, bool allowSingle = false)
    {
        var minimum = allowSingle ? 1 : 2;
        if (members.Count < minimum || members.Count > 10
            || members.Select(m => m.Account).Distinct().Count() != members.Count
            || !members.Any(m => m.Account == representative)) throw new InvalidDataException("Invalid signaling membership.");
    }

    private bool Valid(Allocation allocation)
    {
        var now = DateTimeOffset.UtcNow;
        return listening && !allocation.Failed && now < allocation.HardDeadline
            && (allocation.Active ? allocation.Peers.All(p => now - p.LastSeen < TimeSpan.FromSeconds(options.IdleTimeoutSeconds)) : now < allocation.Deadline);
    }

    private Allocation? Select(string session, string? connection)
    {
        if (!sessions.TryGetValue(session, out var allocation) || !Valid(allocation)) return null;
        if (connection is null || allocation.Peers.Any(peer => peer.Connection == connection)) return allocation;
        return null;
    }

    private Peer? Find(string session, string account, string connection)
    {
        var allocation = Select(session, connection);
        return allocation?.Peers.SingleOrDefault(peer => peer.Member.Account == account && peer.Connection == connection);
    }

    public SignalingSnapshot? Current(string session, string account, string connection)
    {
        lock (gate)
            return Find(session, account, connection) is { } peer ? Snapshot(peer) : null;
    }

    public bool IsRetired(string session, string connection, ushort number)
    {
        lock (gate)
        {
            var allocation = Select(session, connection);
            return allocation is not null && allocation.RetiredNumbers.Contains(number);
        }
    }

    private bool TryPeer(byte[] identity, out Peer peer)
    {
        peer = null!;
        return identity.Length is > 0 and <= 39 && identity.All(b => b is >= 0x21 and <= 0x7e)
            && identities.TryGetValue(Encoding.ASCII.GetString(identity), out peer!);
    }

    private static SignalingSnapshot Snapshot(Peer peer) => new(peer.Allocation.Session, peer.Member.Account,
        peer.Number, peer.Member.Nonce, peer.Allocation.Peers.Single(p => p.Member.Account == peer.Allocation.Representative).Number,
        peer.Allocation.Peers.Select(p => new SignalingPeerInfo(p.Member.Account, p.Number, p.Member.Nonce)).ToArray());
}
