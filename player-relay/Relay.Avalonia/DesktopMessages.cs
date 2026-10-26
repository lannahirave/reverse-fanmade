namespace ReVerse.Relay.Desktop;

public enum DesktopErrorCode
{
    DetectionTimedOut,
    IpconfigCouldNotStart,
    IpconfigFailed,
    NoZeroTierAddress,
    InvalidPlayerCount,
    InvalidFillWait,
    InvalidZeroTierAddress,
    BackendFolderDoesNotExist,
    BackendExecutableMissing,
    BackendCommandRequired,
    BackendAlreadyRunning,
    BackendProcessCouldNotStart,
    BackendExecutablePermission,
    BackendUrlRequired,
    PortOwnerCouldNotClose,
    InvalidServerApiKey
}

public sealed class DesktopException : Exception
{
    public DesktopErrorCode Code { get; }
    public string? Detail { get; }
    public string? EntryName { get; }

    public DesktopException(
        DesktopErrorCode code,
        string? detail = null,
        string? entryName = null,
        Exception? innerException = null)
        : base(code.ToString(), innerException)
    {
        Code = code;
        Detail = detail;
        EntryName = entryName;
    }
}

public sealed class DesktopArgumentOutOfRangeException : ArgumentOutOfRangeException
{
    public DesktopErrorCode Code { get; }

    public DesktopArgumentOutOfRangeException(DesktopErrorCode code, string parameterName)
        : base(parameterName, code.ToString())
    {
        Code = code;
    }
}
