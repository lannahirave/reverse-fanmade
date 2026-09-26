using System.Globalization;
using ReVerse.Relay;

namespace ReVerse.Relay.Desktop;

public abstract class TranslationBase : ITranslation
{
    public abstract string Code { get; }
    public abstract string LanguageName { get; }
    public abstract string WindowTitle { get; }
    public abstract string BrandPrefix { get; }
    public abstract string BrandAccent { get; }
    public abstract string BrandSuffix { get; }
    public abstract string Tagline { get; }
    public abstract string LanguageSelectorTooltip { get; }
    public abstract string HostBackendButton { get; }
    public abstract string RelayOnlyButton { get; }
    public abstract string HostBackendTooltip { get; }
    public abstract string RelayOnlyTooltip { get; }
    public abstract string PlayerRelayTitle { get; }
    public abstract string PlayerRelayDescription { get; }
    public abstract string UsernameLabel { get; }
    public abstract string UsernamePlaceholder { get; }
    public abstract string BackendServerLabel { get; }
    public abstract string BackendServerPlaceholder { get; }
    public abstract string BackendServerTooltip { get; }
    public abstract string SteamLaunchOptionLabel { get; }
    public abstract string SteamLaunchOptionTooltip { get; }
    public abstract string CopyButton { get; }
    public abstract string CopiedButton { get; }
    public abstract string CopyLaunchOptionTooltip { get; }
    public abstract string StartRelayButton { get; }
    public abstract string StopRelayButton { get; }
    public abstract string StartRelayTooltip { get; }
    public abstract string StopRelayTooltip { get; }
    public virtual string DetailedLogsOnButton => "DETAILED LOGS: ON";
    public virtual string DetailedLogsOffButton => "DETAILED LOGS: OFF";
    public virtual string DetailedLogsDescription => "Records full URLs, headers, bodies, WebSocket messages and signaling packets, including credentials.";
    public virtual string DetailedLogsPathLabel => "Log folder";
    public abstract string StatusStopped { get; }
    public abstract string StatusSigningIn { get; }
    public abstract string StatusRunningWaitingForGame { get; }
    public abstract string StatusRunningConnectedToBackend { get; }
    public abstract string StatusBackendConnectionFailed { get; }
    public abstract string StatusSessionExpired { get; }
    public abstract string StatusStartFailed { get; }
    public abstract string RelayPorts { get; }
    public abstract string UdpLoggingAllowedLabel { get; }
    public abstract string UdpLoggingDescription { get; }
    public abstract string BackendHostTitle { get; }
    public abstract string BackendHostDescription { get; }
    public abstract string BackendFolderLabel { get; }
    public abstract string BackendFolderTooltip { get; }
    public abstract string BackendFolderPlaceholder { get; }
    public abstract string BrowseButton { get; }
    public abstract string BrowseBackendTooltip { get; }
    public abstract string ZeroTierAddressLabel { get; }
    public abstract string ZeroTierAddressTooltip { get; }
    public abstract string ZeroTierAddressPlaceholder { get; }
    public abstract string DetectButton { get; }
    public abstract string DetectAddressTooltip { get; }
    public abstract string PlayersLabel { get; }
    public abstract string PlayersTooltip { get; }
    public abstract string PlayerConnectionUrlLabel { get; }
    public abstract string PlayerConnectionUrlPlaceholder { get; }
    public abstract string CopyPlayerUrlTooltip { get; }
    public abstract string PlayerConnectionUrlDescription { get; }
    public abstract string AdvancedCommandHeader { get; }
    public abstract string StartBackendButton { get; }
    public abstract string StartBackendTooltip { get; }
    public abstract string StopBackendButton { get; }
    public abstract string StopBackendTooltip { get; }
    public abstract string BackendStoppedStatus { get; }
    public abstract string BackendRunningStatus { get; }
    public abstract string BackendStoppingStatus { get; }
    public abstract string BackendPorts { get; }
    public abstract string PleaseWait { get; }
    public abstract string SettingsLoadFailureStatus { get; }
    public abstract string DetectingZeroTierStatus { get; }
    public abstract string ZeroTierDetectedStatus { get; }
    public abstract string SelectBackendFolderDialogTitle { get; }
    public abstract string BackendFolderMissingFormat { get; }
    public abstract string RelayStartErrorSuffix { get; }
    public abstract string ErrorInvalidUsername { get; }
    public abstract string ErrorInvalidSecretKey { get; }
    public abstract string ErrorInvalidBackendUrl { get; }
    public abstract string ErrorSavedSettingsEmpty { get; }
    public abstract string ErrorRelayAlreadyRunning { get; }
    public abstract string ErrorBackendPointsToRelay { get; }
    public abstract string ErrorAccountSignInFailedFormat { get; }
    public abstract string ErrorBackendEmptyAccountResponse { get; }
    public abstract string ErrorBackendInvalidAccountResponse { get; }
    public abstract string ErrorDetectionTimedOut { get; }
    public abstract string ErrorIpconfigCouldNotStart { get; }
    public abstract string ErrorIpconfigFailed { get; }
    public abstract string ErrorIpconfigFailedFormat { get; }
    public abstract string ErrorNoZeroTierAddress { get; }
    public abstract string ErrorInvalidPlayerCount { get; }
    public abstract string ErrorInvalidZeroTierAddress { get; }
    public abstract string ErrorBackendFolderDoesNotExist { get; }
    public abstract string ErrorBackendExecutableMissingFormat { get; }
    public abstract string ErrorBackendCommandRequired { get; }
    public abstract string ErrorBackendAlreadyRunning { get; }
    public abstract string ErrorBackendProcessCouldNotStart { get; }
    public abstract string ErrorBackendExecutablePermission { get; }
    public abstract string ErrorBackendUrlRequired { get; }

    public string GetRelayStatusText(RelayStatus status) => status switch
    {
        RelayStatus.SigningIn => StatusSigningIn,
        RelayStatus.RunningWaitingForGame => StatusRunningWaitingForGame,
        RelayStatus.RunningConnectedToBackend => StatusRunningConnectedToBackend,
        RelayStatus.BackendConnectionFailed => StatusBackendConnectionFailed,
        RelayStatus.SessionExpired => StatusSessionExpired,
        RelayStatus.StartFailed => StatusStartFailed,
        RelayStatus.SettingsLoadFailed => SettingsLoadFailureStatus,
        _ => StatusStopped
    };

    public string GetRelayErrorText(RelayErrorCode code, int? statusCode) => code switch
    {
        RelayErrorCode.InvalidUsername => ErrorInvalidUsername,
        RelayErrorCode.InvalidSecretKey => ErrorInvalidSecretKey,
        RelayErrorCode.InvalidBackendUrl => ErrorInvalidBackendUrl,
        RelayErrorCode.SavedSettingsEmpty => ErrorSavedSettingsEmpty,
        RelayErrorCode.RelayAlreadyRunning => ErrorRelayAlreadyRunning,
        RelayErrorCode.BackendPointsToRelay => ErrorBackendPointsToRelay,
        RelayErrorCode.AccountSignInFailed => string.Format(CultureInfo.InvariantCulture, ErrorAccountSignInFailedFormat, statusCode ?? 0),
        RelayErrorCode.BackendEmptyAccountResponse => ErrorBackendEmptyAccountResponse,
        RelayErrorCode.BackendInvalidAccountResponse => ErrorBackendInvalidAccountResponse,
        _ => code.ToString()
    };

    public string GetDesktopErrorText(DesktopErrorCode code, string? detail, string? entryName) => code switch
    {
        DesktopErrorCode.DetectionTimedOut => ErrorDetectionTimedOut,
        DesktopErrorCode.IpconfigCouldNotStart => ErrorIpconfigCouldNotStart,
        DesktopErrorCode.IpconfigFailed => string.IsNullOrWhiteSpace(detail)
            ? ErrorIpconfigFailed
            : string.Format(CultureInfo.InvariantCulture, ErrorIpconfigFailedFormat, detail),
        DesktopErrorCode.NoZeroTierAddress => ErrorNoZeroTierAddress,
        DesktopErrorCode.InvalidPlayerCount => ErrorInvalidPlayerCount,
        DesktopErrorCode.InvalidZeroTierAddress => ErrorInvalidZeroTierAddress,
        DesktopErrorCode.BackendFolderDoesNotExist => ErrorBackendFolderDoesNotExist,
        DesktopErrorCode.BackendExecutableMissing => string.Format(CultureInfo.InvariantCulture, ErrorBackendExecutableMissingFormat, entryName ?? "ReVerse.Capture"),
        DesktopErrorCode.BackendCommandRequired => ErrorBackendCommandRequired,
        DesktopErrorCode.BackendAlreadyRunning => ErrorBackendAlreadyRunning,
        DesktopErrorCode.BackendProcessCouldNotStart => ErrorBackendProcessCouldNotStart,
        DesktopErrorCode.BackendExecutablePermission => ErrorBackendExecutablePermission,
        DesktopErrorCode.BackendUrlRequired => ErrorBackendUrlRequired,
        _ => code.ToString()
    };

    public string GetBackendFolderMissingText(string entryName) =>
        string.Format(CultureInfo.InvariantCulture, BackendFolderMissingFormat, entryName);

    public string GetRelayStartErrorText(string details) =>
        string.IsNullOrWhiteSpace(details) ? RelayStartErrorSuffix : $"{details} {RelayStartErrorSuffix}";
}
