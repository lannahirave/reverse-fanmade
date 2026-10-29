using System.Security.Cryptography;
using System.Text;
using ReVerse.Capture.Configuration;

namespace ReVerse.Capture.Middleware;

public sealed class AccessGate(RequestDelegate next, AccessOptions options)
{
    private readonly byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));

    public async Task Invoke(HttpContext context)
    {
        if (!options.Enabled || Authorized(context.Request.Headers[AccessOptions.HeaderName]))
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
    }

    private bool Authorized(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= AccessOptions.MaxKeyLength } provided) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), expected);
    }
}
