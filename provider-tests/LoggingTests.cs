using System.Text.Json;
using ReVerse.Capture.Capturing;
using ReVerse.Traffic;

namespace Provider.Tests;

internal static class LoggingTests
{
    internal static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reverse-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var payload = new string('x', 20_000) + "\n\"quoted\"";
            using (var requests = new RequestLog(folder))
            using (var detailed = new DetailedTrafficLog(folder))
            {
                detailed.Write("disabled", new { });
                Assert.That(!Directory.GetFiles(folder, "detailed-traffic-*.jsonl").Any(), "disabled logging creates no file");
                detailed.Enabled = true;
                for (var i = 0; i < 140; i++)
                {
                    requests.WriteAsync(new { type = "request", index = i, payload }).GetAwaiter().GetResult();
                    detailed.Write("traffic", new { index = i, payload });
                }
                Parallel.For(140, 340, i => requests.WriteAsync(new { type = "request", index = i, payload }).GetAwaiter().GetResult());
                Assert.That(requests.LastError is null && detailed.LastError is null, "healthy writes report no error");
            }
            CheckFiles(folder, "requests", 340, false);
            CheckFiles(folder, "detailed-traffic", 140, true);
            using (var append = new RequestLog(folder))
                append.WriteAsync(new { type = "request", index = 340, payload }).GetAwaiter().GetResult();
            CheckFiles(folder, "requests", 341, false);

            var blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "not a directory");
            using var failedRequests = new RequestLog(blocked);
            using var failedDetailed = new DetailedTrafficLog(blocked) { Enabled = true };
            var requestFailures = 0;
            var detailedFailures = 0;
            failedRequests.Failed += _ => requestFailures++;
            failedDetailed.Failed += _ => detailedFailures++;
            for (var i = 0; i < 2; i++)
            {
                failedRequests.WriteAsync(new { index = i }).GetAwaiter().GetResult();
                failedDetailed.Write("failure", new { index = i });
            }
            Assert.That(failedRequests.LastError is not null && failedDetailed.LastError is not null, "file failures remain observable");
            Assert.That(requestFailures == 1 && detailedFailures == 1, "repeated failures notify once");
        }
        finally { Directory.Delete(folder, recursive: true); }
        Console.WriteLine("PASS Serilog JSON compatibility, rolling, concurrent writes, reopen, disabled logging and file failure reporting");
    }

    private static void CheckFiles(string folder, string prefix, int count, bool detailed)
    {
        var files = Directory.GetFiles(folder, prefix + "-*.jsonl");
        Assert.That(files.Length > 1, "logging rolls at 1 MB");
        var indexes = new HashSet<int>();
        foreach (var file in files)
        {
            Assert.That(new FileInfo(file).Length < JsonFileLog.FileSizeLimitBytes + 21_000, "size limit permits at most the final event");
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                Assert.That(record.GetProperty("type").GetString() == (detailed ? "traffic" : "request"), "JSON envelope preserved");
                var data = detailed ? record.GetProperty("data") : record;
                Assert.That(indexes.Add(data.GetProperty("index").GetInt32()), "no duplicate record");
                Assert.That(data.GetProperty("payload").GetString()!.EndsWith("\n\"quoted\""), "JSON escaping preserved");
            }
        }
        Assert.That(indexes.Count == count && indexes.SetEquals(Enumerable.Range(0, count)), "all records survive rolling and disposal");
    }
}
