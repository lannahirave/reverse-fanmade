using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using ReVerse.Capture.Logging;
using ReVerse.Capture.Signaling;

namespace Provider.Tests;

internal static class UdpLoggingTests
{
    internal static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reverse-udp-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var disabled = new UdpTrafficLog(Path.Combine(folder, "disabled"), new()))
                disabled.Record("ignored", bytes: new byte[12]);
            Assert.That(!Directory.Exists(Path.Combine(folder, "disabled")), "disabled UDP logging creates no files");
            var bytes = Encoding.UTF8.GetBytes(new string('x', 300_000));
            using (var log = new UdpTrafficLog(folder, new() { Enabled = true, MaxQueuedBytes = 64 * 1024 * 1024 }))
            {
                for (var i = 0; i < 22; i++) log.Record("rotation_test", sender: "alice", recipient: "bob", details: new { index = i }, bytes: bytes);
                Assert.That(log.DroppedRecords == 0, "rotation records unexpectedly dropped");
            }
            var files = Directory.GetFiles(folder, "udp-*.jsonl");
            var readable = Directory.GetFiles(folder, "udp-*.log");
            Assert.That(files.Length >= 2 && readable.Length >= 2, "both UDP formats roll at 10 MB");
            Assert.That(files.Concat(readable).All(f => new FileInfo(f).Length < UdpTrafficLog.FileSizeLimitBytes + 1_500_000), "roll threshold exceeded by more than one record");
            Assert.That(files.Concat(readable).Any(f => new FileInfo(f).Length >= UdpTrafficLog.FileSizeLimitBytes), "UDP did not use 10 MB limit");
            var records = Read(folder);
            Assert.That(records.Length == 22, "shutdown did not drain all records");
            var text = string.Join("\n", readable.Select(File.ReadAllText));
            foreach (var record in records)
            {
                Assert.That(text.Contains("event=" + record["eventId"]!.GetValue<string>(), StringComparison.Ordinal), "human/JSON event mismatch");
                Assert.Bytes(bytes, Convert.FromBase64String(record["payload"]!["base64"]!.GetValue<string>()), "raw payload changed");
            }
            var credential = Encoding.ASCII.GetBytes("do-not-write-registration-secret");
            var fragment = new WirePacket(0x80, "abcd", "1234", 1,
                [new WireFragment(0x0d, 0x1c, 0, credential)]).Encode();
            var masked = UdpPayload.Describe(fragment, "wire_in");
            Assert.That(masked.Base64 is null && masked.Hex is null && masked.Decoded is not null, "credential fragment was logged");
            Assert.That(UdpPayload.Describe(credential, "registration").Hex is null, "registration was not masked");
            var blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "not a directory");
            using (var failed = new UdpTrafficLog(blocked, new() { Enabled = true })) failed.Record("disk_failure", bytes: new byte[] { 1, 2 });
            QueueBounds();
            Routing(Path.Combine(folder, "routing"));
        }
        finally { Directory.Delete(folder, recursive: true); }
        Console.WriteLine("PASS paired UDP files, 10 MB rotation, payload fidelity, masking, shutdown drain, queue gaps and disk failure");
    }

    private static void QueueBounds()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<UdpLogRecord>();
        using var log = new UdpTrafficLog(new UdpLogOptions { Enabled = true, QueueCapacity = 1 }, record =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            written.Enqueue(record);
        });
        try
        {
            log.Record("first");
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), "log writer did not start");
            log.Record("second");
            log.Record("overflow");
            Assert.That(log.DroppedRecords == 1, "full log queue was not counted");
        }
        finally { release.Set(); }
        log.Dispose();
        Assert.That(written.Any(r => r.Stage == "logging_gap") && written.Any(r => r.Stage == "second"), "drop report or final queued record missing");
        using var byteLimit = new UdpTrafficLog(new UdpLogOptions { Enabled = true, MaxQueuedBytes = 4096 }, written.Enqueue);
        byteLimit.Record("too_large", bytes: new byte[1]);
        byteLimit.Dispose();
        Assert.That(byteLimit.DroppedRecords == 1, "copied-byte budget was not enforced");
        var attempts = 0;
        using var faultedWriter = new UdpTrafficLog(new UdpLogOptions { Enabled = true }, record =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("injected sink failure");
            written.Enqueue(record);
        });
        faultedWriter.Record("fails");
        faultedWriter.Record("survives");
        faultedWriter.Dispose();
        Assert.That(written.Any(r => r.Stage == "survives"), "writer failure prevented later events or shutdown");
    }

    private static void Routing(string folder)
    {
        using var log = new UdpTrafficLog(folder, new() { Enabled = true });
        using var room = new Room(members: [new("alice", 1), new("bob", 2), new("carol", 2)], udp: log);
        room.Ready();
        var body = Bytes.Route([2, 3], Bytes.Block(1, 2));
        room.Conversations["alice"].Application(4, 1, 0x10, body, "multicast");
        Assert.That(room.Take("bob")?.ForwardingId == "multicast" && room.Take("carol")?.ForwardingId == "multicast", "recipient queues lost correlation");
        Assert.Throws<WireFormatException>(() => room.Conversations["alice"].Application(4, 1, 0x10,
            Bytes.Route([2, 99], Bytes.Block(1, 2)), "rejected"), "foreign route accepted");
        room.Empty();
        for (var i = 0; i < 128; i++)
            Assert.That(room.Directory.Queue(Room.Session, "bob", new SignalingDelivery(4, 1, 0x10, [])), "queue fill failed");
        Assert.Throws<WireFormatException>(() => room.Conversations["alice"].Application(4, 1, 0x10,
            Bytes.Route([2], Bytes.Block(1, 2)), "overflow"), "full peer queue accepted delivery");
        log.Dispose();
        var records = Read(folder);
        Assert.That(records.Count(r => r["stage"]!.GetValue<string>() == "forward_queued" && r["forwardingId"]!.GetValue<string>() == "multicast") == 2, "multicast recipients not logged individually");
        Assert.That(records.Any(r => r["stage"]!.GetValue<string>() == "forward_rejected" && r["forwardingId"]!.GetValue<string>() == "rejected"), "rejection reason missing");
        Assert.That(records.Any(r => r["stage"]!.GetValue<string>() == "forward_dropped" && r["forwardingId"]!.GetValue<string>() == "overflow"), "peer queue drop missing");
    }

    internal static JsonNode[] Read(string folder) => Directory.GetFiles(folder, "udp-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line => JsonNode.Parse(line)!).ToArray();

    internal static void CheckNetwork(string folder, IReadOnlyList<Credential> credentials)
    {
        var records = Read(folder);
        var text = string.Join("\n", Directory.GetFiles(folder, "udp-*.log").Select(File.ReadAllText));
        var all = text + string.Join("\n", records.Select(r => r.ToJsonString()));
        foreach (var credential in credentials)
            foreach (var secret in new[] { credential.Identity, credential.Secret, credential.Psk, Convert.ToHexString(credential.SecretBytes), Convert.ToBase64String(credential.SecretBytes) })
                Assert.That(!all.Contains(secret, StringComparison.Ordinal), "UDP log exposed credentials");
        foreach (var stage in new[] { "datagram_received", "datagram_sent", "association_authenticated", "plaintext_received", "packet_parsed",
            "application_received", "delivery_dequeued", "packet_sent", "packet_acknowledged", "packet_retransmitted", "datagram_size_rejected", "association_closed" })
            Assert.That(records.Any(r => r["stage"]!.GetValue<string>() == stage), "missing UDP stage " + stage);
        Assert.That(records.Any(r => r["stage"]!.GetValue<string>() == "packet_parsed" && r["details"]?["isDuplicate"]?.GetValue<bool>() == true), "wire retry not recorded");
        Assert.That(records.Any(r => r["stage"]!.GetValue<string>() == "datagram_received" && r["payload"]?["byteCount"]?.GetValue<int>() == 4096
            && r["payload"]?["base64"]?.GetValue<string>() is { Length: > 0 }), "oversized rejected datagram was not captured in full");
        var forwards = records.Where(r => r["stage"]!.GetValue<string>() == "forward_queued").ToArray();
        Assert.That(forwards.Length == 3, "forwarding logs duplicated or missing");
        foreach (var forward in forwards)
        {
            var id = forward["forwardingId"]!.GetValue<string>();
            var related = records.Where(r => r["forwardingId"]?.GetValue<string>() == id).ToArray();
            foreach (var stage in new[] { "application_received", "delivery_dequeued", "packet_sent", "datagram_sent" })
                Assert.That(related.Any(r => r["stage"]!.GetValue<string>() == stage), "forwarding correlation missing at " + stage);
            var delivery = related.Single(r => r["stage"]!.GetValue<string>() == "delivery_dequeued");
            var reader = new WireReader(Convert.FromBase64String(delivery["payload"]!["base64"]!.GetValue<string>()));
            _ = reader.ReadBytes16(); _ = reader.ReadUInt16();
            Assert.Bytes(Convert.FromBase64String(forward["payload"]!["base64"]!.GetValue<string>()), reader.ReadBytes16().Span, "forwarded payload differs from received peer payload");
            Assert.That(forward["sender"]!.GetValue<string>() != forward["recipient"]!.GetValue<string>(), "missing sender/recipient identities");
        }
        foreach (var record in records)
            Assert.That(text.Contains("event=" + record["eventId"]!.GetValue<string>(), StringComparison.Ordinal), "real UDP formats differ");
        Console.WriteLine("PASS real UDP sender/recipient attribution, raw bytes, forwarding correlation, duplicate/ACK events and credential masking");
    }
}
