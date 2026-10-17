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
