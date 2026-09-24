using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using ReVerse.Capture.Capturing;
using ReVerse.Capture.Matchmaking;
using ReVerse.Capture.Protocol;
using ReVerse.Traffic;

namespace Provider.Tests;

internal static class NotificationLifetimeTests
{
    internal static void Run() => Check().GetAwaiter().GetResult();

    private static async Task Check()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reverse-notification-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new RequestLog(folder);
            using var detailed = new DetailedTrafficLog(folder);
            var hub = new NotificationHub(log, new GameState(), detailed);
            var context = new DefaultHttpContext();
            var socket = new FailedWriterSocket();
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            var disconnected = 0;
            try
            {
                await hub.RunAsync(context, "account", () => disconnected++).WaitAsync(TimeSpan.FromSeconds(5));
                throw new Exception("Expected writer failure");
            }
            catch (InvalidOperationException exception) when (exception.Message == "send failed") { }
            Assert.That(socket.SendAttempted && socket.ReceiveCancelled, "writer failure cancels the pending receiver");
            Assert.That(socket.Aborted && disconnected == 1 && !hub.IsOnline("account"), "writer failure performs all connection cleanup once");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        Console.WriteLine("PASS notification writer failure wakes receive and guarantees disconnect cleanup");
    }

    private sealed class SocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class FailedWriterSocket : WebSocket
    {
        private bool assigned;
        private readonly TaskCompletionSource receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SendAttempted { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public bool Aborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await receiving.Task.WaitAsync(cancellationToken);
            SendAttempted = true;
            throw new InvalidOperationException("send failed");
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!assigned)
            {
                assigned = true;
                var bytes = Encoding.UTF8.GetBytes("{\"command\":\"session_assign\"}");
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }
            receiving.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { ReceiveCancelled = true; throw; }
            throw new Exception("Unexpected receive completion");
        }
    }
}
