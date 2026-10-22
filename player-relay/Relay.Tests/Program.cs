using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ReVerse.Relay;

static int Port()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
await EmoteBridgeTests.Run();
await EmoteBridgeTests.RunUnsupportedBackend();
var upstreamPort = Port(); var relayPort = Port(); var tlsPort = Port();
var player = new RelaySettings { Username = "Гравець 🎮", SecretKey = "test secret", BackendAddress = $"http://127.0.0.1:{upstreamPort}" };
var accountId = Guid.NewGuid().ToString("N");
var accountToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
Check(!System.Text.Json.JsonSerializer.Serialize(player).Contains("test secret"), "settings do not serialize the secret");
var expectedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(player.Username));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(player.BackendAddress);
await using var upstream = builder.Build();
upstream.UseWebSockets();
upstream.Run(async context =>
{
    if (context.Request.Headers[RelayService.NgrokSkipBrowserWarningHeader].ToString() != "1")
    { context.Response.StatusCode = 428; return; }
    if (context.Request.Path == "/relay/account/login")
    {
        var login = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        if (login.GetProperty("username").GetString() != player.Username || login.GetProperty("secretKey").GetString() != player.SecretKey)
        { context.Response.StatusCode = 400; return; }
        await context.Response.WriteAsJsonAsync(new { accountId, username = player.Username, sessionToken = accountToken });
        return;
    }
    if (context.Request.Headers[RelayService.PlayerHeader] != accountId ||
        context.Request.Headers[RelayService.NameHeader] != expectedName ||
        context.Request.Headers[RelayService.SessionHeader] != accountToken)
    { context.Response.StatusCode = 403; return; }
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync("relay-test");
        var buffer = new byte[4096];
        while (true)
        {
            var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseOutputAsync(received.CloseStatus ?? WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
                return;
            }
            await ws.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
        }
    }
    else if (context.Request.Path == "/redirect")
    {
        context.Response.StatusCode = 307;
        context.Response.Headers.Location = player.BackendAddress + "/echo?q=a%2Fb";
    }
    else
    {
        context.Response.StatusCode = 201;
        context.Response.Headers["X-Query"] = context.Request.QueryString.Value;
        context.Response.Headers["X-Method"] = context.Request.Method;
        context.Response.Headers.Connection = "X-Private";
        context.Response.Headers["X-Private"] = "must not pass";
        context.Response.Headers.Append("Set-Cookie", "a=1");
        context.Response.Headers.Append("Set-Cookie", "b=2");
        await context.Request.Body.CopyToAsync(context.Response.Body);
    }
});
await upstream.StartAsync();
var certDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts", Guid.NewGuid().ToString("N"));
var detailedDirectory = Path.Combine(certDirectory, "detailed-logs");
var danceDirectory = Path.Combine(certDirectory, "dance");
var relay = new RelayService(detailedDirectory) { DetailedLogsEnabled = true, DanceDataLocator = () => danceDirectory };
await relay.StartAsync(player, relayPort, tlsPort, certDirectory);
try
{
    using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    var bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
    using var request = new HttpRequestMessage(HttpMethod.Patch, $"http://127.0.0.1:{relayPort}/echo?q=a%2Fb&x=1") { Content = new ByteArrayContent(bytes) };
    request.Headers.TryAddWithoutValidation(RelayService.PlayerHeader, "spoof");
    request.Headers.TryAddWithoutValidation(RelayService.NameHeader, "spoof");
    request.Headers.TryAddWithoutValidation(RelayService.SessionHeader, "spoof");
    using var response = await client.SendAsync(request);
    Check((int)response.StatusCode == 201, "status and injected identity override");
    Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "binary HTTP body preserved");
    Check(response.Headers.GetValues("X-Query").Single() == "?q=a%2Fb&x=1", "query preserved");
    Check(response.Headers.GetValues("X-Method").Single() == "PATCH", "method preserved");
    Check(!response.Headers.Contains("X-Private"), "connection-nominated header removed");
    Check(response.Headers.GetValues("Set-Cookie").Count() == 2, "multiple cookies preserved");
    using var redirect = await client.GetAsync($"http://127.0.0.1:{relayPort}/redirect");
    Check(redirect.StatusCode == HttpStatusCode.TemporaryRedirect && redirect.Headers.Location!.Port == relayPort, "redirect stays local");
    foreach (var secure in new[] { false, true })
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("relay-test");

        using var cert = RelayCertificate.LoadOrCreate(certDirectory);
        ws.Options.RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString();
        await ws.ConnectAsync(new Uri($"{(secure ? "wss" : "ws")}://127.0.0.1:{(secure ? tlsPort : relayPort)}/socket"), CancellationToken.None);
        Check(ws.SubProtocol == "relay-test", "WebSocket subprotocol " + secure);
        await ws.SendAsync(bytes.AsMemory(0, 100000), WebSocketMessageType.Binary, false, CancellationToken.None);
        await ws.SendAsync(bytes.AsMemory(100000), WebSocketMessageType.Binary, true, CancellationToken.None);
        using var received = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult frame;
        do
        {
            frame = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            received.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        Check(received.ToArray().SequenceEqual(bytes), "fragmented binary WebSocket round trip " + secure);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(ws.State == WebSocketState.Closed, "WebSocket close handshake " + secure);
    }
    string[] ReadDetailedLines()
    {
        return Directory.GetFiles(detailedDirectory, "detailed-traffic-*.jsonl").Order(StringComparer.Ordinal)
            .SelectMany(path =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(file);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }).ToArray();
    }
    var entries = ReadDetailedLines().Select(line => JsonNode.Parse(line)!).ToArray();
    Check(entries.Any(entry => entry["type"]?.GetValue<string>() == "relayLoginRequest" &&
        entry["data"]?["payload"]?["Utf8"]?.GetValue<string>()?.Contains(player.SecretKey, StringComparison.Ordinal) == true),
        "detailed relay login includes the full secret");
    var patchId = entries.First(entry => entry["type"]?.GetValue<string>() == "relayRequestStart" &&
        entry["data"]?["rawTarget"]?.GetValue<string>() == "/echo?q=a%2Fb&x=1")["data"]!["id"]!.GetValue<string>();
    byte[] Captured(string type, string id) => entries.Where(entry => entry["type"]?.GetValue<string>() == type &&
        entry["data"]?["id"]?.GetValue<string>() == id)
        .SelectMany(entry => Convert.FromBase64String(entry["data"]!["payload"]!["Base64"]!.GetValue<string>()))
        .ToArray();
    Check(Captured("relayRequestBody", patchId).SequenceEqual(bytes), "detailed relay HTTP request body is exact");
    Check(Captured("relayResponseBody", patchId).SequenceEqual(bytes), "detailed relay HTTP response body is exact");
    var socketId = entries.First(entry => entry["type"]?.GetValue<string>() == "relayRequestStart" &&
        entry["data"]?["rawTarget"]?.GetValue<string>() == "/socket")["data"]!["id"]!.GetValue<string>();
    Check(entries.Where(entry => entry["type"]?.GetValue<string>() == "relayWebSocketFrame" &&
        entry["data"]?["id"]?.GetValue<string>() == socketId &&
        entry["data"]?["direction"]?.GetValue<string>() == "clientToBackend")
        .SelectMany(entry => Convert.FromBase64String(entry["data"]!["payload"]!["Base64"]!.GetValue<string>()))
        .SequenceEqual(bytes), "detailed relay WebSocket client frames are exact");
    Check(entries.Where(entry => entry["type"]?.GetValue<string>() == "relayWebSocketFrame" &&
        entry["data"]?["id"]?.GetValue<string>() == socketId &&
        entry["data"]?["direction"]?.GetValue<string>() == "backendToClient")
        .SelectMany(entry => Convert.FromBase64String(entry["data"]!["payload"]!["Base64"]!.GetValue<string>()))
        .SequenceEqual(bytes), "detailed relay WebSocket backend frames are exact");
    relay.DetailedLogsEnabled = false;
    var count = ReadDetailedLines().Length;
    using (var quiet = await client.GetAsync($"http://127.0.0.1:{relayPort}/echo"))
        Check((int)quiet.StatusCode == 201, "relay continues when detailed logging is off");
    Check(ReadDetailedLines().Length == count, "detailed relay logging stops live");
    relay.DetailedLogsEnabled = true;
    using (var resumed = await client.GetAsync($"http://127.0.0.1:{relayPort}/echo"))
        Check((int)resumed.StatusCode == 201, "relay continues when detailed logging resumes");
    Check(ReadDetailedLines().Length > count, "detailed relay logging resumes live");
    using var active = new ClientWebSocket();
    active.Options.AddSubProtocol("relay-test");
    await active.ConnectAsync(new Uri($"ws://127.0.0.1:{relayPort}/socket"), CancellationToken.None);
    await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    var danceLink = JsonNode.Parse(File.ReadAllText(Path.Combine(danceDirectory, EmoteBridge.LinkFile)));
    Check(danceLink?["state"]?.GetValue<string>() == "stopped",
        "relay runs the dance sync while started and stops it on shutdown");
    relay = new RelayService { DanceDataLocator = () => null };
    await relay.StartAsync(player, relayPort, tlsPort, certDirectory);
    Check(true, "stop with active socket and restart same ports");
    await upstream.StopAsync();
    using var unavailable = await client.GetAsync($"http://127.0.0.1:{relayPort}/echo");
    Check((int)unavailable.StatusCode == 502, "unavailable backend returns 502");
}
finally { await relay.DisposeAsync(); }
Console.WriteLine("All relay transport checks passed.");


if (args.Length == 2 && args[0] == "--backend")
{
    var relayRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
    var backendDll = Path.GetFullPath(args[1], relayRoot);
    var backendRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(backendDll)!, "../../.."));
    var realPort = Port();
    var info = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = backendRoot,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    info.ArgumentList.Add(backendDll);
    info.Environment["Relay__Enabled"] = "true";
    info.Environment["Steam__Mode"] = "fallback";
    info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{realPort}";
    info.Environment["Relay__DatabasePath"] = Path.Combine(certDirectory, "real-accounts.db");
    info.Environment["Capture__LogDirectory"] = Path.Combine(certDirectory, "real-capture");
    using var process = System.Diagnostics.Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try
    {
        using var http = new HttpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            try { using var ready = await http.GetAsync($"http://127.0.0.1:{realPort}/ready", deadline.Token); break; }
            catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
        }
        var found = new List<string>();
        foreach (var secret in new[] { "first secret", "second secret", "first secret" })
        {
            var localPort = Port();
            await using var realRelay = new RelayService { DanceDataLocator = () => null };
            await realRelay.StartAsync(new RelaySettings
            {
                Username = "Same name", SecretKey = secret, BackendAddress = $"http://127.0.0.1:{realPort}"
            }, localPort, Port(), certDirectory);
            using var signed = await http.PostAsync($"http://127.0.0.1:{localPort}/v1/steam-steam/sign/RVS-B-WW", null);
            signed.EnsureSuccessStatusCode();
            using var tokenJson = System.Text.Json.JsonDocument.Parse(await signed.Content.ReadAsStringAsync());
            using var verify = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{localPort}/v1/verify/test");
            verify.Headers.Authorization = new("Bearer", tokenJson.RootElement.GetProperty("rebe_token").GetString());
            using var verified = await http.SendAsync(verify);
            verified.EnsureSuccessStatusCode();
            using var identityJson = System.Text.Json.JsonDocument.Parse(await verified.Content.ReadAsStringAsync());
            found.Add(identityJson.RootElement.GetProperty("id_token").GetProperty("sub").GetString()!);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{localPort}/"), deadline.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"command\":\"session_assign\"}"), WebSocketMessageType.Text, true, deadline.Token);
            var ack = new byte[1024];
            var receivedAck = await socket.ReceiveAsync(new ArraySegment<byte>(ack), deadline.Token);
            Check(Encoding.UTF8.GetString(ack, 0, receivedAck.Count).Contains("CMD_RESPONSE"), "real backend authenticated WebSocket ACK");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        }
        Check(found[0] != found[1] && found[0] == found[2], "real relay → backend match-or-create account identity");
    }
    finally
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
    }
}
