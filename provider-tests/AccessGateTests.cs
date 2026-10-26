using Microsoft.AspNetCore.Http;
using ReVerse.Capture.Configuration;
using ReVerse.Capture.Middleware;

namespace Provider.Tests;

internal static class AccessGateTests
{
    private const string Key = "0123456789abcdef";

    private static (int Status, bool Passed) Send(AccessOptions options, params string[] headerValues)
    {
        var passed = false;
        var gate = new AccessGate(_ => { passed = true; return Task.CompletedTask; }, options);
        var context = new DefaultHttpContext();
        if (headerValues.Length > 0) context.Request.Headers[AccessOptions.HeaderName] = headerValues;
        gate.Invoke(context).GetAwaiter().GetResult();
        return (context.Response.StatusCode, passed);
    }

    internal static void Gate()
    {
        var open = new AccessOptions();
        Assert.That(Send(open) is (200, true), "empty key leaves the gate off");
        var options = new AccessOptions { ApiKey = Key };
        Assert.That(Send(options, Key) is (200, true), "correct key passes");
        Assert.That(Send(options) is (401, false), "missing key is refused");
        Assert.That(Send(options, "wrong") is (401, false), "wrong key is refused");
        Assert.That(Send(options, Key + "x") is (401, false), "key with a suffix is refused");
        Assert.That(Send(options, "") is (401, false), "empty key is refused");
        Assert.That(Send(options, Key, Key) is (401, false), "duplicate key headers are refused");
        Assert.That(Send(options, new string('a', 5000)) is (401, false), "oversized key is refused");
        for (var i = 0; i < 50; i++) Send(options, "wrong");
        Assert.That(Send(options, Key) is (200, true), "repeated wrong keys never lock out the correct key");
    }

    internal static void Options()
    {
        Assert.Throws<ArgumentException>(() => new AccessOptions { ApiKey = "short" }.Validate(), "short keys are refused");
        Assert.Throws<ArgumentException>(() => new AccessOptions { ApiKey = new string('a', 1025) }.Validate(), "oversized keys are refused");
        Assert.Throws<ArgumentException>(() => new AccessOptions { ApiKey = "0123456789abcde\n" + "f" }.Validate(), "control characters are refused");
        var ok = new AccessOptions { ApiKey = "  0123456789abcdef  " };
        ok.Validate();
        Assert.That(ok.ApiKey == Key && ok.Enabled, "key is trimmed and enables the gate");
        Assert.That(!new AccessOptions().Enabled, "empty key leaves the gate off");
    }
}
