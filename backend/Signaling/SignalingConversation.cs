using ReVerse.Capture.Logging;
using System.Text;

namespace ReVerse.Capture.Signaling;


public sealed class SignalingConversation(SignalingOptions options, SignalingDirectory directory,
    SignalingSnapshot peer, string connection, Action<string> stage,
    Action<SignalingPayloadAudit>? observePayload = null, UdpTrafficLog? udp = null)
{
    private bool probed;
    private bool configured;
    private bool replied19;
    private bool replied21;
    private bool registered;
    private bool reportedUnknown;
    private bool negativeControlSent;

    public IReadOnlyList<WireContent> Control(WireContent content)
    {
        if (negativeControlSent) throw new WireFormatException("Client continued after a deliberately invalid startup reply.");
        if (content is WireSettings settings)
        {
            if (!probed) throw new WireFormatException("Settings before startup probe.");
            if (settings.Entries.Count != 3 || !settings.Entries.Select(s => s.Key).Order().SequenceEqual(new ulong[] { 0, 1, 2 }))
                throw new WireFormatException("Unsupported startup settings keys.");
            configured = true;
            stage("settings_received");

            return [new WireSettings([new(0, 5000), new(1, 3), new(2, 25)])];
        }
        if (content is not WireControl control) return [];
        switch (control.Type)
        {
            case 1:
                stage("no_op_received");
                return [];
            case 3:
                probed = true;
                stage("probe_received");
                return [new WireControl(4, [])];
            case 0x12:
                if (!configured || !control.Values.SequenceEqual(new ulong[] { 1, 1 }))
                    throw new WireFormatException("Unsupported startup request 18.");
                if (!options.ExperimentalReplies) return Unknown();
                if (options.NegativeControlReply == 19) return NegativeControl(19);
                replied19 = true;
                stage("experimental_reply19");
                return [new WireControl(0x13, [options.Reply19!.Value])];
            case 0x14:
                if (!replied19 || !control.Values.SequenceEqual(new ulong[] { 1 }))
                    throw new WireFormatException("Unsupported startup request 20.");
                if (!options.ExperimentalReplies) return Unknown();
                if (options.NegativeControlReply == 21) return NegativeControl(21);
                replied21 = true;
                stage("experimental_reply21_and_gate5");
                return [new WireControl(0x15, [options.Reply21!.Value]), new WireControl(5, [])];
            default:
                throw new WireFormatException("Unexpected client startup control.");
        }
    }

    private IReadOnlyList<WireContent> NegativeControl(byte type)
    {
        negativeControlSent = true;
        stage($"negative_control_reply{type}_zero");

        return [new WireControl(type, [0])];
    }

    private IReadOnlyList<WireContent> Unknown()
    {
        if (!reportedUnknown) { stage("blocked_unknown_startup_reply_use_explicit_experimental_settings"); reportedUnknown = true; }
        return [];
    }

    public void Application(byte family, byte operation, byte qualifier, ReadOnlyMemory<byte> body, string? forwardingId = null)
    {
        if (negativeControlSent) throw new WireFormatException("Application after a deliberately invalid startup reply.");
        if (family == 1 && operation == 1 && qualifier == 1)
        {
            if (!replied21) throw new WireFormatException("Application registration before startup gates.");
            var input = new WireReader(body, ushort.MaxValue);
            var secret = input.ReadBytes16().ToArray();
            input.RequireEnd();
            var output = new WireWriter();
            output.WriteUInt16(0);
            Text(output, peer.Session);
            output.WriteUInt16(options.RegistrationFieldBIsPeerNumber ? peer.Number : options.RegistrationFieldB!.Value);
            output.WriteUInt16(peer.Representative);
            var reply = new SignalingDelivery(1, 1, 2, output.ToArray());
            bool accepted;
            try { accepted = directory.Register(peer.Session, peer.Account, connection, secret, reply); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
            if (!accepted) throw new WireFormatException("Application credential rejected.");
            registered = true;
            stage("application_registered");
            if (directory.Activate(peer.Session, connection))
            {
                var current = directory.Current(peer.Session, peer.Account, connection)
                    ?? throw new WireFormatException("Signaling membership is unavailable.");
                var readiness = new WireWriter();
                Text(readiness, peer.Session);
                readiness.WriteByte(1);
                readiness.WriteUInt16(checked((ushort)current.Peers.Count));
                foreach (var entry in current.Peers)
                {
                    Text(readiness, entry.Account);
                    readiness.WriteUInt16(entry.Number);
                    readiness.WriteByte(2);
                }
                foreach (var entry in current.Peers)
                    if (!directory.Queue(peer.Session, entry.Account, new(3, 7, 0x10, readiness.ToArray()), connection))
                        throw new WireFormatException("Peer readiness delivery unavailable.");
                stage("all_members_registered_readiness_sent_NOT_gameplay_verified");
            }
            return;
        }
        if (!registered) throw new WireFormatException("Payload before registration.");
        if (family == 4 && operation == 1 && qualifier == 0x10)
        {
            Forward(body, forwardingId);
            return;
        }
        throw new WireFormatException($"Unsupported application contract {family:x2}/{operation:x2}/{qualifier:x2}.");
    }

    private void Forward(ReadOnlyMemory<byte> body, string? forwardingId)
    {
        var audit = new SignalingPayloadAudit { BodyBytes = body.Length };
        try { Forward(body, ref audit, forwardingId); }
        catch (WireFormatException error)
        {
            udp?.Record("forward_rejected", connection, peer.Session, peer.Account, "backend", forwardingId,
                new { reason = error.Message, audit.DestinationCount, audit.Outcome });
            throw;
        }
        finally { observePayload?.Invoke(audit); }
    }

    private void Forward(ReadOnlyMemory<byte> body, ref SignalingPayloadAudit audit, string? forwardingId)
    {
        if (!directory.IsActive(peer.Session) && !directory.AllRegistered(peer.Session, connection))
            throw new WireFormatException("Peer set is not registered.");
        var current = directory.Current(peer.Session, peer.Account, connection);
        if (current is null)
        {
            audit = audit with { Outcome = "membership_unavailable_dropped" };
            stage("membership_unavailable_dropped");
            udp?.Record("forward_dropped", connection, peer.Session, peer.Account, "backend", forwardingId,
                new { reason = "membership_unavailable" });
            return;
        }
        var input = new WireReader(body, ushort.MaxValue);
        var count = input.ReadUInt16();
        audit = audit with { DestinationCount = count };
        if (count is 0 || count > current.Peers.Count) throw new WireFormatException("Invalid destination count.");
        var destinations = new HashSet<ushort>();
        for (var i = 0; i < count; i++)
            if (!destinations.Add(input.ReadUInt16())) throw new WireFormatException("Duplicate destination.");
        var block = input.ReadBytes16();
        input.RequireEnd();
        var envelope = new WireReader(block);
        var tag = envelope.ReadByte();
        audit = audit with { Tag = tag };
        if (tag != 2) throw new WireFormatException("Unsupported forwarded tag.");

        var version = envelope.ReadByte();
        audit = audit with { Version = version };
        if (version != 1) throw new WireFormatException("Unsupported forwarded version.");
        uint senderNonce = envelope.ReadUInt32(), destinationNonce = envelope.ReadUInt32();
        var primaryLength = envelope.ReadUInt16();
        audit = audit with { PrimaryLength = primaryLength };
        var secondaryLength = envelope.ReadUInt16();
        audit = audit with { SecondaryLength = secondaryLength };
        var primary = envelope.ReadBytes(primaryLength);
        if (secondaryLength != 0)
        {
            audit = audit with { SecondaryMetadata = envelope.ReadUInt16() };
            _ = envelope.ReadBytes(secondaryLength);
        }
        envelope.RequireEnd();
        audit = audit with { EnvelopeValid = true };
        if (options.PeerDiagnostics)
            audit = audit with { PeerPrimary = PeerPrimaryDiagnostics.Inspect(primary.Span, senderNonce, destinationNonce) };
        if (senderNonce != current.Nonce) throw new WireFormatException("Sender nonce does not match the authenticated membership.");

        var stale = destinations.Where(number => current.Peers.All(p => p.Number != number)).ToArray();
        if (stale.Any(number => !directory.IsRetired(peer.Session, connection, number)))
            throw new WireFormatException("Destination outside authenticated session.");
        var targets = destinations.Select(number => current.Peers.SingleOrDefault(p => p.Number == number))
            .Where(target => target is not null).Cast<SignalingPeerInfo>().ToArray();
        if (targets.Length == 0)
        {
            audit = audit with { Outcome = "stale_topology_dropped" };
            stage("stale_topology_payload_dropped");
            udp?.Record("forward_dropped", connection, peer.Session, peer.Account, "backend", forwardingId,
                new { reason = "all_destinations_retired", destinations = destinations.ToArray() });
            return;
        }
        if (targets.Any(target => target.Account == current.Account || target.Nonce != destinationNonce))
            throw new WireFormatException("Destination nonce or route mismatch.");
        var output = new WireWriter(ushort.MaxValue);
        Text(output, peer.Session);
        output.WriteUInt16(current.Number);
        output.WriteBytes16(block.Span);
        foreach (var target in targets)
        {
            var accepted = directory.Queue(peer.Session, target.Account,
                new(4, 1, 0x10, output.ToArray(), 0x42, forwardingId, current.Account), connection);
            udp?.Record(accepted ? "forward_queued" : "forward_dropped", connection, peer.Session,
                current.Account, target.Account, forwardingId,
                new { senderNumber = current.Number, recipientNumber = target.Number, senderNonce, destinationNonce,
                    reason = accepted ? "recipient_queue_accepted" : "recipient_disconnected_or_queue_full" }, block, "peer");
            if (!accepted) throw new WireFormatException("Destination disconnected or its queue is full.");
        }
        audit = audit with { Outcome = "queued" };
        stage("peer_payload_forwarded");
    }

    private static void Text(WireWriter writer, string text) => writer.WriteBytes16(Encoding.ASCII.GetBytes(text));
}
