using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReVerse.Relay;

public sealed class EmoteBridge : IAsyncDisposable
{
    public const string OutboxFile = "victory_dance_out.json";
    public const string InboxFile = "victory_dance_in.json";
    public const string LinkFile = "victory_dance_link.json";
    private const int InboxKeep = 32;
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromSeconds(15);

    private readonly HttpClient client;
    private readonly Uri upstream;
    private readonly Func<string> token;
    private readonly Func<string?> locate;
    private readonly TimeSpan outboxInterval;
    private readonly TimeSpan locateInterval;
    private readonly int pollWaitSeconds;
    private readonly CancellationTokenSource stopping = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly List<JsonObject> inbox = [];
    private readonly object stateLock = new();
    private Task? outboxTask, inboxTask;
    private string? dataDirectory;
    private DateTimeOffset nextLocate = DateTimeOffset.MinValue;
    private string? outboxNonce;
    private long outboxSeq;
    private DateTime outboxStamp;
    private long outboxLength = -1;
    private string inboxEpoch = "";
    private string linkState = "starting", linkDetail = "";
    private DateTimeOffset linkWritten = DateTimeOffset.MinValue;

    public int Sent { get; private set; }
    public int Received { get; private set; }

    public EmoteBridge(HttpClient client, Uri upstream, Func<string> token, Func<string?>? locateDataDirectory = null,
        TimeSpan? outboxInterval = null, TimeSpan? locateInterval = null, int pollWaitSeconds = 25)
    {
        this.client = client;
        this.upstream = upstream;
        this.token = token;
        locate = locateDataDirectory ?? (() => FindGameDataDirectory(null));
        this.outboxInterval = outboxInterval ?? TimeSpan.FromMilliseconds(100);
        this.locateInterval = locateInterval ?? TimeSpan.FromSeconds(5);
        this.pollWaitSeconds = pollWaitSeconds;
    }

    public static string? FindGameDataDirectory(string? configuredGameDirectory)
    {
        static string? Data(string? game) =>
            !string.IsNullOrWhiteSpace(game) && Directory.Exists(Path.Combine(game, "reframework"))
                ? Path.Combine(game, "reframework", "data") : null;
        if (Data(configuredGameDirectory) is { } configured) return configured;
        foreach (var process in Process.GetProcessesByName("Reverse"))
        {
            using (process)
            {
                try
                {
                    if (Data(Path.GetDirectoryName(process.MainModule?.FileName)) is { } found) return found;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return null;
    }

    public void Start()
    {
        if (outboxTask is not null) throw new InvalidOperationException("Already started.");
        outboxTask = Task.Run(() => OutboxLoop(stopping.Token));
        inboxTask = Task.Run(() => InboxLoop(stopping.Token));
    }

    private string? DataDirectory()
    {
        var now = DateTimeOffset.UtcNow;
        lock (stateLock)
        {
            if (dataDirectory is not null && !Directory.Exists(Path.GetDirectoryName(dataDirectory))) dataDirectory = null;
            if (dataDirectory is null && now >= nextLocate)
            {
                nextLocate = now + locateInterval;
                try { dataDirectory = locate(); }
                catch (Exception) { }
                if (dataDirectory is not null) Directory.CreateDirectory(dataDirectory);
            }
            return dataDirectory;
        }
    }

    private void SetLink(string state, string detail = "")
    {
        var dir = DataDirectory();
        lock (stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (state == linkState && detail == linkDetail && now - linkWritten < TimeSpan.FromSeconds(5)) return;
            linkState = state;
            linkDetail = detail;
            if (dir is null) return;
            var link = new JsonObject { ["state"] = state, ["t"] = now.ToUnixTimeSeconds() };
            if (detail.Length > 0) link["detail"] = detail;
            if (WriteAtomic(Path.Combine(dir, LinkFile), link.ToJsonString())) linkWritten = now;
        }
    }

    private static bool WriteAtomic(string path, string text)
    {
        var temp = path + ".relay.tmp";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, path, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(15); }
        }
        return false;
    }

    private HttpRequestMessage Request(HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, new Uri(upstream, pathAndQuery));
        request.Headers.TryAddWithoutValidation(RelayService.SessionHeader, token());
        return request;
    }

    private async Task OutboxLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadOutbox(ct);
                await Task.Delay(outboxInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ReadOutbox(CancellationToken ct)
    {
        var dir = DataDirectory();
        if (dir is null) { SetLink("no-game"); return; }
        var path = Path.Combine(dir, OutboxFile);
        var info = new FileInfo(path);
        if (!info.Exists || (info.LastWriteTimeUtc == outboxStamp && info.Length == outboxLength)) return;
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(ct);
        }
        catch (IOException) { return; }
        JsonObject? box;
        try { box = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return; }
        if (box?["events"] is not JsonArray events || box["nonce"]?.GetValue<string>() is not { } nonce) return;
        outboxStamp = info.LastWriteTimeUtc;
        outboxLength = info.Length;
        var firstSight = outboxNonce is null;
        if (nonce != outboxNonce)
        {
            outboxNonce = nonce;
            outboxSeq = 0;
            if (firstSight && info.LastWriteTimeUtc < started.UtcDateTime)
            {
                outboxSeq = events.Select(Seq).DefaultIfEmpty(0).Max();
                return;
            }
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var ev in events.OfType<JsonObject>().OrderBy(Seq))
        {
            var seq = Seq(ev);
            if (seq <= outboxSeq) continue;
            outboxSeq = seq;
            var t = ev["t"] is JsonValue tv && tv.TryGetValue<long>(out var tt) ? tt : 0;
            if (Math.Abs(now - t) > MaxEventAge.TotalSeconds) continue;
            var body = new JsonObject();
            foreach (var key in new[] { "action", "member", "bank", "motion", "loop", "hide_weapon", "hold", "char", "name" })
                if (ev[key] is { } value) body[key] = value.DeepClone();
            using var request = Request(HttpMethod.Post, "/relay/emote");
            request.Content = JsonContent.Create(body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await client.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode) SetLink("error", $"send HTTP {(int)response.StatusCode}");
                else if (await DanceReply(response, timeout.Token)) { Sent++; SetLink("online"); }
                else SetLink("error", "backend does not support dance sync");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                SetLink("offline", "backend unreachable");
            }
        }
    }

    private static async Task<bool> DanceReply(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["delivered"] is JsonValue; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return false; }
    }

    private static long Seq(JsonNode? ev) =>
        ev?["seq"] is JsonValue v && v.TryGetValue<long>(out var s) ? s : 0;

    private async Task InboxLoop(CancellationToken ct)
    {
        long after = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = after < 0 ? 0 : pollWaitSeconds;
                using var request = Request(HttpMethod.Get, $"/relay/emote?after={Math.Max(after, 0)}&wait={wait}");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(wait + 15));
                using var response = await client.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    SetLink("error", $"poll HTTP {(int)response.StatusCode}");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }
                var reply = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
                if (reply?["epoch"] is not JsonValue epochValue || !epochValue.TryGetValue<string>(out var epoch))
                {
                    SetLink("error", "backend does not support dance sync");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                var events = (reply?["events"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                SetLink("online");
                if (epoch != inboxEpoch)
                {
                    inboxEpoch = epoch;
                    if (after >= 0) after = 0;
                }
                var max = events.Select(e => e["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0)
                    .DefaultIfEmpty(0).Max();
                if (after < 0) { after = max; WriteInbox(); continue; }
                after = Math.Max(after, max);
                if (events.Count == 0) continue;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var ev in events)
                {
                    var copy = (JsonObject)ev.DeepClone();
                    copy["t"] = now;
                    inbox.Add(copy);
                    Received++;
                }
                if (inbox.Count > InboxKeep) inbox.RemoveRange(0, inbox.Count - InboxKeep);
                WriteInbox();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                SetLink("offline", "backend unreachable");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void WriteInbox()
    {
        var dir = DataDirectory();
        if (dir is null) return;
        var box = new JsonObject
        {
            ["v"] = 1, ["epoch"] = inboxEpoch,
            ["events"] = new JsonArray(inbox.Select(e => (JsonNode)e.DeepClone()).ToArray())
        };
        WriteAtomic(Path.Combine(dir, InboxFile), box.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        foreach (var task in new[] { outboxTask, inboxTask })
            if (task is not null)
                try { await task; } catch (OperationCanceledException) { }
        SetLink("stopped");
        stopping.Dispose();
    }
}
