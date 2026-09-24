using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ReVerse.Capture.Capturing;
using ReVerse.Capture.Protocol;
using ReVerse.Capture.Middleware;
using ReVerse.Traffic;

namespace ReVerse.Capture.Matchmaking;


public sealed class NotificationHub(RequestLog log, GameState state, DetailedTrafficLog detailed)
{
    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<Connection>> connections = new(StringComparer.Ordinal);

    public bool IsOnline(string account)
    {
        lock (gate) return connections.TryGetValue(account, out var peers) && peers.Any(p => !p.Stop.IsCancellationRequested);
    }

    public void Publish(IEnumerable<string> accounts, string type, JsonObject data)
    {
        lock (gate)
            foreach (var account in accounts.Distinct(StringComparer.Ordinal))
                if (connections.TryGetValue(account, out var peers))
                    foreach (var peer in peers) peer.Publish(type, data);
    }

    public async Task RunAsync(HttpContext context, string account, Action disconnected)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var peer = new Connection(context.RequestAborted);
        var trafficId = context.Items[DetailedTrafficMiddleware.ContextKey] as string ?? Guid.NewGuid().ToString("N");
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
        var writer = WriteAsync(socket, peer, account, trafficId, remoteAddress);
        var buffer = new byte[8192];
        var assigned = false;
        try
        {
            while (!peer.Stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, peer.Stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536)
                        throw new InvalidDataException("Notification commands must be bounded JSON text.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var bytes = message.ToArray();
                detailed.Write("webSocketMessage", new
                {
                    id = trafficId, direction = "clientToBackend", account, remoteAddress,
                    payload = DetailedTrafficLog.Payload(bytes)
                });
                var command = JsonNode.Parse(bytes) as JsonObject
                    ?? throw new InvalidDataException("Invalid command.");
                var name = command["command"]?.GetValue<string>();
                if (name is "session_assign" or "session_refresh")
                {
                    var key = command["args"]?["sessionKey"]?.GetValue<string>();
                    if (key is not null && state.ResolveIdentity(key, strict: true).AccountId != account)
                        throw new InvalidDataException("Notification session belongs to another account.");
                }

                await log.WriteAsync(new { type = "matchmakingCommand", timestampUtc = DateTimeOffset.UtcNow, account, command = name });
                lock (gate)
                {
                    if (name == "session_assign")
                    {
                        peer.Ack(name);
                        if (!assigned)
                        {
                            if (!connections.TryGetValue(account, out var peers)) connections[account] = peers = [];
                            peers.Add(peer);
                            assigned = true;
                        }
                    }
                    else if (!assigned) throw new InvalidDataException("Assign the notification connection first.");
                    else if (name == "session_refresh") peer.Ack(name);
                    else if (name == "retransmission") peer.Replay(command["args"] as JsonObject ?? command);
                    else throw new InvalidDataException("Unknown notification command.");
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or JsonException or InvalidOperationException or BadHttpRequestException or FormatException)
        {
            await log.WriteAsync(new { type = "matchmakingSocketClosed", timestampUtc = DateTimeOffset.UtcNow, account, reason = ex.GetType().Name });
        }
        finally
        {
            lock (gate)
            {
                if (connections.TryGetValue(account, out var peers))
                {
                    peers.Remove(peer);
                    if (peers.Count == 0) connections.Remove(account);
                }
            }
            peer.Stop.Cancel();
            try { await writer; }
            finally
            {
                try { socket.Abort(); }
                finally { disconnected(); }
            }
        }
    }

    private async Task WriteAsync(WebSocket socket, Connection peer, string account, string trafficId, string? remoteAddress)
    {
        try
        {
            await foreach (var json in peer.Out.Reader.ReadAllAsync(peer.Stop.Token))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, peer.Stop.Token);
                detailed.Write("webSocketMessage", new
                {
                    id = trafficId, direction = "backendToClient", account, remoteAddress,
                    payload = DetailedTrafficLog.Payload(bytes)
                });
                await log.WriteAsync(new { type = "matchmakingNotification", timestampUtc = DateTimeOffset.UtcNow, account, body = ProtocolAudit.Redact(JsonNode.Parse(json)) });
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            peer.Stop.Cancel();
        }
    }

    private sealed class Connection(CancellationToken cancellation) : IDisposable
    {
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public Channel<string> Out { get; } = Channel.CreateBounded<string>(256);
        private readonly SortedDictionary<long, string> history = new();
        private long sequence;

        private void Enqueue(string value)
        {

            if (!Out.Writer.TryWrite(value)) Stop.Cancel();
        }

        public void Ack(string command) => Enqueue(JsonSerializer.Serialize(new { dataFormatType = "CMD_RESPONSE", command }));

        public void Publish(string type, JsonObject body)
        {
            var json = new JsonObject
            {
                ["dataFormatType"] = "MSG_DATA", ["seq"] = ++sequence, ["dataType"] = type,
                ["data"] = new JsonObject { ["data"] = body.DeepClone() }
            }.ToJsonString();
            history[sequence] = json;
            if (history.Count > 128) history.Remove(history.Keys.First());
            Enqueue(json);
        }

        public void Replay(JsonObject args)
        {
            IEnumerable<long> requested;
            if (args["lost_seqs"] is JsonArray { Count: > 0 } lost)
            {
                if (lost.Count > 128) throw new InvalidDataException("Replay request too large.");
                requested = lost.Select(n => n!.GetValue<long>()).Distinct().Order().ToArray();
            }
            else
            {
                var from = args["seq"]?.GetValue<long>() ?? throw new InvalidDataException("Missing replay sequence.");
                if (from < 1 || from > sequence + 1 || sequence - from >= 128)
                    throw new InvalidDataException("Replay window unavailable; reconnect and search again.");
                requested = history.Keys.Where(n => n >= from).ToArray();
            }
            foreach (var number in requested)
            {
                if (!history.TryGetValue(number, out var json)) throw new InvalidDataException("Replay window unavailable.");
                Enqueue(json);
            }
            Ack("retransmission");
        }

        public void Dispose() => Stop.Dispose();
    }
}
