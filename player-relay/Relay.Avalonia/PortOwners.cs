using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReVerse.Relay.Desktop;

public sealed record PortOwner(int ProcessId, string ProcessName);

public static class PortOwners
{
    private const int AddressFamilyIPv4 = 2;
    private const int AddressFamilyIPv6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public static IReadOnlyList<PortOwner> FindListeners(params int[] ports)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var ownPid = Environment.ProcessId;
        var processIds = new HashSet<int>();
        processIds.UnionWith(ListenerProcessIds(AddressFamilyIPv4, 24, 8, 20, ports));
        processIds.UnionWith(ListenerProcessIds(AddressFamilyIPv6, 56, 20, 52, ports));
        processIds.Remove(ownPid);
        var owners = new List<PortOwner>();
        foreach (var processId in processIds.Order())
            owners.Add(new PortOwner(processId, GetProcessName(processId)));
        return owners;
    }

    public static async Task<bool> CloseAsync(PortOwner owner, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(owner.ProcessId);
        }
        catch (ArgumentException)
        {
            return true;
        }
        using var _ = process;
        try
        {
            if (!string.Equals(process.ProcessName, owner.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static IEnumerable<int> ListenerProcessIds(int addressFamily, int rowSize, int portOffset, int pidOffset, int[] ports)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer && result != 0)
            return [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
                if (result == ErrorInsufficientBuffer)
                    continue;
                if (result != 0)
                    return [];
                var found = new List<int>();
                var count = Marshal.ReadInt32(buffer);
                for (var index = 0; index < count; index++)
                {
                    var row = buffer + 4 + index * rowSize;
                    var rawPort = (uint)Marshal.ReadInt32(row + portOffset);
                    var port = (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
                    if (ports.Contains(port))
                        found.Add(Marshal.ReadInt32(row + pidOffset));
                }
                return found;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);
}
