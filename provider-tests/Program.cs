namespace Provider.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--udp-tests"]) { UdpLoggingTests.Run(); await NetworkTests.ProviderSmoke(); await NetworkTests.ProviderSmoke(failedUdpLog: true); return 0; }
        if (args is ["--logging-tests"]) { LoggingTests.Run(); NotificationLifetimeTests.Run(); return 0; }
        if (args is ["--http-probe"]) return await NetworkTests.HttpProbe();
        if (args is ["--emote-tests"])
        {
            EmoteHubTests.Sanitize(); EmoteHubTests.FanOut(); EmoteHubTests.LongPollWakes(); EmoteHubTests.Bounds();
            Console.WriteLine("PASS dance event hub");
            return 0;
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Run without arguments for the provider regression suite.");
            return 2;
        }
        (string Name, Action Run)[] tests =
        [
            ("detailed UDP files and bounded logging", UdpLoggingTests.Run),
            ("notification writer failure cleanup", NotificationLifetimeTests.Run),
            ("rolling JSON logging and failure isolation", LoggingTests.Run),
            ("explicit experimental options and resource bounds", DirectoryTests.Options),
            ("per-account/session credentials, copies and allocation idempotency", DirectoryTests.Credentials),
            ("membership and capacity bounds; removal releases capacity", DirectoryTests.Bounds),
            ("authenticated attachment, duplicate attachment and session revocation", DirectoryTests.AttachAndRevoke),
            ("active membership refresh preserves remaining routes and drops stale routes", DirectoryTests.MembershipRefresh),
            ("single-member reduction preserves the remaining signaling association", DirectoryTests.MembershipReductionToSingle),
            ("bounded reliable queue fails closed", DirectoryTests.QueueBounds),
            ("membership expansion preserves active peers and credentials", DirectoryTests.MembershipExpansion),
            ("startup gates and observing mode", ConversationTests.Startup),
            ("explicit negative controls emit zero without opening startup gates", ConversationTests.NegativeControls),
            ("all registration orders and fixed registration/readiness bytes", ConversationTests.RegistrationOrders),
            ("registration credential and framing rejection", ConversationTests.BadRegistration),
            ("nonce-pair forwarding, authenticated context/sender and opaque bytes", ConversationTests.Forwarding),
            ("malformed/foreign routes are rejected before any delivery", ConversationTests.BadForwarding),
            ("allowlisted tag/secondary diagnostics including rejected and truncated payloads", ConversationTests.PayloadDiagnostics),
            ("peer primary CRC, all control schemas and copy-only decoding", PeerDiagnosticsTests.Decode),
            ("peer ready/index stream diagnostics, bounds and privacy", PeerDiagnosticsTests.Streams),
            ("peer diagnostics opt-in preserves even malformed primary/secondary forwarding", PeerDiagnosticsTests.Forwarding),
            ("bounded peer diagnostics retain late milestones", PeerDiagnosticsTests.Sampling),
            ("recursive metadata redaction preserves its input", DirectoryTests.Redaction),
            ("dance events: field whitelist and rejection", EmoteHubTests.Sanitize),
            ("dance events: same-session fan-out excludes sender", EmoteHubTests.FanOut),
            ("dance events: long poll wakes on post, renews, times out and cancels", EmoteHubTests.LongPollWakes),
            ("dance events: rate limit and bounded mailbox", EmoteHubTests.Bounds),
        ];
        var failures = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error)
            {
                ++failures;
                Console.Error.WriteLine($"FAIL {test.Name}: {error}");
            }
        }
        try { await DirectoryTests.Deadline(); Console.WriteLine("PASS allocation retries cannot extend admission deadline"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL admission deadline: {error}"); }
        try { await NetworkTests.ProviderSmoke(); Console.WriteLine("PASS real provider: two DTLS clients, startup, registration, forwarding, revocation and logs"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL real provider smoke: {error}"); }
        try { await NetworkTests.MembershipReduction(); Console.WriteLine("PASS real provider single-member reduction preserves the remaining transport"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL real provider single-member reduction: {error}"); }
        try { await NetworkTests.ProviderSmoke(true); Console.WriteLine("PASS real provider copy-only peer diagnostics and persisted ready/CRC observations"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL real peer diagnostics: {error}"); }
        try { await NetworkTests.Lifecycle(); Console.WriteLine("PASS real provider disabled/bind-failure/start-stop lifecycle"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL real provider lifecycle: {error}"); }
        try { await NetworkTests.NegativeControls(); Console.WriteLine("PASS real DTLS negative controls send zero without gate 5 or readiness"); }
        catch (Exception error) { ++failures; Console.Error.WriteLine($"FAIL real negative controls: {error}"); }
        Console.WriteLine($"Provider contracts: {tests.Length + 6 - failures} passed, {failures} failed.");
        return failures == 0 ? 0 : 1;
    }
}
