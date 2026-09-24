using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using ReVerse.Capture.Capturing;
using ReVerse.Capture.Signaling;
using ReVerse.Traffic;

namespace Provider.Tests;

internal static class NetworkTests
{
    private static string[] ReadLogLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Artifacts() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts", Guid.NewGuid().ToString("N")));

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    internal static async Task ProviderSmoke(bool peerDiagnostics = false)
    {
        const string session = "network-room";
        var options = Room.NewOptions();
        options.PeerDiagnostics = peerDiagnostics;
        options.Port = options.PublicPort = FreePort();
        options.HandshakeTimeoutSeconds = 2;
        options.Validate();
        var directory = new SignalingDirectory(options);
        var folder = Artifacts();
        using var log = new RequestLog(folder);
        using var detailed = new DetailedTrafficLog(folder);
        using var provider = new SignalingProvider(options, directory, log, detailed, NullLogger<SignalingProvider>.Instance);
        var endpoint = new IPEndPoint(IPAddress.Loopback, options.Port);
        var credentials = new List<Credential>();
        try
        {
            Assert.That(!directory.IsListening, "provider ready before bind");
            await provider.StartAsync(CancellationToken.None);
            Assert.That(directory.IsListening, "StartAsync did not bind before returning");
            Assert.That(directory.Allocate(session, "bob", [new("bob", 0xaabbccdd), new("alice", 0x01020304)],
                DateTimeOffset.UtcNow.AddSeconds(30)), "provider allocation");
            var aliceCredential = Credential.From(directory.Descriptor(session, "alice")!);
            var bobCredential = Credential.From(directory.Descriptor(session, "bob")!);
            credentials.AddRange([aliceCredential, bobCredential]);
            var wrongKey = Convert.FromHexString(aliceCredential.Psk);
            wrongKey[0] ^= 0xff;
            RejectHandshake(endpoint, aliceCredential with { Psk = Convert.ToHexString(wrongKey) });

            using var alice = new ProviderPeer(endpoint, aliceCredential);
            using var bob = new ProviderPeer(endpoint, bobCredential);
            alice.Startup();
            bob.Startup();
            var registration = alice.SendApplication(1, 1, 1, Bytes.Sized(aliceCredential.SecretBytes));
            var reply = alice.Application();
            Assert.That(reply is { Family: 1, Operation: 1, Qualifier: 2, Descriptor: 0x1c }, "DTLS registration reply envelope");
            Assert.Bytes([0, 0, .. Bytes.Text(session), 0, 1, 0, 2], reply.Body.Span, "DTLS registration reply layout");
            Assert.That(!directory.IsActive(session), "provider activated before second actual registration");
            alice.Resend(registration);
            alice.NoApplication(TimeSpan.FromMilliseconds(180));
            bob.NoApplication(TimeSpan.FromMilliseconds(100));
            bob.SendApplication(1, 1, 1, Bytes.Sized(bobCredential.SecretBytes));
            reply = bob.Application();
            Assert.That(reply is { Family: 1, Operation: 1, Qualifier: 2 }, "last registrant received readiness ahead of registration reply");
            Assert.Bytes([0, 0, .. Bytes.Text(session), 0, 2, 0, 2], reply.Body.Span, "last registration reply layout");
            var expectedReady = Convert.FromHexString("000C6E6574776F726B2D726F6F6D0100020005616C6963650001020003626F62000202");
            foreach (var client in new[] { alice, bob })
            {
                var ready = client.Application();
                Assert.That(ready is { Family: 3, Operation: 7, Qualifier: 0x10, Descriptor: 0x1c }, "DTLS readiness envelope");
                Assert.Bytes(expectedReady, ready.Body.Span, "DTLS readiness bytes");
            }
            Assert.That(directory.IsActive(session), "provider not active after both registrations");
            var block = peerDiagnostics
                ? PeerDiagnosticsTests.Block(PeerDiagnosticsTests.Encode([2, 1, 1, 2, 3, 4, 0xaa, 0xbb, 0xcc, 0xdd, 0, 0, 0, 1, 0, 1, 1]))
                : Bytes.Block(0x01020304, 0xaabbccdd);
            alice.SkipUnreliableBytes(37);
            var forward = alice.SendApplication(4, 1, 0x10, Bytes.Route([2], block), descriptor: 0x32);
            var delivered = bob.Application();
            Assert.That(delivered is { Family: 4, Operation: 1, Qualifier: 0x10, Descriptor: 0x42, Routing: 0, Auxiliary: 0 },
                "provider trusted spoofed routing/auxiliary or used wrong channel");
            Assert.Bytes([.. Bytes.Text(session), 0, 1, .. Bytes.Sized(block)], delivered.Body.Span,
                "provider changed nested block or used untrusted context/sender");
            alice.Resend(forward);
            var unreliableDatagrams = bob.UnreliableDatagrams;
            bob.NoApplication(TimeSpan.FromMilliseconds(650));
            Assert.That(bob.UnreliableDatagrams == unreliableDatagrams, "unreliable forwarding was retransmitted");
            alice.SkipUnreliableBytes(19);
            alice.SendApplication(4, 1, 0x10, Bytes.Route([2], block), descriptor: 0x32);
            Assert.Bytes([.. Bytes.Text(session), 0, 1, .. Bytes.Sized(block)], bob.Application().Body.Span,
                "lost channel-3 bytes blocked a later complete datagram");
            var reverse = Bytes.Block(0xaabbccdd, 0x01020304, secondary: false);
            bob.SendApplication(4, 1, 0x10, Bytes.Route([1], reverse), descriptor: 0x32);
            Assert.Bytes([.. Bytes.Text(session), 0, 2, .. Bytes.Sized(reverse)], alice.Application().Body.Span,
                "reverse route did not use authenticated second sender");
            directory.Remove(session);
            foreach (var credential in credentials)
            {
                Assert.That(directory.LookupPsk(credential.IdentityBytes) is null, "removed provider key remains valid");
                RejectHandshake(endpoint, credential);
            }
        }
        finally { await provider.StopAsync(CancellationToken.None); }
        Assert.That(!directory.IsListening, "stopped provider advertises listening");
        using (var rebound = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            rebound.ExclusiveAddressUse = true;
            rebound.Bind(endpoint);
        }
        var records = System.IO.Directory.GetFiles(folder, "*.jsonl").SelectMany(ReadLogLines).Select(line => JsonNode.Parse(line)!).ToArray();
        foreach (var stage in new[] { "dtls_authenticated", "application_registered", "peer_payload_forwarded", "all_members_registered_readiness_sent_NOT_gameplay_verified" })
            Assert.That(records.Any(r => r["stage"]?.GetValue<string>() == stage), "real provider missing stage " + stage);
        Assert.That(records.Count(r => r["stage"]?.GetValue<string>() == "application_registered") == 2, "wire retry duplicated registration");
        Assert.That(records.Count(r => r["stage"]?.GetValue<string>() == "peer_payload_forwarded") == 3, "wire retry duplicated forwarding");
        var observed = records.Where(r => r["stage"]?.GetValue<string>() == "peer_payload_observed")
            .Select(r => r["details"]!["payload"]!).ToArray();
        Assert.That(observed.Length == 3, "real provider missing nested metadata or audited a duplicate");
        Assert.That(observed.All(p => p["tag"]!.GetValue<int>() == 2 && p["version"]!.GetValue<int>() == 1
            && p["primaryLength"]!.GetValue<int>() == (peerDiagnostics && p["secondaryLength"]!.GetValue<int>() > 0 ? 21 : 4) && p["envelopeValid"]!.GetValue<bool>()
            && p["outcome"]!.GetValue<string>() == "queued"), "real provider metadata contract");
        Assert.That(observed.Count(p => p["secondaryLength"]!.GetValue<int>() == 3 && p["secondaryMetadata"]!.GetValue<int>() == 0xbeef) == 2
            && observed.Count(p => p["secondaryLength"]!.GetValue<int>() == 0 && p["secondaryMetadata"] is null) == 1, "real secondary/no-secondary metadata");
        var logs = string.Join('\n', records.Select(r => r.ToJsonString()));
        var inner = records.Where(r => r["stage"]?.GetValue<string>() == "peer_primary_observed").ToArray();
        Assert.That(inner.Length == (peerDiagnostics ? 3 : 0), "diagnostics opt-in did not reach real logs");
        if (peerDiagnostics)
        {
            Assert.That(inner.Count(r => r["details"]!["primary"]!["readyRecordCount"]!.GetValue<int>() == 1) == 2,
                "real log missing ready record observations");
            Assert.That(inner.Count(r => r["details"]!["primary"]!["crcValid"]!.GetValue<bool>() == false) == 1,
                "invalid inner CRC not observed while still forwarded");
            Assert.That(inner.All(r => r["details"]!["outcome"]!.GetValue<string>() == "queued"), "inner diagnostics altered forwarding outcome");
        }
        foreach (var credential in credentials)
            foreach (var privateValue in new[] { credential.Identity, credential.Psk, credential.Secret })
                Assert.That(!logs.Contains(privateValue, StringComparison.Ordinal), "real provider leaked credentials in metadata logs");
    }

    internal static async Task MembershipReduction()
    {
        const string session = "single-member-room";
        var options = Room.NewOptions();
        options.Port = options.PublicPort = FreePort();
        options.HandshakeTimeoutSeconds = 2;
        options.Validate();
        var directory = new SignalingDirectory(options);
        var folder = Artifacts();
        using var log = new RequestLog(folder);
        using var detailed = new DetailedTrafficLog(folder);
        using var provider = new SignalingProvider(options, directory, log, detailed, NullLogger<SignalingProvider>.Instance);
        var endpoint = new IPEndPoint(IPAddress.Loopback, options.Port);
        try
        {
            await provider.StartAsync(CancellationToken.None);
            Assert.That(directory.Allocate(session, "bob",
                [new("bob", 0xaabbccdd), new("alice", 0x01020304)], DateTimeOffset.UtcNow.AddSeconds(30)),
                "single-member fixture allocation");
            var aliceCredential = Credential.From(directory.Descriptor(session, "alice")!);
            var bobCredential = Credential.From(directory.Descriptor(session, "bob")!);
            using var alice = new ProviderPeer(endpoint, aliceCredential);
            using var bob = new ProviderPeer(endpoint, bobCredential);
            alice.Startup();
            bob.Startup();
            alice.SendApplication(1, 1, 1, Bytes.Sized(aliceCredential.SecretBytes));
            Assert.That(alice.Application() is { Family: 1, Operation: 1, Qualifier: 2 }, "single-member alice registration reply");
            bob.SendApplication(1, 1, 1, Bytes.Sized(bobCredential.SecretBytes));
            Assert.That(bob.Application() is { Family: 1, Operation: 1, Qualifier: 2 }, "single-member bob registration reply");
            Assert.That(alice.Application() is { Family: 3, Operation: 7, Qualifier: 0x10 }
                && bob.Application() is { Family: 3, Operation: 7, Qualifier: 0x10 }, "single-member readiness");
            Assert.That(directory.Replace(session, "bob", [new("bob", 0xaabbccdd)], DateTimeOffset.UtcNow.AddSeconds(30)),
                "single-member live replacement");
            bob.SendApplication(4, 1, 0x10, Bytes.Route([1], Bytes.Block(0xaabbccdd, 0x01020304)));
            bob.NoApplication(TimeSpan.FromMilliseconds(350));
            Assert.That(directory.IsAlive(session) && directory.IsActive(session)
                && directory.LookupPsk(bobCredential.IdentityBytes) is not null,
                "remaining provider transport was revoked");
        }
        finally
        {
            directory.Remove(session);
            await provider.StopAsync(CancellationToken.None);
        }
        var records = System.IO.Directory.GetFiles(folder, "*.jsonl").SelectMany(ReadLogLines)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.That(records.Any(r => r["stage"]?.GetValue<string>() == "peer_payload_observed"
            && r["details"]!["payload"]!["outcome"]?.GetValue<string>() == "stale_topology_dropped"),
            "single-member stale route was not audited");
    }

    internal static async Task Lifecycle()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var options = Room.NewOptions();
        options.Port = options.PublicPort = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        options.Enabled = false;
        var directory = new SignalingDirectory(options);
        var folder = Artifacts();
        using var log = new RequestLog(folder);
        using var detailed = new DetailedTrafficLog(folder);
        using (var disabled = new SignalingProvider(options, directory, log, detailed, NullLogger<SignalingProvider>.Instance))
        {
            await disabled.StartAsync(CancellationToken.None);
            Assert.That(!directory.IsListening && !directory.Allocate("disabled", "carol", Room.Members, DateTimeOffset.UtcNow.AddMinutes(1)), "disabled provider advertised readiness");
            await disabled.StopAsync(CancellationToken.None);
        }
        options.Enabled = true;
        using var failed = new SignalingProvider(options, directory, log, detailed, NullLogger<SignalingProvider>.Instance);
        var rejected = false;
        try { await failed.StartAsync(CancellationToken.None); }
        catch (DtlsHostException) { rejected = true; }
        Assert.That(rejected && !directory.IsListening, "failed UDP bind advertised credentials");
        await failed.StopAsync(CancellationToken.None);
    }

    internal static async Task NegativeControls()
    {
        foreach (var reply in new[] { 19, 21 })
        {
            var options = Room.NewOptions();
            options.Port = options.PublicPort = FreePort();
            options.NegativeControlReply = reply;
            options.Validate();
            var directory = new SignalingDirectory(options);
            var folder = Artifacts();
            using var log = new RequestLog(folder);
            using var detailed = new DetailedTrafficLog(folder);
            using var provider = new SignalingProvider(options, directory, log, detailed, NullLogger<SignalingProvider>.Instance);
            try
            {
                await provider.StartAsync(CancellationToken.None);
                Assert.That(directory.Allocate("negative", "alice", [new("alice", 1), new("bob", 2)],
                    DateTimeOffset.UtcNow.AddSeconds(30)), "negative-control allocation");
                using var client = new ProviderPeer(new(IPAddress.Loopback, options.Port), Credential.From(directory.Descriptor("negative", "alice")!));
                client.Startup(reply);
                Assert.That(!directory.IsActive("negative") && !directory.AllRegistered("negative"), "negative control activated transport");
            }
            finally { await provider.StopAsync(CancellationToken.None); }
            var records = System.IO.Directory.GetFiles(folder, "*.jsonl").SelectMany(ReadLogLines).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.That(records.Any(r => r["stage"]?.GetValue<string>() == $"negative_control_reply{reply}_zero"), "negative-control diagnostic missing");
            Assert.That(!records.Any(r => r["stage"]?.GetValue<string>() is "application_registered" or "experimental_reply21_and_gate5"), "negative control logged a success gate");
        }
    }

    private static void RejectHandshake(IPEndPoint endpoint, Credential credential)
    {
        using var udp = new LoopbackUdp(endpoint);
        try
        {
            var transport = Connect(udp, credential);
            transport.Close();
        }
        catch (IOException) { return; }
        throw new InvalidOperationException("Invalid/revoked PSK completed a DTLS handshake.");
    }

    internal static DtlsTransport Connect(LoopbackUdp udp, Credential credential) => new DtlsClientProtocol().Connect(
        new PskClient(credential.IdentityBytes, Convert.FromHexString(credential.Psk)), udp);



    internal static async Task<int> HttpProbe()
    {
        try
        {
            var line = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var descriptor = JsonNode.Parse(line ?? "")!.AsObject();
            var server = descriptor["endpoints"]![0]!["server"]!;
            Assert.That(server["host"]!.GetValue<string>() == "127.0.0.1", "probe only supports loopback");
            var endpoint = new IPEndPoint(IPAddress.Loopback, server["port"]!.GetValue<int>());
            var credential = Credential.From(descriptor);
            using var udp = new LoopbackUdp(endpoint);
            var connected = Connect(udp, credential);
            try
            {
                Console.WriteLine("CONNECTED");
                var command = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.That(command == "VERIFY_REVOKED", "missing cancellation confirmation from test harness");
                RejectHandshake(endpoint, credential);
                Console.WriteLine("REVOKED");
            }
            finally { connected.Close(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Credential probe failed: " + error.GetType().Name);
            return 1;
        }
    }
}



internal sealed class ProviderPeer : IDisposable
{
    private readonly LoopbackUdp udp;
    private readonly DtlsTransport transport;
    private readonly string clientId = Guid.NewGuid().ToString("N")[..8];
    private readonly string serverId = Guid.NewGuid().ToString("N")[..8];
    private readonly HashSet<ulong> received = [];
    private readonly Queue<WireContent> controls = new();
    private readonly Queue<WireApplication> applications = new();
    private ulong sequence = 1;
    private readonly Dictionary<int, ulong> offsets = [];
    internal int UnreliableDatagrams { get; private set; }

    internal ProviderPeer(IPEndPoint endpoint, Credential credential)
    {
        udp = new LoopbackUdp(endpoint);
        try { transport = NetworkTests.Connect(udp, credential); }
        catch { udp.Dispose(); throw; }
    }

    private byte[] Send(WireContent content)
    {
        var packet = new WirePacket(0x80, clientId, serverId, sequence++, [content]).Encode();
        transport.Send(packet, 0, packet.Length);
        return packet;
    }

    internal void Resend(byte[] packet) => transport.Send(packet, 0, packet.Length);

    internal void SkipUnreliableBytes(ulong bytes) => offsets[3] = offsets.GetValueOrDefault(3) + bytes;

    internal void Startup(int? negativeControl = null)
    {
        Send(new WireControl(3, []));
        Assert.That(Control() is WireControl { Type: 4, Values.Count: 0 }, "real provider probe reply");
        Send(new WireSettings([new(0, 5000), new(1, 3), new(2, 25)]));
        Assert.That(Control() is WireSettings settings && settings.Entries.SequenceEqual(new WireSetting[] { new(0, 5000), new(1, 3), new(2, 25) }), "real provider settings reply");
        Send(new WireControl(0x12, [1, 1]));
        Assert.That(Control() is WireControl { Type: 0x13 } nineteen && nineteen.Values.SequenceEqual(new ulong[] { negativeControl == 19 ? 0UL : 1UL }), "real provider reply19");
        if (negativeControl == 19) { NoStartupGate(); return; }
        Send(new WireControl(0x14, [1]));
        Assert.That(Control() is WireControl { Type: 0x15 } twentyOne && twentyOne.Values.SequenceEqual(new ulong[] { negativeControl == 21 ? 0UL : 1UL }), "real provider reply21");
        if (negativeControl == 21) { NoStartupGate(); return; }
        Assert.That(Control() is WireControl { Type: 5, Values.Count: 0 }, "real provider gate5");
    }

    private void NoStartupGate()
    {
        NoApplication(TimeSpan.FromMilliseconds(200));
        Assert.That(controls.Count == 0, "negative control sent an extra startup gate");
    }

    internal byte[] SendApplication(byte family, byte operation, byte qualifier, byte[] body, byte descriptor = 0x1c)
    {

        var bytes = new WireApplication(family, operation, qualifier, ushort.MaxValue, 0xdeadbeef, body).Encode();
        var channel = descriptor >> 4;
        var offset = offsets.GetValueOrDefault(channel);

        var packet = Send(new WireFragment(0x0f, descriptor, offset, bytes));
        offsets[channel] = offset + (ulong)bytes.Length;
        return packet;
    }

    private WireContent Control()
    {
        Until(() => controls.Count != 0);
        return controls.Dequeue();
    }

    internal WireApplication Application()
    {
        Until(() => applications.Count != 0);
        return applications.Dequeue();
    }

    internal void NoApplication(TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
        {
            Assert.That(applications.Count == 0, "unexpected/duplicate real provider application");
            Receive();
        }
        Assert.That(applications.Count == 0, "unexpected/duplicate real provider application");
    }

    private void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.That(timer.Elapsed < TimeSpan.FromSeconds(5), "real provider response deadline exceeded");
            Receive();
        }
    }

    private void Receive()
    {
        var buffer = new byte[transport.GetReceiveLimit()];
        var length = transport.Receive(buffer, 0, buffer.Length, 80);
        if (length < 0) return;
        var packet = WirePacket.Parse(buffer.AsMemory(0, length));
        Assert.That(packet.FirstId == serverId && (!packet.IsExtended || packet.SecondId == clientId), "provider directional wire binding");

        if (packet.Contents.Any(c => c is WireControl or WireSettings or WireFragment { Descriptor: 0x1c }))
            Send(new WireAcknowledgment([new(packet.Sequence, packet.Sequence)]));
        foreach (var fragment in packet.Contents.OfType<WireFragment>().Where(f => f.Descriptor == 0x42))
        {
            ++UnreliableDatagrams;
            InspectUnreliable(buffer.AsSpan(0, length), packet, fragment);
        }
        if (!received.Add(packet.Sequence)) return;
        foreach (var item in packet.Contents)
        {
            if (item is WireControl or WireSettings) controls.Enqueue(item);
            if (item is WireFragment fragment)
            {
                Assert.That(fragment.Descriptor == 0x42 || fragment.IsFinal, "smoke fixture unexpectedly fragmented");
                foreach (var application in WireApplication.ParseMany(fragment.Data))
                    applications.Enqueue(application with { Descriptor = fragment.Descriptor });
            }
        }
    }

    private static void InspectUnreliable(ReadOnlySpan<byte> bytes, WirePacket packet, WireFragment fragment)
    {
        Assert.That(fragment.IsFinal && fragment.HasOffset && !fragment.IsReliable,
            "channel 4 must be final, offset-framed and unretained");
        Assert.That(packet.Contents.Count == 1, "forwarding packet should contain one unfragmented application");

        var position = packet.IsExtended ? 5 : 1;
        position += 1 + bytes[position];
        if (packet.IsExtended) position += 1 + bytes[position];
        _ = Compact(bytes, ref position);
        Assert.That(bytes[position++] == fragment.Type, "raw forwarding content type");
        Assert.That(bytes[position++] == 0x40 && bytes[position++] == 0x42,
            "descriptor 0x42 must encode as compact bytes 40 42");
        Assert.That(Compact(bytes, ref position) == fragment.Offset, "channel 4 raw byte offset");
        var bodyLength = fragment.HasLength ? checked((int)Compact(bytes, ref position)) : bytes.Length - position;
        Assert.That(bodyLength == fragment.Data.Length && bodyLength == bytes.Length - position, "unfragmented body length");
        Assert.Bytes(fragment.Data.Span, bytes[position..], "raw unfragmented application differs");
    }

    private static ulong Compact(ReadOnlySpan<byte> bytes, ref int position)
    {
        var first = bytes[position++];
        var width = 1 << (first >> 6);
        ulong value = (ulong)(first & 0x3f);
        for (var i = 1; i < width; i++) value = (value << 8) | bytes[position++];
        return value;
    }

    public void Dispose()
    {
        try { transport.Close(); }
        finally { udp.Dispose(); }
    }
}

internal sealed class PskClient(byte[] identity, byte[] key) : PskTlsClient(new BcTlsCrypto(), identity, key)
{
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();
    protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    public override int GetHandshakeTimeoutMillis() => 2500;
}

internal sealed class LoopbackUdp : DatagramTransport, IDisposable
{
    private readonly Socket socket;
    internal LoopbackUdp(IPEndPoint endpoint)
    {
        Assert.That(IPAddress.IsLoopback(endpoint.Address), "test transport requires loopback");
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Connect(endpoint);
    }
    public int GetReceiveLimit() => 2048;
    public int GetSendLimit() => 2048;
    public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
    public int Receive(Span<byte> buffer, int waitMillis)
    {
        socket.ReceiveTimeout = waitMillis;
        try { return socket.Receive(buffer); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut) { return -1; }
        catch (SocketException) { throw new IOException("Loopback UDP receive failed."); }
    }
    public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));
    public void Send(ReadOnlySpan<byte> buffer) => socket.Send(buffer);
    public void Close() => socket.Dispose();
    public void Dispose() => socket.Dispose();
}
