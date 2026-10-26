using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReVerse.Traffic;

namespace ReVerse.Relay;

public sealed class RelayService : IAsyncDisposable
{
    public const string PlayerHeader = "X-Relay-Player-Id";
    public const string NameHeader = "X-Relay-Username";
    public const string SessionHeader = "X-Relay-Session-Token";
    public const string NgrokSkipBrowserWarningHeader = "ngrok-skip-browser-warning";
    public const string ApiKeyHeader = "x-api-key";
    private string sessionToken = "";
    private string apiKey = "";
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource stopping = new();
    private readonly DetailedTrafficLog detailed;
    private WebApplication? app;
    private X509Certificate2? certificate;
    private EmoteBridge? emotes;
    public event Action<RelayStatus>? StatusChanged;
    public event Action<Exception>? DetailedLogError
    {
        add => detailed.Failed += value;
        remove => detailed.Failed -= value;
    }
    public bool DetailedLogsEnabled { get => detailed.Enabled; set => detailed.Enabled = value; }
    public string DetailedLogsDirectory => detailed.DirectoryPath;
    public Func<string?>? DanceDataLocator { get; init; }

    public RelayService(string? detailedLogsDirectory = null)
    {
        detailed = new DetailedTrafficLog(detailedLogsDirectory ??
            RelaySettings.DetailedLogsDirectory);
    }

    public async Task StartAsync(RelaySettings settings, int httpPort = 5080, int httpsPort = 5081,
        string? certificateDirectory = null)
    {
        if (app is not null) throw new RelayException(RelayErrorCode.RelayAlreadyRunning);
        var upstream = settings.Validate();
        var addresses = await Dns.GetHostAddressesAsync(upstream.DnsSafeHost);
        if (addresses.Any(IPAddress.IsLoopback) && (upstream.Port == httpPort || upstream.Port == httpsPort))
            throw new RelayException(RelayErrorCode.BackendPointsToRelay);
        apiKey = settings.ServerApiKey;
        client.DefaultRequestHeaders.Remove(ApiKeyHeader);
        if (apiKey.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        StatusChanged?.Invoke(RelayStatus.SigningIn);
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        loginTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var loginUrl = new Uri(upstream, "/relay/account/login");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = JsonContent.Create(new { username = settings.Username, secretKey = settings.SecretKey })
        };
        loginRequest.Headers.TryAddWithoutValidation(NgrokSkipBrowserWarningHeader, "1");
        var loginBytes = await loginRequest.Content.ReadAsByteArrayAsync(loginTimeout.Token);
        detailed.Write("relayLoginRequest", new
        {
            url = loginUrl.ToString(), method = "POST",
            headers = loginRequest.Headers.Concat(loginRequest.Content.Headers)
                .ToDictionary(p => p.Key, p => p.Value.ToArray()),
            payload = DetailedTrafficLog.Payload(loginBytes)
        });
        HttpResponseMessage sent;
        try
        {
            sent = await client.SendAsync(loginRequest, loginTimeout.Token);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError
            || exception.InnerException is IOException)
        {
            throw new RelayException(RelayErrorCode.ServerUnreachable, innerException: exception);
        }
        using var login = sent;
        var loginResponseBytes = await login.Content.ReadAsByteArrayAsync(loginTimeout.Token);
        detailed.Write("relayLoginResponse", new
        {
            url = loginUrl.ToString(), statusCode = (int)login.StatusCode,
            headers = login.Headers.Concat(login.Content.Headers).ToDictionary(p => p.Key, p => p.Value.ToArray()),
            payload = DetailedTrafficLog.Payload(loginResponseBytes)
        });
        if (login.StatusCode == HttpStatusCode.Unauthorized)
            throw new RelayException(RelayErrorCode.ServerApiKeyRejected, (int)login.StatusCode);
        if (!login.IsSuccessStatusCode)
            throw new RelayException(RelayErrorCode.AccountSignInFailed, (int)login.StatusCode);
        var account = JsonSerializer.Deserialize<RelayAccount>(loginResponseBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new RelayException(RelayErrorCode.BackendEmptyAccountResponse);
        if (!Guid.TryParseExact(account.AccountId, "N", out _) || account.SessionToken is not { Length: 64 } ||
            !account.SessionToken.All(Uri.IsHexDigit) || string.IsNullOrEmpty(account.Username))
            throw new RelayException(RelayErrorCode.BackendInvalidAccountResponse);
        sessionToken = account.SessionToken;
        var playerId = account.AccountId;
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Username));
        certificate = RelayCertificate.LoadOrCreate(certificateDirectory ?? RelaySettings.DataDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;
            o.Listen(IPAddress.Loopback, httpPort);
            o.Listen(IPAddress.Loopback, httpsPort, listen => listen.UseHttps(certificate));
        });
        app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token);
            var traceId = Guid.NewGuid().ToString("N");
            var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "/";
            detailed.Write("relayRequestStart", new
            {
                id = traceId, method = context.Request.Method, rawTarget = raw,
                url = $"{context.Request.Scheme}://{context.Request.Host}{raw}",
                remoteAddress = context.Connection.RemoteIpAddress?.ToString(),
                remotePort = context.Connection.RemotePort,
                localAddress = context.Connection.LocalIpAddress?.ToString(),
                localPort = context.Connection.LocalPort,
                headers = context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToArray()),
                resolvedBackendAddresses = addresses.Select(address => address.ToString()).ToArray()
            });
            try
            {
                if (!raw.StartsWith('/') || raw.StartsWith("//") || raw.Contains('#'))
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                var target = new Uri(upstream.GetLeftPart(UriPartial.Authority) + raw);
                if (context.WebSockets.IsWebSocketRequest)
                    await ForwardWebSocket(context, target, playerId, name, traceId, linked.Token);
                else
                    await ForwardHttp(context, target, playerId, name, traceId, linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { context.Abort(); }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or OperationCanceledException)
            {
                detailed.Write("relayRequestError", new { id = traceId, error = ex.ToString() });
                StatusChanged?.Invoke(RelayStatus.BackendConnectionFailed);
                if (context.Response.HasStarted) context.Abort();
                else { context.Response.StatusCode = 502; await context.Response.WriteAsync("Backend connection failed."); }
            }
            finally
            {
                detailed.Write("relayResponseEnd", new
                {
                    id = traceId, statusCode = context.Response.StatusCode,
                    headers = context.Response.Headers.ToDictionary(p => p.Key, p => p.Value.ToArray())
                });
            }
        });
        try { await app.StartAsync(); }
        catch { await app.DisposeAsync(); app = null; certificate.Dispose(); certificate = null; throw; }
        var gameDirectory = settings.GameDirectory;
        emotes = new EmoteBridge(client, upstream, () => sessionToken,
            DanceDataLocator ?? (() => EmoteBridge.FindGameDataDirectory(gameDirectory)));
        emotes.Start();
        StatusChanged?.Invoke(RelayStatus.RunningWaitingForGame);
    }

    private static HashSet<string> Excluded(IEnumerable<string> connectionValues)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
          "TE", "Trailer", "Transfer-Encoding", "Upgrade", PlayerHeader, NameHeader, SessionHeader,
          NgrokSkipBrowserWarningHeader, ApiKeyHeader };
        foreach (var value in connectionValues)
            foreach (var item in value.Split(',')) result.Add(item.Trim());
        return result;
    }

    private async Task ForwardHttp(HttpContext context, Uri target, string playerId, string name, string traceId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            request.Content = new StreamContent(new TrafficTapStream(context.Request.Body, onRead: bytes =>
                detailed.Write("relayRequestBody", new { id = traceId, payload = DetailedTrafficLog.Payload(bytes.Span) })));
        var excluded = Excluded(context.Request.Headers.Connection.Select(v => v ?? ""));
        foreach (var header in context.Request.Headers)
        {
            if (excluded.Contains(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                if (request.Content is null && header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    request.Content = new ByteArrayContent([]);
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        request.Headers.TryAddWithoutValidation(PlayerHeader, playerId);
        request.Headers.TryAddWithoutValidation(NameHeader, name);
        request.Headers.TryAddWithoutValidation(SessionHeader, sessionToken);
        request.Headers.TryAddWithoutValidation(NgrokSkipBrowserWarningHeader, "1");
        detailed.Write("relayUpstreamRequest", new
        {
            id = traceId, method = request.Method.Method, url = target.ToString(),
            headers = request.Headers.Concat(request.Content is null
                ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                : request.Content.Headers).ToDictionary(p => p.Key, p => p.Value.ToArray())
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        detailed.Write("relayUpstreamResponse", new
        {
            id = traceId, statusCode = (int)response.StatusCode,
            headers = response.Headers.Concat(response.Content.Headers).ToDictionary(p => p.Key, p => p.Value.ToArray())
        });
        context.Response.StatusCode = (int)response.StatusCode;
        var responseExcluded = Excluded(response.Headers.Connection);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!responseExcluded.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

        if (response.Headers.Location is { IsAbsoluteUri: true } location && location.Authority == target.Authority)
            context.Response.Headers.Location = $"{context.Request.Scheme}://{context.Request.Host}{location.PathAndQuery}{location.Fragment}";
        if (!HttpMethods.IsHead(context.Request.Method) && (int)response.StatusCode is not (204 or 205 or 304))
            await response.Content.CopyToAsync(new TrafficTapStream(context.Response.Body, onWrite: bytes =>
                detailed.Write("relayResponseBody", new { id = traceId, payload = DetailedTrafficLog.Payload(bytes.Span) })), ct);
        StatusChanged?.Invoke(response.StatusCode == HttpStatusCode.Unauthorized
            ? RelayStatus.SessionExpired
            : RelayStatus.RunningConnectedToBackend);
    }

    private async Task ForwardWebSocket(HttpContext context, Uri target, string playerId, string name, string traceId, CancellationToken ct)
    {
        using var remote = new ClientWebSocket();
        var excluded = Excluded(context.Request.Headers.Connection.Select(v => v ?? ""));
        foreach (var header in context.Request.Headers)
            if (!excluded.Contains(header.Key) && !header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                remote.Options.SetRequestHeader(header.Key, header.Value.ToString());
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) remote.Options.AddSubProtocol(protocol);
        remote.Options.SetRequestHeader(PlayerHeader, playerId);
        remote.Options.SetRequestHeader(NameHeader, name);
        remote.Options.SetRequestHeader(SessionHeader, sessionToken);
        remote.Options.SetRequestHeader(NgrokSkipBrowserWarningHeader, "1");
        if (apiKey.Length > 0) remote.Options.SetRequestHeader(ApiKeyHeader, apiKey);
        var wsTarget = new UriBuilder(target) { Scheme = target.Scheme == "https" ? "wss" : "ws" };
        detailed.Write("relayWebSocketConnect", new
        {
            id = traceId, url = wsTarget.Uri.ToString(),
            remoteAddress = context.Connection.RemoteIpAddress?.ToString(),
            headers = context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToArray()),
            playerId, name, sessionToken
        });
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await remote.ConnectAsync(wsTarget.Uri, connectTimeout.Token);
        }
        using var local = await context.WebSockets.AcceptWebSocketAsync(remote.SubProtocol);
        detailed.Write("relayWebSocketConnected", new { id = traceId, url = wsTarget.Uri.ToString(), subProtocol = remote.SubProtocol });
        StatusChanged?.Invoke(RelayStatus.RunningConnectedToBackend);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toRemote = Pump(local, remote, traceId, "clientToBackend", pumps.Token);
        var toLocal = Pump(remote, local, traceId, "backendToClient", pumps.Token);
        try
        {
            var first = await Task.WhenAny(toRemote, toLocal);
            await first;
            await Task.WhenAll(toRemote, toLocal).WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch (TimeoutException) { }
        finally
        {
            await pumps.CancelAsync();
            local.Abort(); remote.Abort();
            try { await Task.WhenAll(toRemote, toLocal); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        }
    }

    private async Task Pump(WebSocket source, WebSocket destination, string traceId, string direction, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await destination.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription, ct);
                detailed.Write("relayWebSocketClose", new
                {
                    id = traceId, direction, closeStatus = result.CloseStatus?.ToString(), result.CloseStatusDescription
                });
                return;
            }
            await destination.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType, result.EndOfMessage, ct);
            detailed.Write("relayWebSocketFrame", new
            {
                id = traceId, direction, messageType = result.MessageType.ToString(), result.EndOfMessage,
                payload = DetailedTrafficLog.Payload(buffer.AsSpan(0, result.Count))
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        if (emotes is not null) { await emotes.DisposeAsync(); emotes = null; }
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        client.Dispose(); certificate?.Dispose(); stopping.Dispose();
        detailed.Dispose();
        sessionToken = ""; apiKey = "";
    }

    private sealed record RelayAccount(string AccountId, string Username, string SessionToken);
}
