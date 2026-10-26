using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReVerse.Relay.Desktop;

public static partial class ZeroTierHost
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(5);
    public static string BackendEntryName => OperatingSystem.IsWindows() ? "ReVerse.Capture.exe" : "ReVerse.Capture";

    public static async Task<string?> DetectAddressAsync(CancellationToken cancellationToken = default)
    {
        return OperatingSystem.IsWindows()
            ? await DetectWindowsAddressAsync(cancellationToken)
            : await DetectNetworkInterfaceAddressAsync(cancellationToken);
    }

    private static async Task<string?> DetectWindowsAddressAsync(CancellationToken cancellationToken)
    {

        var executable = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        using var timeoutCts = new CancellationTokenSource(DetectionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        var started = false;

        try
        {
            if (!process.Start())
                throw new DesktopException(DesktopErrorCode.IpconfigCouldNotStart);
            started = true;

            outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new DesktopException(DesktopErrorCode.IpconfigFailed, detail: error.Trim());
            return ParseAddress(output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DesktopException(DesktopErrorCode.DetectionTimedOut);
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            if (outputTask is not null || errorTask is not null)
            {
                try
                {
                    await Task.WhenAll(outputTask ?? Task.FromResult(string.Empty), errorTask ?? Task.FromResult(string.Empty));
                }
                catch (Exception) when (outputTask?.IsCanceled == true || errorTask?.IsCanceled == true)
                {
                }
            }
        }
    }

    private static async Task<string?> DetectNetworkInterfaceAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(DetectNetworkInterfaceAddress, CancellationToken.None)
                .WaitAsync(DetectionTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new DesktopException(DesktopErrorCode.DetectionTimedOut);
        }
    }

    private static string? DetectNetworkInterfaceAddress()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || !IsZeroTierInterface(networkInterface))
                continue;

            try
            {
                foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var value = address.Address;
                    if (value.AddressFamily == AddressFamily.InterNetwork && IsUsableIpv4(value.ToString()))
                        return value.ToString();
                }
            }
            catch (NetworkInformationException)
            {
            }
        }
        return null;
    }

    private static bool IsZeroTierInterface(NetworkInterface networkInterface)
    {
        return networkInterface.Name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
               networkInterface.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
               networkInterface.Name.StartsWith("zt", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ParseAddress(string ipconfigOutput)
    {
        var zeroTierAdapter = false;
        foreach (var rawLine in ipconfigOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd();
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                zeroTierAdapter = line.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!zeroTierAdapter || !line.Contains("IPv4", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = Ipv4Pattern().Match(line);
            if (match.Success && IsUsableIpv4(match.Value))
                return match.Value;
        }
        return null;
    }

    public const int MaxFillWaitSeconds = 100;

    public static string BuildCommand(string backendFolder, string address, int players, bool udpLoggingAllowed = false, int fillWaitSeconds = 0,
        string serverApiKey = "")
    {
        var folder = Path.GetFullPath(backendFolder.Trim().Trim('"'));
        var ip = ValidateAddress(address);
        var apiKey = ValidateApiKey(serverApiKey);
        if (players is < 2 or > 10)
            throw new DesktopArgumentOutOfRangeException(DesktopErrorCode.InvalidPlayerCount, nameof(players));
        if (fillWaitSeconds is < 0 or > MaxFillWaitSeconds)
            throw new DesktopArgumentOutOfRangeException(DesktopErrorCode.InvalidFillWait, nameof(fillWaitSeconds));
        var settings = new[]
        {
            "Relay__Enabled=true",
            "Steam__Mode=fallback",
            "Matchmaking__ExperimentalSessionProtocol=true",
            $"Matchmaking__Rulesets__match_master={players}",
            $"Matchmaking__PartialMatchWaitSeconds={fillWaitSeconds}",
            "Matchmaking__IgnorePlayerAttributes=true",
            $"ASPNETCORE_URLS={BuildBackendUrl(ip)}",
            "Signaling__Enabled=true",
            $"UdpLogging__Enabled={udpLoggingAllowed.ToString().ToLowerInvariant()}",
            "Signaling__PeerDiagnostics=true",
            "Signaling__BindAddress=0.0.0.0",
            $"Signaling__PublicHost={ip}",
            "Signaling__Port=5070",
            "Signaling__PublicPort=5070",
            "Signaling__ExperimentalReplies=true",
            "Signaling__Reply19=1",
            "Signaling__Reply21=1",
            "Signaling__RegistrationFieldBIsPeerNumber=true"
        };
        if (apiKey.Length > 0)
            settings = [.. settings, $"Access__ApiKey={apiKey}"];
        var controlSetting = $"Capture__DetailedLogsControlFile={RelaySettings.DetailedLogsControlFile}";
        return OperatingSystem.IsWindows()
            ? string.Join(" && ", settings.Append(controlSetting).Select(setting => $"set \"{setting}\"")) +
              $" && \"{Path.Combine(folder, BackendEntryName)}\""
            : string.Join(' ',
            [
                "env",
                .. settings,
                $"'{controlSetting.Replace("'", "'\\''", StringComparison.Ordinal)}'",
                $"./{BackendEntryName}"
            ]);
    }

    public static string GetBackendEntryPath(string backendFolder) =>
        Path.Combine(Path.GetFullPath(backendFolder.Trim().Trim('"')), BackendEntryName);

    public static string GenerateApiKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string ValidateApiKey(string? apiKey)
    {
        var value = apiKey?.Trim() ?? "";
        if (value.Length > 0 && !ApiKeyPattern().IsMatch(value))
            throw new DesktopException(DesktopErrorCode.InvalidServerApiKey);
        return value;
    }

    public static string BuildBackendUrl(string address) => $"http://{ValidateAddress(address)}:6080";

    public static bool IsDetectedAddress(string? currentAddress, string? detectedAddress) =>
        detectedAddress is not null && string.Equals(currentAddress?.Trim(), detectedAddress, StringComparison.Ordinal);

    private static string ValidateAddress(string address)
    {
        var value = address.Trim();
        if (!IsUsableIpv4(value))
            throw new DesktopException(DesktopErrorCode.InvalidZeroTierAddress);
        return value;
    }

    private static bool IsUsableIpv4(string value)
    {
        return IPAddress.TryParse(value, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork &&
               !IPAddress.IsLoopback(address) &&
               !value.StartsWith("169.254.", StringComparison.Ordinal) &&
               value != "0.0.0.0";
    }

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{16,128}$")]
    private static partial Regex ApiKeyPattern();
}

internal sealed class BackendHostController : IAsyncDisposable
{
    private readonly object gate = new();
    private Process? process;

    public event Action<bool>? RunningChanged;

    public bool IsRunning
    {
        get
        {
            lock (gate)
                return process is { HasExited: false };
        }
    }

    public void Start(string backendFolder, string command)
    {
        var folder = Path.GetFullPath(backendFolder.Trim().Trim('"'));
        var launcher = ZeroTierHost.GetBackendEntryPath(folder);
        if (!Directory.Exists(folder))
            throw new DesktopException(DesktopErrorCode.BackendFolderDoesNotExist);
        if (!File.Exists(launcher))
            throw new DesktopException(DesktopErrorCode.BackendExecutableMissing, entryName: ZeroTierHost.BackendEntryName);
        command = command.Trim();
        if (command.Length == 0)
            throw new DesktopException(DesktopErrorCode.BackendCommandRequired);
        if (OperatingSystem.IsLinux())
            EnsureExecutable(launcher);
        lock (gate)
        {
            if (process is { HasExited: false })
                throw new DesktopException(DesktopErrorCode.BackendAlreadyRunning);
            process?.Dispose();
            process = Process.Start(CreateStartInfo(folder, command)) ??
                throw new DesktopException(DesktopErrorCode.BackendProcessCouldNotStart);
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
        }
        RunningChanged?.Invoke(true);
    }

    private static ProcessStartInfo CreateStartInfo(string folder, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandInterpreter))
                commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return new ProcessStartInfo
            {
                FileName = commandInterpreter,
                Arguments = $"/k \"{command}\"",
                WorkingDirectory = folder,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !Path.IsPathRooted(shell) || !File.Exists(shell))
            shell = "/bin/sh";
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    [SupportedOSPlatform("linux")]
    private static void EnsureExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
        catch (IOException exception)
        {
            throw new DesktopException(DesktopErrorCode.BackendExecutablePermission, innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DesktopException(DesktopErrorCode.BackendExecutablePermission, innerException: exception);
        }
    }

    public async Task StopAsync()
    {
        Process? old;
        lock (gate)
        {
            old = process;
            process = null;
            if (old is not null)
                old.Exited -= OnExited;
        }
        if (old is null)
            return;
        try
        {
            if (!old.HasExited)
            {
                old.Kill(true);
                await old.WaitForExitAsync();
            }
        }
        finally
        {
            old.Dispose();
            RunningChanged?.Invoke(false);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (gate)
        {
            if (!ReferenceEquals(process, sender))
                return;
            process?.Dispose();
            process = null;
        }
        RunningChanged?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
