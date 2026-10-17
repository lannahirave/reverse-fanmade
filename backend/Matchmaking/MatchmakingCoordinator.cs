using Serilog;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReVerse.Capture.Signaling;

namespace ReVerse.Capture.Matchmaking;


public sealed class MatchmakingCoordinator(
    MatchmakingOptions options, NotificationHub hub, GameSessionRegistry registry,
    SignalingOptions signalingOptions, SignalingDirectory signaling) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Ticket> tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> queueChanged = new(StringComparer.Ordinal);
    private long order;

    private sealed class Ticket(string owner, JsonObject request, long order, DateTimeOffset expires)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Owner { get; } = owner;
        public JsonObject Request { get; } = (JsonObject)request.DeepClone();
        public string Ruleset { get; } = RequiredString(request, "rulesetName");
        public long Order { get; } = order;
        public DateTimeOffset Expires { get; } = expires;
        public long Sequence { get; set; } = 1;
        public string State { get; set; } = "queued";
        public string? SessionId { get; set; }
        public string? OfferId { get; set; }
        public JsonObject Wire() => new()
        {
            ["matchmakingTicketSequenceNo"] = Sequence, ["ticketId"] = Id,
            ["rulesetName"] = Ruleset, ["submitRetryWaitSeconds"] = 1
        };
    }

    public JsonObject Submit(string account, JsonObject request)
    {
        lock (gate)
        {
            Sweep();
            ValidateTicket(account, request);
            if (!hub.IsOnline(account)) throw Error(409, "Assign a notification socket before searching.");
            var existing = tickets.Values.FirstOrDefault(t => t.Owner == account && t.State is "queued" or "offered");



            if (existing?.SessionId is { } priorId && registry.Sessions.TryGetValue(priorId, out var prior)
                && prior.Players.ContainsKey(account))
            {
                EndSession(prior);
                existing = null;
            }
            if (existing is not null)
            {
                if (!JsonNode.DeepEquals(existing.Request, request)) throw Error(409, "Cancel the active ticket before changing search parameters.");
                return existing.Wire();
            }
            if (registry.Sessions.Values.Any(s => s.Reserved.Contains(account)))
                throw Error(409, "Leave the current offered/joined session before searching again.");
            var ticket = new Ticket(account, request, ++order, DateTimeOffset.UtcNow.AddSeconds(options.TicketLifetimeSeconds));
            tickets.Add(ticket.Id, ticket);
            QueueChanged(ticket);
            TicketEvent(ticket, "submitted");
            Log.ForContext("UserFacing", true).ForContext("TicketId", ticket.Id)
                .Information("Player is searching for a match. Waiting for compatible players.");

            return ticket.Wire();
        }
    }

    public JsonObject Cancel(string account, string id)
    {
        lock (gate)
        {
            if (!tickets.TryGetValue(id, out var ticket) || ticket.Owner != account) throw Error(404, "Ticket not found.");
            if (ticket.State is "canceled" or "expired" or "failed") return new();
            if (ticket.SessionId is { } sessionId && registry.Sessions.TryGetValue(sessionId, out var session))
                EndSession(session);
            if (ticket.State == "queued") QueueChanged(ticket);
            ticket.State = "canceled";
            ticket.Sequence++;
            TicketEvent(ticket, "canceled");
            Log.ForContext("UserFacing", true).ForContext("TicketId", ticket.Id).Information("Match search canceled.");
            return new();
        }
    }

    public void Disconnected(string account)
    {
        lock (gate)
        {
            if (hub.IsOnline(account)) return;
            foreach (var ticket in tickets.Values.Where(t => t.Owner == account && t.State == "queued").ToArray())
            {
                QueueChanged(ticket);
                ticket.State = "canceled";
                ticket.Sequence++;
            }
            foreach (var session in registry.Sessions.Values.Where(s => s.Reserved.Contains(account)).ToArray()) LeaveSession(session, account);
        }
    }

    public JsonObject SessionRequest(string account, string nickname, string path, string method, string[] ids, string? member, JsonObject body, GameSessionRead? read = null, string keyword = "", string reason = "")
    {
        lock (gate)
        {
            Sweep();
            if (!options.ExperimentalSessionProtocol) throw Error(501, "Session protocol needs stock-client validation; experimental mode is disabled.");
            if (path == "/v1/gameSession" && method == "GET")
            {
                if (ids.Length == 0) throw Error(400, "X-Be-Session-Ids is required.");
                var sessions = ids.Select(id => Authorized(account, id)).ToArray();
                read ??= GameSessionRead.Parse(QueryCollection.Empty);


                var matching = sessions.Distinct().Where(s => read.UsePlayerSession is null || s.UsePlayerSession == read.UsePlayerSession).ToArray();
                if (matching.Length == 0) throw Error(404, "No sessions match the requested filter.");
                return new JsonObject { ["gameSessions"] = new JsonArray(matching.Select(s => (JsonNode)read.Serialize(s)).ToArray()) };
            }

            if (path == "/v1/gameSession" && method == "POST") return CreatePrivate(account, nickname, body);
            if (path.EndsWith("/invitation", StringComparison.Ordinal) || path.EndsWith("/spectators", StringComparison.Ordinal))
                throw Error(501, "Invitations and spectators are not implemented.");
            var join = path.EndsWith("/member/players", StringComparison.Ordinal) && method == "POST";
            GameSessionRegistry.Session session;
            if (join && ids.Length == 0 && !string.IsNullOrEmpty(keyword))
                session = registry.Sessions.Values.FirstOrDefault(s => !string.IsNullOrEmpty(s.Keyword) && KeywordEquals(keyword, s.Keyword))
                    ?? throw Error(404, "Session not found.");
            else
            {
                if (ids.Length != 1) throw Error(400, "One X-Be-Session-Id is required.");
                session = join
                    ? registry.Sessions.TryGetValue(ids[0], out var found) ? found : throw Error(404, "Session not found.")
                    : Authorized(account, ids[0]);
            }
            var representative = session.Representative == account;
            if (path == "/v1/gameSession")
            {
                if (!representative) throw Error(403, "Representative permission required.");
                if (method == "DELETE") { EndSession(session); return new(); }
                if (method != "PATCH") throw Error(405, "Use GET, POST, PATCH or DELETE.");
                var patch = body["gameSession"] as JsonObject ?? body;
                if (!ReferenceEquals(patch, body) && body.Any(p => p.Key != "gameSession"))
                    throw Error(400, "Unsupported session update field.");
                if (patch.Any(p => p.Key is not ("joinDisabled" or "customData1" or "customData2")))
                    throw Error(400, "Unsupported session update field.");

                var disabled = patch["joinDisabled"]?.GetValue<bool>() ?? session.JoinDisabled;
                var custom1 = patch["customData1"]?.GetValue<string>() ?? session.CustomData1;
                var custom2 = patch["customData2"]?.GetValue<string>() ?? session.CustomData2;
                var disabledChanged = disabled != session.JoinDisabled;
                if (!disabledChanged && custom1 == session.CustomData1 && custom2 == session.CustomData2)
                    return new();
                session.JoinDisabled = disabled;
                session.CustomData1 = custom1;
                session.CustomData2 = custom2;
                session.Sequence++;
                if (disabledChanged) SessionEvent(session, "joinDisabled:updated", new() { ["joinDisabled"] = disabled });
                else SessionSequenceEvent(session);
                return new();
            }
            if (path.EndsWith("/member/players", StringComparison.Ordinal))
            {
                if (method != "POST") throw Error(405, "Use POST to join.");
                if (body["useCrossPlay"] is JsonValue flag && flag.TryGetValue<bool>(out var cross)
                    && cross != session.UseCrossPlay)
                    throw Error(409, "Join cross-play setting differs from the matched tickets.");
                var player = GameJoinProtocol.ParsePlayer(account, nickname, body);
                if (!session.Reserved.Contains(account))
                {
                    if (session.Players.Count >= session.Capacity)
                        throw Error(409, "Session is closed or full.");
                    if (string.IsNullOrEmpty(session.Keyword) || !KeywordEquals(keyword, session.Keyword))
                        throw Error(403, "Join before sending messages.");
                }
                if (session.Players.TryGetValue(account, out var joined))
                {
                    if (joined["customData1"]!.GetValue<string>() != player["customData1"]!.GetValue<string>())
                        throw Error(409, "A different join attempt already owns this reservation.");
                    return GameJoinProtocol.Reply(session.Id, joined, session.Keyword);
                }
                if (session.JoinDisabled || session.Players.Count >= session.Capacity) throw Error(409, "Session is closed or full.");
                var refreshSignaling = session.ProviderAllocated;
                session.Reserved.Add(account);
                session.Players.Add(account, player);
                session.Sequence++;
                SessionEvent(session, "member:players:created", GameJoinProtocol.CreatedEvent(player));
                if (refreshSignaling) RefreshSignaling(session, account);
                else TryAllocateSignaling(session);
                Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id)
                    .Information("Player joined the lobby. {PlayerCount}/{Capacity} places filled.", session.Players.Count, session.Capacity);
                return GameJoinProtocol.Reply(session.Id, player, session.Keyword);
            }
            if (path.EndsWith("/members", StringComparison.Ordinal))
            {
                if (method is not ("PATCH" or "DELETE")) throw Error(405, "Use PATCH or DELETE.");
                var target = string.IsNullOrEmpty(member) ? account : member;
                if (target != account && !representative) throw Error(403, "Cannot change another member.");
                if (!session.Reserved.Contains(target)) throw Error(404, "Member not found.");
                if (method == "DELETE")
                {
                    var effectiveReason = representative && !string.IsNullOrEmpty(session.Keyword)
                        && target != session.Representative && string.IsNullOrEmpty(reason) ? "kicked" : reason;
                    LeaveSession(session, target, effectiveReason);
                    return new();
                }
                if (!session.Players.TryGetValue(target, out var player)) throw Error(409, "Member has not joined.");
                if (body.Any(p => p.Key is not ("customData1" or "customData2"))) throw Error(400, "Unsupported member update field.");
                var values = body.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "");
                if (values.Values.Any(value => value.Length > 16384)) throw Error(400, "Member metadata is too large.");
                if (values.TryGetValue("customData1", out var metadata))
                {
                    _ = GameJoinProtocol.ReadNonce(metadata);
                    if (metadata != player["customData1"]!.GetValue<string>())
                        throw Error(409, "Peer metadata is bound to this join. Leave and rejoin to change it.");
                }
                if (values.All(value => value.Value == (player[value.Key]?.GetValue<string>() ?? "")))
                    return new();
                foreach (var value in values) player[value.Key] = value.Value;
                session.Sequence++;
                SessionSequenceEvent(session);
                return new();
            }
            if (path.EndsWith("/signaling", StringComparison.Ordinal))
            {
                if (method == "GET")
                {
                    if (!signalingOptions.Enabled) throw Error(503, "No signaling provider has supplied endpoints.");
                    if (!session.Players.ContainsKey(account)) throw Error(403, "Join before obtaining signaling credentials.");
                    TryAllocateSignaling(session);
                    return signaling.Descriptor(session.Id, account) ?? throw Error(503, "No signaling provider has supplied endpoints.");
                }
                if (method != "PATCH") throw Error(405, "Use GET or PATCH.");
                if (!representative || !session.Players.ContainsKey(account)) throw Error(403, "Joined representative permission required.");



                if (body.Count != 1 || body["signalingTimeoutSeconds"] is not JsonValue value
                    || !value.TryGetValue<int>(out var timeout) || timeout is < 1 or > 600)
                    throw Error(400, "signalingTimeoutSeconds must be an integer from 1 to 600.");
                session.RequestSignaling(timeout, signalingOptions.Enabled);
                TryAllocateSignaling(session);
                return new();
            }
            if (path.EndsWith("/sessionMessage", StringComparison.Ordinal))
            {
                if (method != "POST") throw Error(405, "Use POST.");
                if (!session.Players.ContainsKey(account)) throw Error(403, "Join before sending messages.");
                var content = RequiredString(body, "content");
                session.Sequence++;
                SessionEvent(session, "sessionMessage:created", new() { ["from"] = account, ["content"] = content });
                return new();
            }
            throw Error(404, "Unknown session route.");
        }
    }

    private JsonObject CreatePrivate(string account, string nickname, JsonObject body)
    {
        if (registry.Sessions.Values.Any(s => s.Reserved.Contains(account)))
            throw Error(409, "Leave the current offered/joined session before searching again.");
        if (body.Any(p => p.Key is not ("gameSession" or "regions")))
            throw Error(400, "Unsupported session create field.");
        JsonObject request = body["gameSession"] switch
        {
            JsonObject obj => obj,
            JsonArray { Count: 1 } arr when arr[0] is JsonObject single => single,
            null when body.ContainsKey("gameSession") => throw Error(400, "Unsupported session create field."),
            null => body,
            _ => throw Error(400, "Unsupported session create field.")
        };
        if (body["regions"] is JsonArray rootRegions)
        {
            foreach (var region in rootRegions)
            {
                if (region is JsonValue value && value.TryGetValue<string>(out _)) continue;
                if (region is JsonObject obj && obj["name"] is JsonValue name && name.TryGetValue<string>(out _)) continue;
                throw Error(400, "Invalid region.");
            }
        }
        else if (body.ContainsKey("regions"))
            throw Error(400, "Invalid region.");
        if (request.Any(p => p.Key is not ("maxPlayers" or "maxSpectators" or "member" or "joinDisabled"
            or "supportedService" or "customData1" or "customData2" or "useCrossPlay" or "usePlayerSession"
            or "keywordType" or "regions")))
            throw Error(400, "Unsupported session create field.");
        if (request["keywordType"] is JsonObject keywords)
        {
            if (keywords.Any(p => p.Key is not ("length" or "charType" or "charOmitType" or "valid")))
                throw Error(400, "Unsupported session create field.");
        }
        else if (request.ContainsKey("keywordType")) throw Error(400, "Unsupported session create field.");
        if (request["regions"] is JsonArray memberRegions)
        {
            foreach (var region in memberRegions)
            {
                if (region is JsonValue value && value.TryGetValue<string>(out _)) continue;
                if (region is JsonObject obj && obj["name"] is JsonValue name && name.TryGetValue<string>(out _)) continue;
                throw Error(400, "Invalid region.");
            }
        }
        else if (request.ContainsKey("regions")) throw Error(400, "Invalid region.");
        var capacity = request["maxPlayers"]?.GetValue<int>()
            ?? throw Error(400, "maxPlayers is required.");
        if (capacity is < 2 or > 10) throw Error(400, "maxPlayers must be 2..10 players.");
        var spectators = request["maxSpectators"]?.GetValue<int>() ?? 0;
        if (spectators is < 0 or > 10) throw Error(400, "maxSpectators must be 0..10.");
        var member = request["member"] as JsonObject ?? throw Error(400, "member is required.");
        if (member["players"] is not JsonArray { Count: 1 } players || players[0] is not JsonObject host
            || RequiredString(host, "accountId") != account)
            throw Error(400, "Exactly one host player matching the authenticated account is required.");
        var supported = request["supportedService"];
        if (supported is not null && supported is not JsonArray)
            throw Error(400, "supportedService must be an array.");
        if (supported is JsonArray services && services.Any(s => s is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw Error(400, "supportedService entries must be strings.");
        var custom1 = request["customData1"]?.GetValue<string>() ?? "";
        var custom2 = request["customData2"]?.GetValue<string>() ?? "";
        if (custom1.Length > 16384 || custom2.Length > 16384) throw Error(400, "Session metadata is too large.");
        var crossPlay = (request["useCrossPlay"] as JsonValue)?.TryGetValue<bool>(out var cross) == true ? cross : true;
        var keyword = NewKeyword();
        while (registry.Sessions.Values.Any(s => !string.IsNullOrEmpty(s.Keyword) && KeywordEquals(s.Keyword, keyword)))
            keyword = NewKeyword();
        var session = registry.Create(account, capacity, keyword);
        session.UseCrossPlay = crossPlay;
        session.CustomData1 = custom1;
        session.CustomData2 = custom2;
        var player = GameJoinProtocol.ParsePlayer(account, nickname, new JsonObject
        {
            ["players"] = new JsonArray(new JsonObject
            {
                ["accountId"] = account, ["joinState"] = "JOINED",
                ["customData1"] = host["customData1"]?.GetValue<string>() ?? """{"version":1,"nonce":1}"""
            })
        });
        session.Players.Add(account, player);
        session.Sequence++;
        SessionEvent(session, "member:players:created", GameJoinProtocol.CreatedEvent(player));
        Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id)
            .Information("Private lobby created. Waiting for players to join.");
        return new JsonObject
        {
            ["gameSessions"] = new JsonArray(new JsonObject
            {
                ["sessionId"] = session.Id, ["gameSessionSequenceNo"] = session.Sequence,
                ["keyword"] = keyword, ["serviceEncryptionKey"] = session.ServiceEncryptionKey,
                ["member"] = new JsonObject { ["players"] = new JsonArray(player.DeepClone()), ["spectators"] = new JsonArray() }
            })
        };
    }

    private static string NewKeyword()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(4, alphabet, static (span, table) =>
        {
            foreach (ref var c in span) c = table[RandomNumberGenerator.GetInt32(table.Length)];
        });
    }

    private static bool KeywordEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected))
        && presented.Length == expected.Length;

    private GameSessionRegistry.Session Authorized(string account, string id)
    {
        if (!registry.Sessions.TryGetValue(id, out var session) || !session.Reserved.Contains(account)) throw Error(404, "Session not found.");
        return session;
    }

    private void EndSession(GameSessionRegistry.Session session)
    {
        registry.Sessions.Remove(session.Id);
        signaling.Remove(session.Id);
        foreach (var ticket in tickets.Values.Where(t => t.SessionId == session.Id && t.State == "offered"))
        {
            ticket.State = "failed";
            ticket.Sequence++;
            hub.Publish([ticket.Owner], "matchmaking:offers:failed", new()
            {
                ["offers"] = new JsonArray(new JsonObject { ["matchmakingOfferSequenceNo"] = 2, ["offerId"] = ticket.OfferId })
            });
            TicketEvent(ticket, "failed");
        }
        Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id).Information("Game session closed.");
    }

    private void LeaveSession(GameSessionRegistry.Session session, string account, string reason = "")
    {
        if (string.IsNullOrEmpty(session.Keyword) || session.Representative == account)
        {
            EndSession(session);
            return;
        }
        var recipients = session.Reserved.ToArray();
        if (!session.Players.Remove(account, out var player))
        {
            session.Reserved.Remove(account);
            return;
        }
        session.Reserved.Remove(account);
        session.Sequence++;
        SessionEvent(session, "member:players:deleted", GameJoinProtocol.DeletedEvent(player, reason), recipients);
        Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id)
            .Information("Player left the lobby. {PlayerCount}/{Capacity} places filled.", session.Players.Count, session.Capacity);
        if (!session.ProviderAllocated) return;
        if (session.Players.Count < 2)
        {
            if (session.Players.Count == 1 && session.SignalingTimeoutSeconds is { } timeout
                && signaling.IsListening && signaling.Replace(session.Id, session.Representative,
                    session.Players.Select(p => new SignalingMember(p.Key,
                        GameJoinProtocol.ReadNonce(p.Value["customData1"]!.GetValue<string>()))).ToArray(),
                    DateTimeOffset.UtcNow.AddSeconds(timeout)))
            {
                session.ProviderAllocated = true;
                return;
            }
            signaling.Remove(session.Id);
            session.ProviderAllocated = false;
            return;
        }
        RefreshSignaling(session);
    }

    private void TryAllocateSignaling(GameSessionRegistry.Session session)
    {
        if (session.ProviderAllocated || session.SignalingDeadline is not { } deadline
            || session.Players.Count < 2 || session.Players.Count != session.Reserved.Count || !signaling.IsListening) return;
        var members = session.Players.Select(p => new SignalingMember(p.Key,
            GameJoinProtocol.ReadNonce(p.Value["customData1"]!.GetValue<string>()))).ToArray();
        if (!signaling.Allocate(session.Id, session.Representative, members, deadline)) return;
        session.ProviderAllocated = true;
        session.Sequence++;

        foreach (var account in session.Reserved)
        {
            var descriptor = signaling.Descriptor(session.Id, account);
            if (descriptor is not null) hub.Publish([account], "gameSession:signaling:created", new()
            {
                ["sessionId"] = session.Id, ["gameSessionSequenceNo"] = session.Sequence, ["signaling"] = descriptor
            });
        }
    }

    private void RefreshSignaling(GameSessionRegistry.Session session, string? credentialAccount = null)
    {
        if (session.SignalingTimeoutSeconds is not { } timeout || session.Players.Count < 2
            || session.Players.Count != session.Reserved.Count || !signaling.IsListening) return;
        var members = session.Players.Select(p => new SignalingMember(p.Key,
            GameJoinProtocol.ReadNonce(p.Value["customData1"]!.GetValue<string>()))).ToArray();
        if (!signaling.Replace(session.Id, session.Representative, members, DateTimeOffset.UtcNow.AddSeconds(timeout))) return;
        session.ProviderAllocated = true;
        if (credentialAccount is null) return;
        session.Sequence++;
        foreach (var account in session.Reserved)
        {
            if (account == credentialAccount && signaling.Descriptor(session.Id, account) is { } descriptor)
            {
                hub.Publish([account], "gameSession:signaling:created", new()
                {
                    ["sessionId"] = session.Id, ["gameSessionSequenceNo"] = session.Sequence, ["signaling"] = descriptor
                });
            }
            else hub.Publish([account], "gameSession:operateSequenceNo", new()
            {
                ["sessionId"] = session.Id, ["gameSessionSequenceNo"] = session.Sequence, ["skip"] = 0
            });
        }
    }




    private void SessionSequenceEvent(GameSessionRegistry.Session session) =>
        SessionEvent(session, "operateSequenceNo", new() { ["skip"] = 0 });

    private void SessionEvent(GameSessionRegistry.Session session, string suffix, JsonObject body, IEnumerable<string>? recipients = null)
    {
        body["sessionId"] = session.Id;
        body["gameSessionSequenceNo"] = session.Sequence;
        hub.Publish(recipients ?? session.Reserved, "gameSession:" + suffix, body);
    }

    private void TicketEvent(Ticket ticket, string suffix) => hub.Publish([ticket.Owner], "matchmaking:tickets:" + suffix,
        new() { ["tickets"] = new JsonArray(ticket.Wire()) });

    private static void ValidateTicket(string account, JsonObject request)
    {
        _ = RequiredString(request, "rulesetName");
        _ = RequiredString(request, "groupName");
        if (request["useCrossPlay"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)) throw Error(400, "useCrossPlay must be boolean.");
        if (request["players"] is not JsonArray { Count: 1 } players || players[0] is not JsonObject player)
            throw Error(400, "Only authenticated single-player tickets are supported.");
        if (RequiredString(player, "accountId") != account) throw Error(403, "Ticket player must match the relay account.");
        var area = request["playarea"] as JsonObject ?? throw Error(400, "playarea is required.");
        _ = RequiredString(area, "name");
        if (area["regions"] is not JsonArray { Count: > 0 and <= 32 } regions) throw Error(400, "At least one region is required.");
        foreach (var region in regions)
        {
            if (region is not JsonObject obj) throw Error(400, "Invalid region.");
            _ = RequiredString(obj, "name");
        }
        ValidateAttributes(request["ticketAttributes"]);
        ValidateAttributes(player["playerAttributes"]);
    }

    private static void ValidateAttributes(JsonNode? node)
    {
        if (node is not JsonArray { Count: <= 64 } attrs) throw Error(400, "Typed attributes array is required.");
        foreach (var item in attrs)
        {
            if (item is not JsonObject attr) throw Error(400, "Invalid typed attribute.");
            _ = RequiredString(attr, "name");
            if (!attr.ContainsKey("type") || !attr.ContainsKey("value")) throw Error(400, "Attribute type/value are required.");
        }
    }



    private JsonObject Compatibility(Ticket ticket)
    {
        var copy = (JsonObject)ticket.Request.DeepClone();
        copy["players"]![0]!.AsObject().Remove("accountId");
        copy["playarea"]!.AsObject().Remove("regions");
        if (options.IgnorePlayerAttributes) copy["players"]![0]!.AsObject().Remove("playerAttributes");
        return copy;
    }

    private static HashSet<string> Regions(Ticket ticket) => new(
        ticket.Request["playarea"]!["regions"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()), StringComparer.Ordinal);

    private void QueueChanged(Ticket ticket) => queueChanged[ticket.Ruleset] = DateTimeOffset.UtcNow;

    private bool PartialReady(string ruleset, int players, DateTimeOffset now) =>
        options.PartialMatchWaitSeconds > 0 && players >= 2
        && queueChanged.TryGetValue(ruleset, out var since)
        && now - since >= TimeSpan.FromSeconds(options.PartialMatchWaitSeconds);

    private void Match()
    {
        if (!options.ExperimentalSessionProtocol) return;
        var now = DateTimeOffset.UtcNow;
        var queue = tickets.Values.Where(t => t.State == "queued" && hub.IsOnline(t.Owner)).OrderBy(t => t.Order).ToArray();
        foreach (var first in queue)
        {
            if (first.State != "queued" || !options.Rulesets.TryGetValue(first.Ruleset, out var count)) continue;
            var selected = new List<Ticket> { first };
            var regions = Regions(first);
            var fingerprint = Compatibility(first);
            foreach (var next in queue.Where(t => t.Order > first.Order && t.State == "queued"))
            {
                if (!JsonNode.DeepEquals(fingerprint, Compatibility(next))) continue;
                var common = Regions(next);
                common.IntersectWith(regions);
                if (common.Count == 0) continue;
                selected.Add(next);
                regions = common;
                if (selected.Count == count) break;
            }
            if (selected.Count < count && !PartialReady(first.Ruleset, selected.Count, now)) continue;
            var session = registry.Create(selected.Select(t => t.Owner).ToArray(), selected.Count, DateTimeOffset.UtcNow.AddSeconds(options.JoinLifetimeSeconds));
            session.UseCrossPlay = first.Request["useCrossPlay"]!.GetValue<bool>();
            var offerId = Guid.NewGuid().ToString("N");
            session.OfferId = offerId;
            foreach (var ticket in selected)
            {
                QueueChanged(ticket);
                ticket.State = "offered";
                ticket.SessionId = session.Id;
                ticket.OfferId = offerId;
                hub.Publish([ticket.Owner], "matchmaking:offers:created", new()
                {
                    ["offers"] = new JsonArray(new JsonObject
                    {
                        ["matchmakingOfferSequenceNo"] = 1, ["offerId"] = offerId, ["rulesetName"] = ticket.Ruleset,
                        ["location"] = new JsonObject { ["gameSessionId"] = session.Id }
                    })
                });
            }
            if (selected.Count < count)
                Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id)
                    .Information("Starting with {PlayerCount} of {TargetPlayers} players after no queue change for {WaitSeconds} seconds. Waiting for them to join.",
                        selected.Count, count, options.PartialMatchWaitSeconds);
            else
                Log.ForContext("UserFacing", true).ForContext("SessionId", session.Id)
                    .Information("Match found for {PlayerCount} players. Waiting for them to join.", selected.Count);
        }
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var ticket in tickets.Values.Where(t => t.State == "queued" && t.Expires <= now))
        {
            QueueChanged(ticket);
            ticket.State = "expired";
            ticket.Sequence++;
            TicketEvent(ticket, "timedOut");
            Log.ForContext("UserFacing", true).ForContext("TicketId", ticket.Id)
                .Information("Match search expired before enough compatible players joined.");
        }
        foreach (var session in registry.Sessions.Values.ToArray())
        {
            if (session.ProviderAllocated)
            {

                if (!signaling.IsAlive(session.Id)) EndSession(session);
            }
            else if (signalingOptions.Enabled && session.SignalingDeadline is { } deadline
                && session.Players.Count == session.Reserved.Count)
            {
                if (deadline <= now) EndSession(session);
                else TryAllocateSignaling(session);
            }
            else if (session.Deadline <= now || session.SignalingDeadline is { } pending && pending <= now)
                EndSession(session);
        }
        foreach (var ticket in tickets.Values.Where(t => t.State is not ("queued" or "offered") && t.Expires.AddMinutes(5) < now).ToArray()) tickets.Remove(ticket.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                lock (gate) { Sweep(); Match(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal static BadHttpRequestException Error(int status, string message) => new(message, status);
    internal static string RequiredString(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= 16384
            ? text : throw Error(400, $"{name} must be a nonempty string.");
}
