using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ReVerse.Traffic;

public sealed class JsonFileLog : IDisposable, ILoggingFailureListener
{
    public const long FileSizeLimitBytes = 1_000_000;
    private readonly object gate = new();
    private readonly Logger logger;
    private Exception? lastError;
    private bool disposed;

    public string DirectoryPath { get; }
    public Exception? LastError => Volatile.Read(ref lastError);
    public event Action<Exception>? Failed;

    public JsonFileLog(string directoryPath, string prefix)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
        logger = new LoggerConfiguration()
            .WriteTo.Fallible(sink => sink.File(
                Path.Combine(DirectoryPath, prefix + "-.jsonl"),
                outputTemplate: "{Message:lj}{NewLine}",
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null,
                encoding: new UTF8Encoding(false)), this)
            .CreateLogger();
    }

    public void Write(string json)
    {
        Exception? notification;
        lock (gate)
        {
            if (disposed) return;
            var previous = lastError;
            Volatile.Write(ref lastError, null);
            logger.Information("{Json:l}", json);
            notification = previous is null ? lastError : null;
        }
        if (notification is not null) Failed?.Invoke(notification);
    }

    void ILoggingFailureListener.OnLoggingFailed(object sender, LoggingFailureKind kind,
        string message, IReadOnlyCollection<LogEvent>? events, Exception? exception) =>
        Volatile.Write(ref lastError, exception ?? new IOException(message));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            logger.Dispose();
        }
    }
}
