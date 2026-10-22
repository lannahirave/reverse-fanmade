using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ReVerse.Relay;

internal static class EmoteBridgeTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Until(Func<bool> condition, string message, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("timed out: " + message);
            await Task.Delay(20);
        }
        Console.WriteLine("PASS " + message);
    }

    private static JsonObject? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (IOException) { return null; }
    }

    private static void WriteOutbox(string dir, string nonce, params JsonObject[] events) =>
        File.WriteAllText(Path.Combine(dir, EmoteBridge.OutboxFile),
            new JsonObject { ["v"] = 1, ["nonce"] = nonce, ["events"] = new JsonArray(events.Select(e => (JsonNode)e).ToArray()) }.ToJsonString());

    private static JsonObject Ev(long seq, string action = "play", long? t = null) => new()
    {
        ["seq"] = seq, ["t"] = t ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["action"] = action, ["member"] = 2,
        ["bank"] = 20000, ["motion"] = 24, ["loop"] = true, ["char"] = "jill", ["name"] = "Robot Dance", ["junk"] = "x"
    };

    public static async Task Run()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var token = new string('a', 64);
        var posts = new ConcurrentQueue<(string? Token, JsonObject Body)>();
        var queries = new ConcurrentQueue<string>();
        var releases = Channel.CreateUnbounded<JsonObject>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var fake = builder.Build();
        fake.Run(async context =>
        {
            if (context.Request.Path != "/relay/emote") { context.Response.StatusCode = 404; return; }
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var body = await context.Request.ReadFromJsonAsync<JsonObject>();
                posts.Enqueue((context.Request.Headers[RelayService.SessionHeader].ToString(), body!));
                await context.Response.WriteAsJsonAsync(new JsonObject { ["delivered"] = 1 });
                return;
            }
            queries.Enqueue(context.Request.QueryString.Value ?? "");
            if (context.Request.Query["wait"] == "0")
            {
                await context.Response.WriteAsJsonAsync(new JsonObject
                {
                    ["epoch"] = "e1", ["events"] = new JsonArray(new JsonObject { ["id"] = 5, ["action"] = "play", ["member"] = 1 })
                });
                return;
            }
            try
            {
                var next = await releases.Reader.ReadAsync(context.RequestAborted).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted);
                await context.Response.WriteAsJsonAsync(next);
            }
            catch (TimeoutException)
            {
                await context.Response.WriteAsJsonAsync(new JsonObject { ["epoch"] = "e1", ["events"] = new JsonArray() });
            }
        });
        await fake.StartAsync();

        var dir = Path.Combine(AppContext.BaseDirectory, "artifacts", "dance-" + Guid.NewGuid().ToString("N"), "reframework", "data");
        Directory.CreateDirectory(dir);
        WriteOutbox(dir, "n1", Ev(1));
        File.SetLastWriteTimeUtc(Path.Combine(dir, EmoteBridge.OutboxFile), DateTime.UtcNow.AddMinutes(-1));
        using var client = new HttpClient();
        var bridge = new EmoteBridge(client, new Uri($"http://127.0.0.1:{port}/"), () => token, () => dir,
            outboxInterval: TimeSpan.FromMilliseconds(20), pollWaitSeconds: 2);
        bridge.Start();
        var inboxPath = Path.Combine(dir, EmoteBridge.InboxFile);
        var linkPath = Path.Combine(dir, EmoteBridge.LinkFile);
        try
        {
            await Until(() => ReadJson(inboxPath)?["epoch"]?.GetValue<string>() == "e1", "inbox created at the current position");
            Check(ReadJson(inboxPath)!["events"]!.AsArray().Count == 0, "backend history (id 5) is not replayed into the inbox");
            await Task.Delay(300);
            Check(posts.IsEmpty, "outbox written before the relay started is not sent");
            await Until(() => ReadJson(linkPath)?["state"]?.GetValue<string>() == "online", "link file reports online");

            WriteOutbox(dir, "n1", Ev(1), Ev(2), Ev(3, t: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120));
            await Until(() => posts.Count >= 1, "new outbox event is posted");
            await Task.Delay(200);
            Check(posts.Count == 1, "stale event (old timestamp) is dropped");
            posts.TryDequeue(out var first);
            Check(first.Token == token, "post carries the relay session token");
            Check(first.Body["junk"] is null && first.Body["seq"] is null && first.Body["t"] is null, "only event fields are forwarded");
            Check((int)first.Body["motion"]! == 24 && (string)first.Body["action"]! == "play" && (int)first.Body["member"]! == 2,
                "event fields preserved");

            File.WriteAllText(Path.Combine(dir, EmoteBridge.OutboxFile), "{\"v\":1,\"nonce\":\"n1\",\"ev");
            await Task.Delay(150);
            WriteOutbox(dir, "n1", Ev(2), Ev(4, "stop"));
            await Until(() => posts.Count == 1, "torn write is retried; next event is posted once");
            posts.TryDequeue(out var stop);
            Check((string)stop.Body["action"]! == "stop", "stop event forwarded");

            WriteOutbox(dir, "n2", Ev(1));
            await Until(() => posts.Count == 1, "new script nonce restarts the sequence");
            posts.Clear();

            await releases.Writer.WriteAsync(new JsonObject
            {
                ["epoch"] = "e1",
                ["events"] = new JsonArray(new JsonObject { ["id"] = 6, ["action"] = "play", ["member"] = 3, ["bank"] = 20000, ["motion"] = 51 })
            });
            await Until(() => ReadJson(inboxPath)?["events"]?.AsArray().Count == 1, "peer event written to the inbox");
            var written = ReadJson(inboxPath)!["events"]![0]!;
            Check((long)written["id"]! == 6 && (int)written["motion"]! == 51 && written["t"] is not null, "inbox event has id, fields and local time");
            await Until(() => queries.Any(q => q.Contains("after=6")), "next poll continues after the delivered id");

            await releases.Writer.WriteAsync(new JsonObject
            {
                ["epoch"] = "e2", ["events"] = new JsonArray(new JsonObject { ["id"] = 1, ["action"] = "stop", ["member"] = 3 })
            });
            await Until(() => ReadJson(inboxPath)?["epoch"]?.GetValue<string>() == "e2", "backend restart (new epoch) is written");
            Check(ReadJson(inboxPath)!["events"]!.AsArray().Count == 2, "restart event kept after earlier ones");
            await Until(() => queries.Any(q => q.Contains("after=1&")), "ids restart from the new epoch");
        }
        finally
        {
            var stopped = bridge.DisposeAsync().AsTask();
            Check(await Task.WhenAny(stopped, Task.Delay(5000)) == stopped, "bridge stops promptly during a long poll");
            await stopped;
        }
        Check(ReadJson(linkPath)?["state"]?.GetValue<string>() == "stopped", "link file reports stopped");
        await fake.StopAsync();
    }

    public static async Task RunUnsupportedBackend()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var fake = builder.Build();
        fake.Run(context => context.Response.WriteAsync("{}"));
        await fake.StartAsync();
        var dir = Path.Combine(AppContext.BaseDirectory, "artifacts", "dance-" + Guid.NewGuid().ToString("N"), "reframework", "data");
        Directory.CreateDirectory(dir);
        using var client = new HttpClient();
        var bridge = new EmoteBridge(client, new Uri($"http://127.0.0.1:{port}/"), () => "token", () => dir,
            outboxInterval: TimeSpan.FromMilliseconds(20), pollWaitSeconds: 1);
        bridge.Start();
        var linkPath = Path.Combine(dir, EmoteBridge.LinkFile);
        try
        {
            await Until(() => ReadJson(linkPath)?["state"]?.GetValue<string>() == "error", "backend without dance sync is reported as an error");
            Check(ReadJson(linkPath)!["detail"]?.GetValue<string>() == "backend does not support dance sync", "error names the missing dance sync");
            WriteOutbox(dir, "n1", Ev(1));
            await Task.Delay(300);
            Check(bridge.Sent == 0 && ReadJson(linkPath)?["state"]?.GetValue<string>() == "error", "a reply without delivery is not counted as sent");
        }
        finally { await bridge.DisposeAsync(); await fake.StopAsync(); }
    }
}
