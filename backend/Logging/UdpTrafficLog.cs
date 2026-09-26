using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReVerse.Capture.Signaling;
using ReVerse.Traffic;
using Serilog;

namespace ReVerse.Capture.Logging;

public sealed class UdpLogOptions
{
    public bool Enabled { get; set; }
    public int QueueCapacity { get; set; } = 2048;
    public int MaxQueuedBytes { get; set; } = 16 * 1024 * 1024;

    public void Validate()
    {
        if (QueueCapacity is < 1 or > 16384 || MaxQueuedBytes is < 4096 or > 64 * 1024 * 1024)
            throw new ArgumentException("UDP logging queue limits are out of range.");
    }
}

public sealed record UdpLogRecord(string EventId, DateTimeOffset TimestampUtc, string Stage,
    string? Connection, string? Session, string? Sender, string? Recipient, string? ForwardingId,
    object? Details, byte[] Bytes, string PayloadKind);

public sealed class UdpTrafficLog : IDisposable, IAsyncDisposable
{
    public const long FileSizeLimitBytes = 10_000_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object enqueueGate = new();
    private readonly UdpLogOptions options;
    private readonly Channel<UdpLogRecord> queue;
    private readonly Task writer;
    private readonly string run = Guid.NewGuid().ToString("N");
    private readonly Action<UdpLogRecord> write;
    private readonly JsonFileLog? jsonFile;
    private readonly JsonFileLog? textFile;
    private long nextId;
    private long queuedBytes;
    private long dropped;
    private long reportedDrops;
    private int closed;
    public bool Enabled => options.Enabled && Volatile.Read(ref closed) == 0;
    public long DroppedRecords => Interlocked.Read(ref dropped);

    public UdpTrafficLog(string directory, UdpLogOptions options)
    {
        options.Validate();
        this.options = options;
        queue = Channel.CreateBounded<UdpLogRecord>(new BoundedChannelOptions(options.QueueCapacity) { SingleReader = true });
        if (options.Enabled)
        {
            jsonFile = new JsonFileLog(directory, "udp", FileSizeLimitBytes);
            textFile = new JsonFileLog(directory, "udp", FileSizeLimitBytes, ".log");
            jsonFile.Failed += _ => Failure("JSON");
            textFile.Failed += _ => Failure("readable");
        }
        write = WriteFiles;
        writer = Task.Run(Drain);
    }

    internal UdpTrafficLog(UdpLogOptions options, Action<UdpLogRecord> write)
    {
        options.Validate();
        this.options = options;
        this.write = write;
        queue = Channel.CreateBounded<UdpLogRecord>(new BoundedChannelOptions(options.QueueCapacity) { SingleReader = true });
        writer = Task.Run(Drain);
    }

    private static void Failure(string format) => Log.ForContext("UserFacing", true)
        .Warning("Cannot write the {Format} UDP log. Check the log folder and available disk space.", format);

    public void Record(string stage, string? connection = null, string? session = null,
        string? sender = null, string? recipient = null, string? forwardingId = null,
        object? details = null, ReadOnlyMemory<byte> bytes = default, string payloadKind = "bytes")
    {
        lock (enqueueGate)
        {
            if (!Enabled) return;
            var cost = bytes.Length + 4096L;
            if (Interlocked.Add(ref queuedBytes, cost) > options.MaxQueuedBytes)
            {
                Interlocked.Add(ref queuedBytes, -cost);
                Interlocked.Increment(ref dropped);
                return;
            }
            var record = new UdpLogRecord(run + ":" + Interlocked.Increment(ref nextId), DateTimeOffset.UtcNow,
                stage, connection, session, sender, recipient, forwardingId, details, bytes.ToArray(), payloadKind);
            if (!queue.Writer.TryWrite(record))
            {
                Interlocked.Add(ref queuedBytes, -cost);
                Interlocked.Increment(ref dropped);
            }
        }
    }

    private async Task Drain()
    {
        try
        {
            await foreach (var record in queue.Reader.ReadAllAsync())
            {
                try { ReportDrops(); write(record); }
                catch (Exception exception)
                {
                    Log.ForContext("UserFacing", true).Error(exception, "A UDP log record could not be written. Network forwarding continues.");
                }
                finally { Interlocked.Add(ref queuedBytes, -(record.Bytes.Length + 4096L)); }
            }
            try { ReportDrops(); }
            catch (Exception exception)
            {
                Log.ForContext("UserFacing", true).Error(exception, "The final UDP logging gap could not be written.");
            }
        }
        finally
        {
            foreach (var file in new[] { jsonFile, textFile })
            {
                try { file?.Dispose(); }
                catch (IOException) { Failure("final"); }
            }
        }
    }

    private void ReportDrops()
    {
        var total = DroppedRecords;
        if (total == reportedDrops) return;
        write(new UdpLogRecord(run + ":" + Interlocked.Increment(ref nextId), DateTimeOffset.UtcNow,
            "logging_gap", null, null, null, null, null,
            new { droppedSinceLastReport = total - reportedDrops, droppedTotal = total }, [], "bytes"));
        reportedDrops = total;
        Log.ForContext("UserFacing", true).Warning("UDP logging could not keep up; {DroppedRecords} log records have been dropped. Network forwarding continues.", total);
    }

    private void WriteFiles(UdpLogRecord record)
    {
        var payload = UdpPayload.Describe(record.Bytes, record.PayloadKind);
        var description = DescribeStage(record.Stage);
        var line = JsonSerializer.Serialize(new
        {
            description, schemaVersion = 1, record.EventId, record.TimestampUtc, record.Stage, record.Connection,
            record.Session, record.Sender, record.Recipient, record.ForwardingId, record.Details, payload
        }, Json);
        jsonFile?.Write(line);
        var text = new StringBuilder()
            .AppendLine($"[{record.TimestampUtc:O}] {description} ({record.Stage})  event={record.EventId}")
            .AppendLine($"  {record.Sender ?? "unknown"} -> {record.Recipient ?? "unknown"}")
            .AppendLine($"  session={record.Session ?? "unassigned"} connection={record.Connection ?? "unassigned"} forwarding={record.ForwardingId ?? "none"}")
            .AppendLine("  details: " + JsonSerializer.Serialize(record.Details, Json))
            .AppendLine("  decoded: " + JsonSerializer.Serialize(payload.Decoded, Json))
            .AppendLine($"  payload: {payload.ByteCount} bytes; {payload.Note}");
        if (payload.Hex is not null)
            for (var offset = 0; offset < payload.Hex.Length; offset += 64)
                text.AppendLine($"    {offset / 2:x6}: {payload.Hex.Substring(offset, Math.Min(64, payload.Hex.Length - offset))}");
        if (payload.Utf8 is not null) text.AppendLine("  text: " + JsonSerializer.Serialize(payload.Utf8));
        textFile?.Write(text.ToString().TrimEnd());
    }

    private static string DescribeStage(string stage) => stage switch
    {
        "datagram_received" => "Received a UDP datagram from the remote endpoint",
        "datagram_sent" => "Sent a UDP datagram to the remote endpoint",
        "association_authenticated" => "Authenticated player; network endpoint and session membership are now linked",
        "plaintext_received" => "Decrypted a client packet; inspecting framing and fragments",
        "packet_parsed" => "Parsed packet; duplicate packets do not deliver messages again",
        "application_received" => "Assembled a complete client message",
        "forward_queued" => "Accepted a peer message into the recipient queue; not yet sent",
        "delivery_dequeued" => "Recipient worker removed a message from its queue for transmission",
        "packet_sent" => "Transmitted a packet; recipient processing is not yet known",
        "packet_retransmitted" => "Retransmitted a retained packet awaiting transport acknowledgment",
        "packet_acknowledged" => "Recipient acknowledged transport delivery; gameplay processing is not confirmed",
        "packet_send_failed" => "Packet transmission failed",
        "forward_dropped" => "Peer message could not be queued for this recipient",
        "forward_rejected" => "Peer message failed routing validation",
        "protocol_rejected" => "Connection rejected a packet or message that violates the protocol",
        "association_closed" => "Player association ended; final traffic counts follow",
        "logging_gap" => "UDP log records were dropped because the logging queue reached its limit",
        _ => stage.Replace('_', ' ')
    };

    public async ValueTask DisposeAsync()
    {
        lock (enqueueGate)
            if (Interlocked.Exchange(ref closed, 1) == 0) queue.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed record UdpPayload(int ByteCount, string? Hex, string? Base64, string? Utf8, string Note, object? Decoded)
{
    private static readonly UTF8Encoding Utf8Encoding = new(false, true);
    internal static UdpPayload Describe(byte[] bytes, string kind)
    {
        object? decoded = null;
        var note = "Exact bytes; gameplay meaning is not inferred.";
        if (kind == "registration") return new(bytes.Length, null, null, null, "Registration credential omitted.", null);
        if (kind == "datagram")
        {
            var records = new List<object>();
            var handshake = false;
            for (var offset = 0; offset + 13 <= bytes.Length && records.Count < 64;)
            {
                if (bytes[offset] is < 20 or > 25 || bytes[offset + 1] != 0xfe || bytes[offset + 2] is not (0xff or 0xfd))
                {
                    records.Add(new { offset, error = "Invalid DTLS record header; remaining bytes are uninterpreted" });
                    break;
                }
                var epoch = (bytes[offset + 3] << 8) | bytes[offset + 4];
                var size = (bytes[offset + 11] << 8) | bytes[offset + 12];
                records.Add(new { contentType = bytes[offset], epoch, bodyBytes = size,
                    recordSequence = Convert.ToHexString(bytes.AsSpan(offset + 5, 6)) });
                handshake |= epoch == 0;
                if (size > bytes.Length - offset - 13) break;
                offset += 13 + size;
            }
            decoded = records;
            if (handshake) return new(bytes.Length, null, null, null, "Unencrypted DTLS handshake contents omitted to exclude credentials.", decoded);
        }
        if (kind is "wire_in" or "wire_out")
        {
            try
            {
                var packet = WirePacket.Parse(bytes);
                decoded = new
                {
                    packet.Class, packet.Sequence,
                    contents = packet.Contents.Select<WireContent, object>(content => content switch
                    {
                        WireFragment fragment => new { type = "fragment", fragment.Channel, fragment.Descriptor, fragment.Offset, fragment.IsFinal, fragment.IsReliable, bytes = fragment.Data.Length },
                        WireAcknowledgment ack => new { type = "acknowledgment", ack.Delay, ack.Ranges },
                        WireControl control => new { type = "control", control.Code, control.Values,
                            meaning = control.Code switch { 1 => "no-op", 3 => "startup probe", 4 => "probe response", 5 => "startup gate",
                                0x12 => "startup request 18", 0x13 => "startup response 19", 0x14 => "startup request 20", 0x15 => "startup response 21", _ => "unknown control" } },
                        WireSettings settings => new { type = "settings", settings.Entries },
                        _ => new { type = "padding" }
                    }).ToArray()
                };
                if (kind == "wire_in" && packet.Contents.Any(c => c is WireFragment))
                    return new(bytes.Length, null, null, null, "Incoming fragment bytes omitted to exclude split registration credentials. Non-credential assembled messages are logged separately.", decoded);
            }
            catch (WireFormatException error)
            {
                return new(bytes.Length, null, null, null, "Invalid plaintext omitted because credential boundaries are unknown.", new { error = error.Message });
            }
        }
        if (kind == "peer")
        {
            try
            {
                var reader = new WireReader(bytes);
                var tag = reader.ReadByte(); var version = reader.ReadByte();
                var senderNonce = reader.ReadUInt32(); var recipientNonce = reader.ReadUInt32();
                var primaryLength = reader.ReadUInt16(); var secondaryLength = reader.ReadUInt16();
                var primary = reader.ReadBytes(primaryLength);
                ushort? secondaryMetadata = secondaryLength == 0 ? null : reader.ReadUInt16();
                _ = reader.ReadBytes(secondaryLength);
                reader.RequireEnd();
                decoded = new { secondaryMetadata, tag, version, senderNonce, recipientNonce, primaryLength, secondaryLength,
                    primary = PeerPrimaryDiagnostics.Inspect(primary.Span, senderNonce, recipientNonce) };
            }
            catch (WireFormatException error) { decoded = new { error = error.Message }; }
        }
        string? utf8;
        try { utf8 = Utf8Encoding.GetString(bytes); } catch (DecoderFallbackException) { utf8 = null; }
        return new(bytes.Length, Convert.ToHexString(bytes), Convert.ToBase64String(bytes), utf8, note, decoded);
    }
}
