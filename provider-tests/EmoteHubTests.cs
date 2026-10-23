using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ReVerse.Capture.Matchmaking;

namespace Provider.Tests;

internal static class EmoteHubTests
{
    private static JsonObject Play(int member = 1, int motion = 24) => new()
    {
        ["action"] = "play", ["member"] = member, ["bank"] = 20000, ["motion"] = motion,
        ["loop"] = true, ["hide_weapon"] = true, ["hold"] = true, ["char"] = "jill", ["name"] = "Robot Dance", ["extra"] = "dropped"
    };

    private static EmoteHub Hub() => new(account => account switch
    {
        "a" or "b" or "c" => ["a", "b", "c"],
        "x" => ["x"],
        _ => null,
    });

    internal static void Sanitize()
    {
        var clean = EmoteHub.Sanitize(Play());
        Assert.That(clean["extra"] is null, "unknown fields are dropped");
        Assert.That((int)clean["motion"]! == 24 && (bool)clean["loop"]! && (string)clean["char"]! == "jill", "allowed fields are copied");
        Assert.That((bool)clean["hide_weapon"]!, "hide_weapon is copied");
        Assert.That((bool)clean["hold"]!, "hold is copied");
        var plain = EmoteHub.Sanitize(new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 20000, ["motion"] = 1 });
        Assert.That(plain["hide_weapon"] is JsonValue hv && !hv.GetValue<bool>(), "hide_weapon defaults to false");
        Assert.That(plain["hold"] is JsonValue ov && !ov.GetValue<bool>(), "hold defaults to false");
        var odd = EmoteHub.Sanitize(new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 20000, ["motion"] = 1, ["hide_weapon"] = "yes" });
        Assert.That(!odd["hide_weapon"]!.GetValue<bool>(), "a non-boolean hide_weapon is false");
        var stop = EmoteHub.Sanitize(new JsonObject { ["action"] = "stop", ["member"] = 2, ["bank"] = 5 });
        Assert.That(stop.Count == 2, "stop carries only action and member");
        foreach (var bad in new[]
        {
            new JsonObject { ["action"] = "spin", ["member"] = 0 },
            new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 1000, ["motion"] = 1 },
            new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 20000, ["motion"] = 999 },
            new JsonObject { ["action"] = "play", ["member"] = 99, ["bank"] = 20000, ["motion"] = 1 },
            new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 20000, ["motion"] = 1, ["char"] = "../x" },
            new JsonObject { ["action"] = "play", ["member"] = 0, ["bank"] = 20000, ["motion"] = 1, ["name"] = "a\nb" },
            new JsonObject { ["action"] = "stop" },
        })
            Assert.Throws<BadHttpRequestException>(() => EmoteHub.Sanitize(bad), "rejects " + bad.ToJsonString());
    }

    internal static void FanOut()
    {
        var hub = Hub();
        Assert.That(hub.Post("a", Play()) == 2, "event is queued for the two peers");
        var forB = hub.WaitAsync("b", 0, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        var forC = hub.WaitAsync("c", 0, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        var forA = hub.WaitAsync("a", 0, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(forB.Count == 1 && forC.Count == 1 && forA.Count == 0, "peers receive it, the sender does not");
        Assert.That((string)forB[0]!["from"]! == "a" && (string)forB[0]!["action"]! == "play", "event carries sender and action");
        var id = (long)forB[0]!["id"]!;
        Assert.That(hub.WaitAsync("b", id, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult().Count == 0,
            "after= filters delivered events");
        Assert.That(hub.Post("x", Play()) == 0, "a session of one delivers nothing");
        Assert.That(hub.Post("nobody", Play()) == 0, "no session delivers nothing");
        Assert.That(hub.WaitAsync("x", 0, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult().Count == 0,
            "the lone sender does not receive its own event");
    }

    internal static void LongPollWakes()
    {
        var hub = Hub();
        var started = DateTime.UtcNow;
        var waiting = hub.WaitAsync("b", 0, TimeSpan.FromSeconds(10), CancellationToken.None);
        Thread.Sleep(100);
        Assert.That(!waiting.IsCompleted, "poll waits while the mailbox is empty");
        hub.Post("a", Play());
        var events = waiting.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.That(events.Count == 1, "poll returns the posted event");
        Assert.That(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "poll wakes on post, not on timeout");

        var after = (long)events[0]!["id"]!;
        var again = hub.WaitAsync("b", after, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Thread.Sleep(100);
        Assert.That(!again.IsCompleted, "completed signal is renewed for the next poll");
        Assert.That(again.GetAwaiter().GetResult().Count == 0, "timeout returns an empty list");

        using var cts = new CancellationTokenSource();
        var cancelled = hub.WaitAsync("c", 99, TimeSpan.FromSeconds(10), cts.Token);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult(), "cancellation ends the poll");
    }

    internal static void Bounds()
    {
        var members = Enumerable.Range(0, 5).Select(i => "s" + i).Append("r").ToArray();
        var hub = new EmoteHub(account => members.Contains(account) ? members : null);
        for (var i = 0; i < EmoteHub.RatePerWindow; i++) hub.Post("s0", Play(motion: i));
        Assert.Throws<BadHttpRequestException>(() => hub.Post("s0", Play()), "sender is rate limited");
        for (var s = 1; s < 5; s++)
            for (var i = 0; i < EmoteHub.RatePerWindow; i++)
                hub.Post("s" + s, i % 2 == 0 ? Play() : new JsonObject { ["action"] = "stop", ["member"] = s });
        var forR = hub.WaitAsync("r", 0, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(5 * EmoteHub.RatePerWindow > EmoteHub.MailboxLimit, "test posts more than the mailbox holds");
        Assert.That(forR.Count == EmoteHub.MailboxLimit, "mailbox is bounded to the newest events");
        var ids = forR.Select(e => (long)e!["id"]!).ToArray();
        Assert.That(ids.SequenceEqual(ids.Order()) && ids.Distinct().Count() == ids.Length, "ids are unique and ascending");
        Assert.That((string)forR[^1]!["from"]! == "s4", "newest event is kept");
    }
}
