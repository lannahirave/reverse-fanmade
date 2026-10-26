using Microsoft.AspNetCore.Http.Features;
using ReVerse.Traffic;

namespace ReVerse.Capture.Middleware;

public sealed class DetailedTrafficMiddleware(RequestDelegate next, DetailedTrafficLog log)
{
    public const string ContextKey = "detailedTrafficId";

    public async Task Invoke(HttpContext context)
    {
        if (!log.IsEnabled)
        {
            await next(context);
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        context.Items[ContextKey] = id;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
        var remotePort = context.Connection.RemotePort;
        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        log.Write("httpRequestStart", new
        {
            id,
            method = context.Request.Method,
            url = $"{context.Request.Scheme}://{context.Request.Host}{rawTarget}",
            rawTarget,
            protocol = context.Request.Protocol,
            remoteAddress,
            remotePort,
            localAddress = context.Connection.LocalIpAddress?.ToString(),
            localPort = context.Connection.LocalPort,
            headers = context.Request.Headers.ToDictionary(p => p.Key, p =>
                p.Key.Equals(Configuration.AccessOptions.HeaderName, StringComparison.OrdinalIgnoreCase) ? ["[REDACTED]"] : p.Value.ToArray())
        });

        var requestBody = context.Request.Body;
        var responseBody = context.Response.Body;
        if (!isWebSocket)
        {
            context.Request.Body = new TrafficTapStream(requestBody, onRead: bytes =>
                log.Write("httpRequestBody", new { id, payload = DetailedTrafficLog.Payload(bytes.Span) }));
            context.Response.Body = new TrafficTapStream(responseBody, onWrite: bytes =>
                log.Write("httpResponseBody", new { id, payload = DetailedTrafficLog.Payload(bytes.Span) }));
        }

        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            log.Write("httpError", new { id, error = exception.ToString() });
            throw;
        }
        finally
        {
            if (!isWebSocket)
            {
                context.Request.Body = requestBody;
                context.Response.Body = responseBody;
            }
            log.Write("httpResponseEnd", new
            {
                id,
                statusCode = context.Response.StatusCode,
                headers = context.Response.Headers.ToDictionary(p => p.Key, p => p.Value.ToArray()),
                remoteAddress,
                remotePort
            });
        }
    }
}
