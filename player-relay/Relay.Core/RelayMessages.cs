namespace ReVerse.Relay;

public enum RelayStatus
{
    Stopped,
    SigningIn,
    RunningWaitingForGame,
    RunningConnectedToBackend,
    BackendConnectionFailed,
    SessionExpired,
    StartFailed,
    SettingsLoadFailed
}

public enum RelayErrorCode
{
    InvalidUsername,
    InvalidSecretKey,
    InvalidBackendUrl,
    SavedSettingsEmpty,
    RelayAlreadyRunning,
    BackendPointsToRelay,
    AccountSignInFailed,
    BackendEmptyAccountResponse,
    BackendInvalidAccountResponse,
    InvalidServerApiKey,
    ServerApiKeyRejected,
    ServerUnreachable
}

public sealed class RelayException : Exception
{
    public RelayErrorCode Code { get; }
    public int? StatusCode { get; }

    public RelayException(RelayErrorCode code, int? statusCode = null, Exception? innerException = null)
        : base(code.ToString(), innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }
}
