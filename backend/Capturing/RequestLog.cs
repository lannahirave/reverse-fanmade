using System.Text.Json;
using ReVerse.Traffic;

namespace ReVerse.Capture.Capturing;

public sealed class RequestLog(string directoryPath) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly JsonFileLog log = new(directoryPath, "requests");
    public string DirectoryPath => log.DirectoryPath;
    public Exception? LastError => log.LastError;
    public event Action<Exception>? Failed { add => log.Failed += value; remove => log.Failed -= value; }

    public Task WriteAsync(object value)
    {
        log.Write(JsonSerializer.Serialize(value, JsonOptions));
        return Task.CompletedTask;
    }

    public void Dispose() => log.Dispose();
}
