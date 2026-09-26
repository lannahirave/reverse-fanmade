using System.Collections.Concurrent;
using ReVerse.Capture.Logging;
using Serilog;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Org.BouncyCastle.Tls;
using ReVerse.Capture.Capturing;
using ReVerse.Traffic;

namespace ReVerse.Capture.Signaling;


public sealed class SignalingProvider(SignalingOptions options, SignalingDirectory directory,
    RequestLog log, DetailedTrafficLog detailed, ILogger<SignalingProvider> logger, UdpTrafficLog? udp = null) : BackgroundService
{
    private readonly ConcurrentDictionary<string, (string Connection, string Session, string Account)> endpoints = new();
    private readonly ConcurrentDictionary<string, (string? Id, ulong Sequence, int Attempt)> activeSends = new();
    private DtlsUdpHost? host;
    private long hostEvents;
    private long droppedAuditRecords;
    private readonly Channel<object> audits = Channel.CreateBounded<object>(256);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) { Log.ForContext("UserFacing", true).Information("Player connection support is disabled in settings."); return; }
        host = new DtlsUdpHost(IPAddress.Parse(options.BindAddress), options.Port, options.MaxConnections,
            options.HandshakeTimeoutSeconds, directory.LookupPsk,
            (identity, transport, token) => Connected(identity, transport, token, null), HostStage,
            datagramLog: (direction, remote, bytes) =>
            {
                endpoints.TryGetValue(remote.ToString(), out var owner);
                var sending = direction == "backendToClient" && activeSends.TryGetValue(remote.ToString(), out var send) ? send : default;
                udp?.Record(direction == "clientToBackend" ? "datagram_received" : "datagram_sent",
                    owner.Connection, owner.Session,
                    direction == "clientToBackend" ? owner.Account ?? remote.ToString() : "backend",
                    direction == "clientToBackend" ? "backend" : owner.Account ?? remote.ToString(), sending.Id,
                    details: new { direction, wireSequence = sending.Sequence == 0 ? (ulong?)null : sending.Sequence,
                        attempt = sending.Attempt == 0 ? (int?)null : sending.Attempt, remoteAddress = remote.Address.ToString(), remotePort = remote.Port,
                        localAddress = options.BindAddress, localPort = options.Port }, bytes: bytes, payloadKind: "datagram");
                detailed.Write("udpDatagram", new
                {
                    direction, remoteAddress = remote.Address.ToString(), remotePort = remote.Port,
                    localPort = options.Port, payload = DetailedTrafficLog.Payload(bytes.Span)
                });
            }, connectedEndpoint: (identity, transport, token, remote) =>
            {
                try { Connected(identity, transport, token, remote); }
                finally { endpoints.TryRemove(remote.ToString(), out _); }
            },
            peerEvent: (remote, stage) => udp?.Record(stage, sender: remote.ToString(), recipient: "backend",
                details: new { remoteAddress = remote.Address.ToString(), remotePort = remote.Port, localPort = options.Port }));
        await host.StartAsync(cancellationToken);
        directory.SetListening(true);
        Log.ForContext("UserFacing", true).Information("Player connection support is listening on UDP port {Port}.", options.Port);
        logger.LogDebug("Signaling UDP bound at {Bind}:{Port}; advertised {PublicHost}:{PublicPort}; extended replies {Experimental}.",
            options.BindAddress, options.Port, options.PublicHost, options.PublicPort, options.ExperimentalReplies);
        if (options.ExperimentalReplies)
            logger.LogDebug("Signaling reply settings: type19={Reply19}, type21={Reply21}, registration fieldB={FieldB}.",
                options.Reply19, options.Reply21, options.RegistrationFieldBIsPeerNumber ? "peer-number" : options.RegistrationFieldB.ToString());
        if (options.NegativeControlReply is { } negativeReply)
            Log.ForContext("UserFacing", true).Warning("Connection test mode {Reply} is enabled. Players cannot complete connection setup in this mode.", negativeReply);
        if (options.PeerDiagnostics)
            logger.LogDebug("Peer diagnostics enabled; payload contents and identity values are excluded.");
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (host is null) return;
        var writer = WriteAudit();
        try { await await Task.WhenAny(host.Completion, writer).WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            directory.SetListening(false);
            try { await host.StopAsync(); }
            finally { audits.Writer.TryComplete(); }
            await writer;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        directory.SetListening(false);
        if (host is not null) await host.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        directory.SetListening(false);
        if (host is not null) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    private void HostStage(string stage)
    {
        var number = Interlocked.Increment(ref hostEvents);

        if (number <= 64 || number % 100 == 0)
            Audit("transport", null, null, null, new { stage, eventCount = number });
    }

    private void Connected(byte[] identity, DtlsTransport transport, CancellationToken stopping, IPEndPoint? remote)
    {
        string connection = Guid.NewGuid().ToString("N")[..12];
        var peer = directory.Attach(identity, connection);
        if (peer is null)
        {
            Audit("association_rejected", connection, null, null, new { reason = "expired_or_duplicate_identity" });
            udp?.Record("association_rejected", connection, sender: remote?.ToString(), recipient: "backend",
                details: new { reason = "expired_or_duplicate_identity" });
            return;
        }
        if (remote is not null) endpoints[remote.ToString()] = (connection, peer.Session, peer.Account);
        udp?.Record("association_authenticated", connection, peer.Session, peer.Account, "backend",
            details: new { remoteAddress = remote?.Address.ToString(), remotePort = remote?.Port,
                peerNumber = peer.Number, nonce = peer.Nonce, representative = peer.Representative,
                members = peer.Peers.Select(p => new { p.Account, p.Number, p.Nonce }).ToArray() });
        var deliveries = new Dictionary<ulong, SignalingDelivery>();
        long received = 0, sent = 0, forwarded = 0;
        long payloadRecords = 0, secondaryPayloads = 0;
        var peerSampling = new PeerDiagnosticSampling();
        var wire = new WireConnection(limits: new WireLimits { MaxPacketBytes = 4096 }, retryAfter: TimeSpan.FromMilliseconds(500));
        var conversation = new SignalingConversation(options, directory, peer, connection, stage =>
        {
            udp?.Record(stage, connection, peer.Session, peer.Account, "backend");
            if (stage == "peer_payload_forwarded")
            {
                if (++forwarded > 16 && forwarded % 100 != 0) return;
            }
            Audit(stage, connection, peer.Session, peer.Account, new { forwarded });
        }, payload =>
        {
            payloadRecords++;
            if (payload.SecondaryLength > 0) secondaryPayloads++;
            if (payload.PeerPrimary is { } primary && peerSampling.Observe(primary))
                Audit("peer_primary_observed", connection, peer.Session, peer.Account,
                    new { payloadRecords, payload.Outcome, primary });

            if (payload.Outcome != "queued" || payloadRecords <= 32 || payloadRecords % 100 == 0
                || payload.SecondaryLength > 0 && secondaryPayloads <= 8)
                Audit("peer_payload_observed", connection, peer.Session, peer.Account,
                    new { payloadRecords, secondaryPayloads, payload });
        }, udp);
        var buffer = new byte[transport.GetReceiveLimit()];
        long lastPacket = Stopwatch.GetTimestamp();
        long lastSummary = lastPacket;
        void Send(IEnumerable<WireDatagram> datagrams, SignalingDelivery? delivery = null)
        {
            foreach (var packet in datagrams)
            {
                if (packet.Bytes.Length > transport.GetSendLimit()) throw new WireFormatException("Encoded packet exceeds DTLS send limit.");
                if (packet.Attempt > 20) throw new WireFormatException("Reliable packet retry budget exhausted.");
                if (delivery is not null && udp is { Enabled: true } && (delivery.Descriptor & 8) != 0)
                    deliveries[packet.Sequence] = delivery;
                var origin = delivery ?? deliveries.GetValueOrDefault(packet.Sequence);
                if (remote is not null && udp is { Enabled: true })
                    activeSends[remote.ToString()] = (origin?.ForwardingId, packet.Sequence, packet.Attempt);
                try { transport.Send(packet.Bytes.Span); }
                catch (IOException error)
                {
                    udp?.Record("packet_send_failed", connection, peer.Session, origin?.SenderAccount ?? "backend",
                        peer.Account, origin?.ForwardingId, new { packet.Sequence, packet.Attempt, reason = error.GetType().Name });
                    throw;
                }
                finally
                {
                    if (remote is not null) activeSends.TryRemove(remote.ToString(), out _);
                }
                udp?.Record(packet.IsRetransmission ? "packet_retransmitted" : "packet_sent", connection,
                    peer.Session, origin?.SenderAccount ?? "backend", peer.Account, origin?.ForwardingId,
                    new { packet.Sequence, packet.Attempt, remoteAddress = remote?.Address.ToString(), remotePort = remote?.Port },
                    packet.Bytes, "wire_out");
                detailed.Write("signalingPlaintext", new
                {
                    direction = "backendToClient", connection, session = peer.Session, account = peer.Account,
                    payload = DetailedTrafficLog.Payload(packet.Bytes.Span)
                });
                sent++;
                if (sent <= 32 || packet.IsRetransmission && packet.Attempt is 2 or 5 or 10)
                    Audit("packet_sent", connection, peer.Session, peer.Account,
                        new { packet.Sequence, bytes = packet.Bytes.Length, packet.Attempt, wire.RetainedPacketCount });
            }
        }
        try
        {
            Audit("dtls_authenticated", connection, peer.Session, peer.Account, new { peer.Number });
            while (!stopping.IsCancellationRequested && directory.IsAttached(peer.Session, peer.Account, connection))
            {
                int length = transport.Receive(buffer, 0, buffer.Length, 100);
                if (length > 0)
                {
                    detailed.Write("signalingPlaintext", new
                    {
                        direction = "clientToBackend", connection, session = peer.Session, account = peer.Account,
                        payload = DetailedTrafficLog.Payload(buffer.AsSpan(0, length))
                    });
                    udp?.Record("plaintext_received", connection, peer.Session, peer.Account, "backend",
                        bytes: buffer.AsMemory(0, length), payloadKind: "wire_in");
                    received++;
                    var result = wire.Receive(buffer.AsMemory(0, length));
                    udp?.Record("packet_parsed", connection, peer.Session, peer.Account, "backend",
                        details: new { result.Packet.Sequence, result.IsDuplicate, assembledApplications = result.Applications.Count,
                            wire.BufferedFragmentCount, wire.BufferedBytes });
                    foreach (var sequence in result.AcknowledgedSequences)
                    {
                        deliveries.Remove(sequence, out var original);
                        udp?.Record("packet_acknowledged", connection, peer.Session, peer.Account, "backend", original?.ForwardingId,
                            new { sequence, originalSender = original?.SenderAccount, meaning = "transport_acknowledged_not_gameplay_processed" });
                    }
                    directory.Touch(peer.Session, peer.Account, connection);
                    lastPacket = Stopwatch.GetTimestamp();
                    if (received <= 64)
                        Audit("packet_received", connection, peer.Session, peer.Account, new
                        {
                            bytes = length, result.Packet.Class, result.Packet.Sequence, result.IsDuplicate,
                            types = result.Packet.Contents.Select(c => c.Type).ToArray(),
                            fragments = result.Packet.Contents.OfType<WireFragment>().Select(f => new { f.Descriptor, f.Offset, length = f.Data.Length, f.IsFinal }).ToArray(),
                            applications = result.Applications.Select(a => new { a.Family, a.Operation, a.Qualifier, a.Routing, a.Auxiliary, bytes = a.Body.Length }).ToArray()
                        });
                    Send(result.Outgoing);
                    foreach (var item in result.Contents.Where(c => c is WireControl or WireSettings))
                    {
                        var replies = conversation.Control(item);
                        if (replies.Count != 0)
                            Send(wire.SendContent(replies, item.Type == 3 ? (byte)0x80 : item.Type is 0x12 or 0x14 ? (byte)0x81 : (byte)0x40));
                    }
                    foreach (var application in result.Applications)
                    {
                        var forwardingId = udp is { Enabled: true } ? Guid.NewGuid().ToString("N") : null;
                        udp?.Record("application_received", connection, peer.Session, peer.Account, "backend", forwardingId,
                            new { application.Family, application.Operation, application.Qualifier, application.Routing, application.Auxiliary,
                                completingPacketSequence = result.Packet.Sequence }, application.Body,
                            application.Family == 1 ? "registration" : "bytes");
                        conversation.Application(application.Family, application.Operation, application.Qualifier, application.Body, forwardingId);
                    }
                }
                if (Stopwatch.GetElapsedTime(lastPacket) > TimeSpan.FromSeconds(options.IdleTimeoutSeconds))
                    throw new WireFormatException("Authenticated connection idle deadline exceeded.");
                if (wire.IsBound)
                {
                    int drained = 0;
                    while (drained++ < 32 && directory.Dequeue(peer.Session, peer.Account, connection) is { } item)
                    {
                        udp?.Record("delivery_dequeued", connection, peer.Session, item.SenderAccount ?? "backend", peer.Account,
                            item.ForwardingId, new { item.Family, item.Operation, item.Qualifier, item.Descriptor }, item.Body);
                        Send(wire.SendApplication(new(item.Family, item.Operation, item.Qualifier, 0, 0, item.Body), item.Descriptor), item);
                    }
                    Send(wire.Tick());
                }
                if (Stopwatch.GetElapsedTime(lastSummary) > TimeSpan.FromSeconds(5))
                {
                    Audit("traffic_summary", connection, peer.Session, peer.Account,
                        new { received, sent, forwarded, payloadRecords, secondaryPayloads, wire.RetainedPacketCount, wire.BufferedFragmentCount, wire.BufferedBytes });
                    lastSummary = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (WireFormatException exception)
        {

            udp?.Record("protocol_rejected", connection, peer.Session, peer.Account, "backend", details: new { reason = exception.Message });
            Audit("protocol_rejected", connection, peer.Session, peer.Account, new { reason = exception.Message, received, sent });
        }
        catch (IOException exception)
        {
            udp?.Record("dtls_closed", connection, peer.Session, peer.Account, "backend", details: new { reason = exception.GetType().Name });
            Audit("dtls_closed", connection, peer.Session, peer.Account, new { reason = exception.GetType().Name, received, sent });
        }
        catch (Exception exception)
        {
            udp?.Record(stopping.IsCancellationRequested ? "association_cancelled" : "association_faulted",
                connection, peer.Session, peer.Account, "backend", details: new { reason = exception.GetType().Name });
            throw;
        }
        finally
        {
            directory.Detach(peer.Session, peer.Account, connection);
            udp?.Record("association_closed", connection, peer.Session, peer.Account, "backend",
                details: new { received, sent, forwarded, cancelled = stopping.IsCancellationRequested,
                    unacknowledgedPackets = wire.RetainedPacketCount, pendingDeliveryCorrelations = deliveries.Count });
            Audit("association_closed", connection, peer.Session, peer.Account, new { received, sent, forwarded, payloadRecords, secondaryPayloads });
        }
        if (!stopping.IsCancellationRequested && options.CloseGracePeriod > TimeSpan.Zero)
        {
            var deadline = DateTimeOffset.UtcNow + options.CloseGracePeriod;
            while (DateTimeOffset.UtcNow < deadline && !stopping.IsCancellationRequested)
            {
                try
                {
                    if (wire.IsBound) Send(wire.Tick());
                    var length = transport.Receive(buffer, 0, buffer.Length, 50);
                    if (length > 0)
                    {
                        udp?.Record("close_grace_received", connection, peer.Session, peer.Account, "backend",
                            bytes: buffer.AsMemory(0, length), payloadKind: "wire_in");
                        detailed.Write("signalingPlaintext", new
                        {
                            direction = "clientToBackend", connection, session = peer.Session, account = peer.Account,
                            payload = DetailedTrafficLog.Payload(buffer.AsSpan(0, length))
                        });
                    }
                }
                catch { break; }
            }
        }
    }

    private void Audit(string stage, string? connection, string? session, string? account, object details)
    {

        if (!audits.Writer.TryWrite(new { type = "signaling", timestampUtc = DateTimeOffset.UtcNow,
            stage, connection, session, account, details, droppedAuditRecords = Interlocked.Read(ref droppedAuditRecords) }))
            Interlocked.Increment(ref droppedAuditRecords);
    }

    private async Task WriteAudit()
    {
        await foreach (var record in audits.Reader.ReadAllAsync()) await log.WriteAsync(record);
    }
}
