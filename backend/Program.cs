using Serilog;
using ReVerse.Capture.Logging;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using ReVerse.Capture.Capturing;
using ReVerse.Capture.Configuration;
using ReVerse.Capture.Middleware;
using ReVerse.Capture.Protocol;
using ReVerse.Capture.Persistence;
using ReVerse.Capture.Matchmaking;
using ReVerse.Capture.Signaling;
using ReVerse.Traffic;

var builder = WebApplication.CreateBuilder(args);
var logDirectory = Path.GetFullPath(builder.Configuration["Capture:LogDirectory"] ?? "logs", builder.Environment.ContentRootPath);
Log.Logger = BackendLogging.Create(logDirectory);
builder.Logging.ClearProviders();
builder.Services.AddSerilog();
try
{
    Log.ForContext("UserFacing", true).Information("Starting the backend...");


    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);



    var urlsConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["urls"]);
    var kestrelConfigured = builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();
    if (!urlsConfigured && !kestrelConfigured)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 5080);
            options.Listen(IPAddress.Loopback, 5081, https => https.UseHttps(LocalCertificate.GetOrCreate()));
        });
    }

    var capture = builder.Configuration.GetSection("Capture").Get<CaptureOptions>() ?? new CaptureOptions();
    capture.Validate();
    var steam = builder.Configuration.GetSection("Steam").Get<SteamOptions>() ?? new SteamOptions();
    steam.Validate();
    var udpLogging = builder.Configuration.GetSection("UdpLogging").Get<UdpLogOptions>() ?? new();
    udpLogging.Validate();
    builder.Services.AddSingleton(_ => new UdpTrafficLog(logDirectory, udpLogging));
    builder.Services.AddSingleton(capture);
    builder.Services.AddSingleton(steam);
    builder.Services.AddSingleton(_ => new RequestLog(Path.GetFullPath(capture.LogDirectory, builder.Environment.ContentRootPath)));
    builder.Services.AddSingleton(_ => new DetailedTrafficLog(
        Path.GetFullPath(capture.LogDirectory, builder.Environment.ContentRootPath),
        () => File.Exists(Path.GetFullPath(capture.DetailedLogsControlFile, builder.Environment.ContentRootPath))));
    builder.Services.AddSingleton(_ =>
    {
        var overrides = new ResponseOverrides(
            Path.Combine(builder.Environment.ContentRootPath, "responses.json"),
            BundledResources.Read("responses.json"));
        overrides.Load();
        return overrides;
    });
    builder.Services.AddSingleton(_ =>
    {
        var contract = new ContractOverrides(
            Path.Combine(builder.Environment.ContentRootPath, "contract.json"),
            BundledResources.Read("contract.json"));
        contract.Load();
        return contract;
    });
    builder.Services.AddSingleton<GameState>();
    var matchmaking = builder.Configuration.GetSection("Matchmaking").Get<MatchmakingOptions>() ?? new();
    matchmaking.Validate();
    builder.Services.AddSingleton(matchmaking);
    builder.Services.AddSingleton<NotificationHub>();
    builder.Services.AddSingleton<GameSessionRegistry>();
    var signaling = builder.Configuration.GetSection("Signaling").Get<SignalingOptions>() ?? new();
    signaling.Validate();
    builder.Services.AddSingleton(signaling);
    builder.Services.AddSingleton<SignalingDirectory>();
    builder.Services.AddSingleton<SignalingProvider>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<SignalingProvider>());
    builder.Services.AddSingleton<MatchmakingCoordinator>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<MatchmakingCoordinator>());
    builder.Services.AddSingleton<MatchmakingEndpoints>();
    builder.Services.AddSingleton(provider => new EmoteHub(provider.GetRequiredService<MatchmakingCoordinator>()));
    builder.Services.AddSingleton<RelayAccounts>();
    builder.Services.AddSingleton<SaveRepository>();
    builder.Services.AddSingleton<SteamClientIdentityProvider>();
    builder.Services.AddHttpClient<SteamIdentityResolver>((_, client) =>
    {
        client.Timeout = TimeSpan.FromSeconds(steam.RequestTimeoutSeconds);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ReVerse.Capture/1.0");
    });
    builder.Services.AddSingleton<DynamicRoutes>();
    builder.Services.AddSingleton<ProtocolMiddleware>();

    var app = builder.Build();
    app.Services.GetRequiredService<RequestLog>().Failed += exception =>
        Log.ForContext("UserFacing", true).Error(exception, "Cannot write request logs. Check the log folder and available disk space.");
    app.Services.GetRequiredService<DetailedTrafficLog>().Failed += exception =>
        Log.ForContext("UserFacing", true).Error(exception, "Cannot write detailed traffic logs. Check the log folder and available disk space.");
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Log.ForContext("UserFacing", true).Information("Backend ready. Listening on {Addresses:l}", string.Join(", ", app.Urls));
        Log.ForContext("UserFacing", true).Information("Log files are in {LogDirectory:l}. Backend and request files roll at 1 MB.", logDirectory);
        Log.ForContext("UserFacing", true).Information("UDP conversation logging is {UdpLogging:l}; readable and JSON files roll at 10 MB.", udpLogging.Enabled ? "on" : "off");
        Log.ForContext("UserFacing", true).Information("Full traffic recording is {Recording:l}. Ordinary diagnostic logs are always written.",
            app.Services.GetRequiredService<DetailedTrafficLog>().IsEnabled ? "on" : "off");
    });
    app.Lifetime.ApplicationStopping.Register(() =>
        Log.ForContext("UserFacing", true).Information("Stopping the backend and closing player connections..."));
    app.UseSerilogRequestLogging();
    app.UseWebSockets();
    app.UseMiddleware<DetailedTrafficMiddleware>();
    app.Use(async (context, next) =>
    {
        try
        {
            if (context.Request.Path == "/relay/account/login")
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!RelayIdentity.Enabled(context)) { context.Response.StatusCode = 404; return; }
                if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
                if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 8192;
                if (context.Request.ContentLength > 8192) throw new BadHttpRequestException("Login request too large.", 413);

                RelayLoginRequest? login;
                try { login = await context.Request.ReadFromJsonAsync<RelayLoginRequest>(context.RequestAborted); }
                catch (JsonException) { throw new BadHttpRequestException("Invalid login request.", 400); }
                var accounts = context.RequestServices.GetRequiredService<RelayAccounts>();
                var result = await Task.Run(() => accounts.Login(login?.Username, login?.SecretKey), context.RequestAborted);
                await context.Response.WriteAsJsonAsync(result);
                Log.ForContext("UserFacing", true).ForContext("AccountId", result.AccountId)
                    .Information("Player signed in{AccountStatus:l}.", result.Created ? " with a new account" : "");
                return;
            }
            _ = RelayIdentity.Read(context);
            if (MatchmakingEndpoints.Owns(context.Request.Path))
            {
                await context.RequestServices.GetRequiredService<MatchmakingEndpoints>().HandleAsync(context);
                return;
            }
            if (context.WebSockets.IsWebSocketRequest && RelayIdentity.Enabled(context))
            {
                var identity = MatchmakingEndpoints.Authenticate(context, context.RequestServices.GetRequiredService<GameState>());
                var coordinator = context.RequestServices.GetRequiredService<MatchmakingCoordinator>();
                await context.RequestServices.GetRequiredService<NotificationHub>().RunAsync(context, identity.AccountId,
                    () => coordinator.Disconnected(identity.AccountId));
                return;
            }
            if (EmoteHub.Owns(context.Request.Path))
            {
                await EmoteHub.Handle(context);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/relay/saves"))
            {

                await SaveEndpoints.Handle(context);
                return;
            }
            await next(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    });
    app.Run(app.Services.GetRequiredService<ProtocolMiddleware>().Invoke);

    app.Logger.LogInformation(
        "Capture backend writes JSONL to {LogDirectory:l}; body prefix limit {Limit} bytes; sensitive header redaction {Redaction}",
        app.Services.GetRequiredService<RequestLog>().DirectoryPath, capture.MaxCapturedBodyBytes, capture.RedactSensitiveHeaders);
    app.Logger.LogInformation(
        "Steam identity mode {Mode}; AppID {AppId}; Web API key configured {ApiKeyConfigured}; local client enabled {LocalClientEnabled}",
        steam.Mode, steam.AppId, steam.ApiKeyConfigured, steam.UseLocalClient);
    await app.RunAsync();
    Log.ForContext("UserFacing", true).Information("Backend stopped.");
}
catch (Exception exception)
{
    Log.ForContext("UserFacing", true).Fatal(exception, "The backend could not continue. Check the backend log for details.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

internal sealed record RelayLoginRequest(string? Username, string? SecretKey);
