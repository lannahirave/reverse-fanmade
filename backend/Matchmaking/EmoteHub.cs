using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using ReVerse.Capture.Protocol;

namespace ReVerse.Capture.Matchmaking;

public sealed class EmoteHub(Func<string, IReadOnlyCollection<string>?> peersOf, TimeProvider? time = null)
{
    public const int MailboxLimit = 64;
    public const int RatePerWindow = 20;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleMailbox = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, Mailbox> boxes = new(StringComparer.Ordinal);
    private long nextId;

    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    private sealed class Mailbox
    {
        public readonly Queue<(long Id, JsonObject Event)> Events = new();
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Touched;
        public readonly Queue<DateTimeOffset> Sent = new();
    }

    public EmoteHub(MatchmakingCoordinator coordinator) : this(coordinator.SessionPeers) { }

    public static JsonObject Sanitize(JsonObject body)
    {
        var action = body["action"] is JsonValue a && a.TryGetValue<string>(out var s) ? s : null;
        if (action is not ("play" or "stop")) throw MatchmakingCoordinator.Error(400, "action must be play or stop.");
        var result = new JsonObject { ["action"] = action, ["member"] = Int(body, "member", 0, 31, required: true) };
        if (action == "play")
        {
            var bank = Int(body, "bank", 0, int.MaxValue, required: true);
            if (bank is not (20000 or 20001)) throw MatchmakingCoordinator.Error(400, "bank must be 20000 or 20001.");
            result["bank"] = bank;
            result["motion"] = Int(body, "motion", 0, 200, required: true);
            result["loop"] = body["loop"] is JsonValue l && l.TryGetValue<bool>(out var loop) && loop;
            result["hide_weapon"] = body["hide_weapon"] is JsonValue h && h.TryGetValue<bool>(out var hide) && hide;
            result["hold"] = body["hold"] is JsonValue o && o.TryGetValue<bool>(out var hold) && hold;
            result["char"] = Text(body, "char", 16, c => c is >= 'a' and <= 'z');
            result["name"] = Text(body, "name", 48, c => !char.IsControl(c));
        }
        return result;
    }

    private static int Int(JsonObject body, string name, int min, int max, bool required)
    {
        if (body[name] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max) return n;
        if (!required && body[name] is null) return min;
        throw MatchmakingCoordinator.Error(400, $"{name} must be an integer between {min} and {max}.");
    }

    private static string Text(JsonObject body, string name, int max, Func<char, bool> allowed)
    {
        if (body[name] is null) return "";
        if (body[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= max && s.All(allowed)) return s;
        throw MatchmakingCoordinator.Error(400, $"{name} is invalid.");
    }

    private Mailbox Box(string account, DateTimeOffset now)
    {
        if (!boxes.TryGetValue(account, out var box)) boxes[account] = box = new Mailbox();
        box.Touched = now;
        return box;
    }

    public int Post(string account, JsonObject body)
    {
        var ev = Sanitize(body);
        var peers = peersOf(account);
        var now = clock.GetUtcNow();
        lock (sync)
        {
            var self = Box(account, now);
            while (self.Sent.Count > 0 && now - self.Sent.Peek() > RateWindow) self.Sent.Dequeue();
            if (self.Sent.Count >= RatePerWindow) throw MatchmakingCoordinator.Error(429, "Too many dance events.");
            self.Sent.Enqueue(now);
            foreach (var stale in boxes.Where(b => now - b.Value.Touched > IdleMailbox).Select(b => b.Key).ToArray())
                boxes.Remove(stale);
            if (peers is null) return 0;
            var delivered = 0;
            foreach (var peer in peers)
            {
                if (peer == account) continue;
                var box = Box(peer, now);
                var copy = (JsonObject)ev.DeepClone();
                var id = ++nextId;
                copy["id"] = id;
                copy["from"] = account;
                box.Events.Enqueue((id, copy));
                while (box.Events.Count > MailboxLimit) box.Events.Dequeue();
                box.Signal.TrySetResult();
                delivered++;
            }
            return delivered;
        }
    }

    public async Task<JsonArray> WaitAsync(string account, long after, TimeSpan wait, CancellationToken ct)
    {
        Task signal;
        lock (sync)
        {
            var box = Box(account, clock.GetUtcNow());
            var ready = Pending(box, after);
            if (ready.Count > 0 || wait <= TimeSpan.Zero) return ready;
            if (box.Signal.Task.IsCompleted) box.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = box.Signal.Task;
        }
        try { await signal.WaitAsync(wait, clock, ct); }
        catch (TimeoutException) { }
        lock (sync) return Pending(Box(account, clock.GetUtcNow()), after);
    }

    private static JsonArray Pending(Mailbox box, long after) =>
        new(box.Events.Where(e => e.Id > after).Select(e => (JsonNode)e.Event.DeepClone()).ToArray());

    public static bool Owns(PathString path) => path.Equals("/relay/emote", StringComparison.Ordinal);

    public static async Task Handle(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = RelayIdentity.Read(context);
        if (identity is null) { context.Response.StatusCode = 404; return; }
        var hub = context.RequestServices.GetRequiredService<EmoteHub>();
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024;
            if (context.Request.ContentLength > 1024) throw new BadHttpRequestException("Dance event too large.", 413);
            JsonObject? body;
            try { body = await context.Request.ReadFromJsonAsync<JsonObject>(context.RequestAborted); }
            catch (JsonException) { throw new BadHttpRequestException("Invalid dance event.", 400); }
            if (body is null) throw new BadHttpRequestException("Dance event is required.", 400);
            var delivered = hub.Post(identity.AccountId, body);
            await context.Response.WriteAsJsonAsync(new JsonObject { ["delivered"] = delivered, ["epoch"] = hub.Epoch });
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method))
        {
            long after = 0;
            if (context.Request.Query.TryGetValue("after", out var value) &&
                (value.Count != 1 || !long.TryParse(value[0], out after) || after < 0))
                throw new BadHttpRequestException("Invalid after.", 400);
            var waitSeconds = 25;
            if (context.Request.Query.TryGetValue("wait", out var w) &&
                (w.Count != 1 || !int.TryParse(w[0], out waitSeconds) || waitSeconds is < 0 or > 30))
                throw new BadHttpRequestException("Invalid wait.", 400);
            JsonArray events;
            try { events = await hub.WaitAsync(identity.AccountId, after, TimeSpan.FromSeconds(waitSeconds), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            await context.Response.WriteAsJsonAsync(new JsonObject { ["epoch"] = hub.Epoch, ["events"] = events });
            return;
        }
        context.Response.StatusCode = 405;
        context.Response.Headers.Allow = "GET, POST";
    }
}
