using ReVerse.Relay;
using ReVerse.Relay.Desktop;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--listen")
{
    var held = new TcpListener(IPAddress.Loopback, int.Parse(args[1]));
    held.Start();
    Console.WriteLine("LISTENING");
    await Task.Delay(Timeout.Infinite);
}

static void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

const string sample = """
Windows IP Configuration

Ethernet adapter Ethernet:

   IPv4 Address. . . . . . . . . . . : 192.168.1.20

Ethernet adapter ZeroTier One [154a350c8623f717]:

   Link-local IPv6 Address . . . . . : fe80::6728:2a3c:b3e3:57ae%68
   IPv4 Address. . . . . . . . . . . : 10.205.138.26
   Subnet Mask . . . . . . . . . . . : 255.255.255.0

Ethernet adapter Another Adapter:

   IPv4 Address. . . . . . . . . . . : 172.26.112.1
""";

Check(ZeroTierHost.ParseAddress(sample) == "10.205.138.26", "ZeroTier IPv4 parsed from ipconfig");
Check(ZeroTierHost.ParseAddress(sample.Replace("ZeroTier", "Overlay")) is null, "non-ZeroTier adapter ignored");
Check(ZeroTierHost.BuildBackendUrl("10.205.138.26") == "http://10.205.138.26:6080", "player backend URL generated");
Check(ZeroTierHost.IsDetectedAddress("10.205.138.26", "10.205.138.26"), "detected address status survives its text event");
Check(!ZeroTierHost.IsDetectedAddress("", "10.205.138.26"), "manual address clearing hides detected status");

var folder = Path.Combine(Path.GetTempPath(), "Backend Folder");
var command = ZeroTierHost.BuildCommand(folder, "10.205.138.26", 4);
Check(command.Contains("UdpLogging__Enabled=false", StringComparison.Ordinal), "default launch explicitly disables UDP logging");
Check(ZeroTierHost.BuildCommand(folder, "10.205.138.26", 4, true).Contains("UdpLogging__Enabled=true", StringComparison.Ordinal), "allowed UDP logging reaches backend launch command");
Check(!JsonSerializer.Deserialize<RelaySettings>("{}")!.BackendUdpLoggingAllowed, "existing settings do not imply UDP logging permission");
var saved = JsonSerializer.Serialize(new RelaySettings { BackendUdpLoggingAllowed = true });
Check(JsonSerializer.Deserialize<RelaySettings>(saved)!.BackendUdpLoggingAllowed, "UDP logging permission survives settings serialization");
foreach (var language in new TranslationService().AvailableLanguages)
    Check(!string.IsNullOrWhiteSpace(language.UdpLoggingOnButton) &&
          !string.IsNullOrWhiteSpace(language.UdpLoggingOffButton) &&
          !language.UdpLoggingOnButton.Contains('?') &&
          !language.UdpLoggingOffButton.Contains('?') &&
          !language.UdpLoggingDescription.Contains('?'), "UDP controls translated: " + language.Code);
foreach (var language in new TranslationService().AvailableLanguages)
    Check(!string.IsNullOrWhiteSpace(language.StartGameButton) &&
          !string.IsNullOrWhiteSpace(language.StartGameTooltip) &&
          language.GetDesktopErrorText(DesktopErrorCode.GameCouldNotStart, null, null) == language.ErrorGameCouldNotStart &&
          !string.IsNullOrWhiteSpace(language.ErrorGameCouldNotStart), "start game control translated: " + language.Code);
if (OperatingSystem.IsWindows())
{
    var launcher = Path.Combine(Path.GetFullPath(folder), "ReVerse.Capture.exe");
    Check(command.StartsWith("set \"Relay__Enabled=true\" && set \"Steam__Mode=fallback\"", StringComparison.Ordinal), "Windows backend environment generated");
    Check(command.Contains("set \"Matchmaking__Rulesets__match_master=4\"", StringComparison.Ordinal), "Windows match size included");
    Check(command.Contains($"set \"Capture__DetailedLogsControlFile={RelaySettings.DetailedLogsControlFile}\"", StringComparison.Ordinal), "Windows detailed log switch included");
    Check(command.EndsWith($"\"{launcher}\"", StringComparison.Ordinal), "Windows backend executable included and quoted");
}
else if (OperatingSystem.IsLinux())
{
    Check(command.StartsWith("env Relay__Enabled=true Steam__Mode=fallback ", StringComparison.Ordinal), "Linux backend command generated with environment");
    Check(command.Contains("Matchmaking__Rulesets__match_master=4", StringComparison.Ordinal), "Linux match size included");
    Check(command.Contains("Capture__DetailedLogsControlFile=", StringComparison.Ordinal), "Linux detailed log switch included");
    Check(command.EndsWith("./ReVerse.Capture", StringComparison.Ordinal), "Linux backend executable included");
}

try
{
    ZeroTierHost.BuildCommand(folder, "10.205.138.26", 1);
    Check(false, "match size lower bound enforced");
}
catch (ArgumentOutOfRangeException)
{
    Check(true, "match size lower bound enforced");
}

try
{
    ZeroTierHost.BuildCommand(folder, "10.205.138.26", 11);
    Check(false, "match size upper bound enforced");
}
catch (ArgumentOutOfRangeException)
{
    Check(true, "match size upper bound enforced");
}

Check(command.Contains("Matchmaking__PartialMatchWaitSeconds=0", StringComparison.Ordinal), "default launch keeps exact match size");
Check(ZeroTierHost.BuildCommand(folder, "10.205.138.26", 6, false, 15).Contains("Matchmaking__PartialMatchWaitSeconds=15", StringComparison.Ordinal), "fill wait reaches backend launch command");
Check(JsonSerializer.Deserialize<RelaySettings>("{}")!.BackendFillWaitSeconds == 15, "fill wait defaults to 15 seconds");
Check(JsonSerializer.Deserialize<RelaySettings>(JsonSerializer.Serialize(new RelaySettings { BackendFillWaitSeconds = 0 }))!.BackendFillWaitSeconds == 0, "fill wait survives settings serialization");
foreach (var wait in new[] { -1, ZeroTierHost.MaxFillWaitSeconds + 1 })
{
    try
    {
        ZeroTierHost.BuildCommand(folder, "10.205.138.26", 6, false, wait);
        Check(false, "fill wait bounds enforced: " + wait);
    }
    catch (ArgumentOutOfRangeException)
    {
        Check(true, "fill wait bounds enforced: " + wait);
    }
}
foreach (var language in new TranslationService().AvailableLanguages)
    Check(!string.IsNullOrWhiteSpace(language.FillWaitLabel) &&
          !string.IsNullOrWhiteSpace(language.FillWaitTooltip) &&
          language.GetDesktopErrorText(DesktopErrorCode.InvalidFillWait, null, null) == language.ErrorInvalidFillWait &&
          !language.ErrorInvalidFillWait.Contains('?'),
          "fill wait translated: " + language.Code);

Check(!command.Contains("Access__ApiKey", StringComparison.Ordinal), "default launch sets no server API key");
var generatedKey = ZeroTierHost.GenerateApiKey();
Check(generatedKey.Length == 32 && generatedKey.All(Uri.IsHexDigit) && generatedKey != ZeroTierHost.GenerateApiKey(), "generated server API keys are random 32-character keys");
var keyed = ZeroTierHost.BuildCommand(folder, "10.205.138.26", 4, false, 0, "  " + generatedKey + "  ");
Check(OperatingSystem.IsWindows()
        ? keyed.Contains($"set \"Access__ApiKey={generatedKey}\"", StringComparison.Ordinal)
        : keyed.Contains($" Access__ApiKey={generatedKey} ", StringComparison.Ordinal),
    "server API key reaches backend launch command trimmed");
foreach (var badKey in new[] { "short", new string('a', 129), "0123456789abcdef\"&calc", "0123456789abcdef %PATH%", "0123456789abcdef $(id)" })
{
    try
    {
        ZeroTierHost.BuildCommand(folder, "10.205.138.26", 4, false, 0, badKey);
        Check(false, "unsafe or invalid server API key refused: " + badKey.Length);
    }
    catch (DesktopException exception) when (exception.Code == DesktopErrorCode.InvalidServerApiKey)
    {
        Check(true, "unsafe or invalid server API key refused: " + badKey.Length);
    }
}
foreach (var language in new TranslationService().AvailableLanguages)
    Check(new[] { language.ServerApiKeyLabel, language.ServerApiKeyPlaceholder, language.ServerApiKeyTooltip,
              language.HostApiKeyPlaceholder, language.HostApiKeyTooltip, language.GenerateButton,
              language.GenerateApiKeyTooltip, language.CopyApiKeyTooltip }.All(value => !string.IsNullOrWhiteSpace(value)) &&
          language.GetRelayErrorText(RelayErrorCode.InvalidServerApiKey, null) == language.ErrorInvalidServerApiKey &&
          language.GetRelayErrorText(RelayErrorCode.ServerApiKeyRejected, 401) == language.ErrorServerApiKeyRejected &&
          language.GetRelayErrorText(RelayErrorCode.ServerUnreachable, null) == language.ErrorServerUnreachable &&
          language.GetDesktopErrorText(DesktopErrorCode.InvalidServerApiKey, null, null) == language.ErrorInvalidHostApiKey,
          "server API key translated: " + language.Code);

if (OperatingSystem.IsWindows())
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    var holderStart = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        holderStart.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    holderStart.ArgumentList.Add("--listen");
    holderStart.ArgumentList.Add(port.ToString());
    using var holder = Process.Start(holderStart)!;
    try
    {
        Check(holder.StandardOutput.ReadLine() == "LISTENING", "port holder started");
        var owners = PortOwners.FindListeners(port);
        Check(owners.Count == 1 && owners[0].ProcessId == holder.Id, "port owner process found");
        Check(PortOwners.FindListeners(port).All(owner => owner.ProcessId != Environment.ProcessId), "own process is never listed as port owner");
        Check(await PortOwners.CloseAsync(owners[0], TimeSpan.FromSeconds(5)), "port owner closed");
        Check(PortOwners.FindListeners(port).Count == 0, "port released after closing owner");
        var reuse = new TcpListener(IPAddress.Loopback, port);
        reuse.Start();
        reuse.Stop();
        Check(true, "port can be bound after closing owner");
    }
    finally
    {
        if (!holder.HasExited)
            holder.Kill();
    }
}

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
    Check(await ZeroTierHost.DetectAddressAsync() is not null, "live ZeroTier address detected");
