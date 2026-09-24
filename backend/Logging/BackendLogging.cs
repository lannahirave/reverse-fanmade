using System.Globalization;
using ReVerse.Traffic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace ReVerse.Capture.Logging;

public static class BackendLogging
{
    public static Logger Create(string directory) => new LoggerConfiguration()
        .MinimumLevel.Debug()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Logger(console => console
            .Filter.ByIncludingOnly(IsConsoleEvent)
            .WriteTo.Console(new ActivityFormatter()))
        .WriteTo.Sink(new DiagnosticFileSink(directory))
        .CreateLogger();

    private static bool IsConsoleEvent(LogEvent entry) =>
        entry.Properties.TryGetValue("UserFacing", out var value) && value is ScalarValue { Value: true }
        || entry.Level >= LogEventLevel.Error;

    private sealed class ActivityFormatter : ITextFormatter
    {
        public void Format(LogEvent entry, TextWriter output)
        {
            var message = entry.Properties.TryGetValue("UserFacing", out var value) && value is ScalarValue { Value: true }
                ? entry.RenderMessage(CultureInfo.InvariantCulture)
                : "The backend encountered an error. Check the backend log for details.";
            output.WriteLine($"[{entry.Timestamp:HH:mm:ss} {entry.Level}] {message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
    }

    private sealed class DiagnosticFileSink : ILogEventSink, IDisposable
    {
        private readonly JsonFileLog file;
        private readonly JsonFormatter formatter = new(renderMessage: true);

        public DiagnosticFileSink(string directory)
        {
            file = new JsonFileLog(directory, "backend");
            file.Failed += _ => Console.Error.WriteLine("[Error] Cannot write backend logs. Check the log folder and available disk space.");
        }

        public void Emit(LogEvent entry)
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            formatter.Format(entry, text);
            file.Write(text.ToString().TrimEnd('\r', '\n'));
        }

        public void Dispose() => file.Dispose();
    }
}
