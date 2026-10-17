using ReVerse.Relay;

namespace ReVerse.Relay.Desktop;

public interface ITranslation
{
    string Code { get; }
    string LanguageName { get; }
    string WindowTitle { get; }
    string BrandPrefix { get; }
    string BrandAccent { get; }
    string BrandSuffix { get; }
    string Tagline { get; }
    string LanguageSelectorTooltip { get; }
    string HostBackendButton { get; }
    string RelayOnlyButton { get; }
    string HostBackendTooltip { get; }
    string RelayOnlyTooltip { get; }
    string PlayerRelayTitle { get; }
    string PlayerRelayDescription { get; }
    string UsernameLabel { get; }
    string UsernamePlaceholder { get; }
    string BackendServerLabel { get; }
    string BackendServerPlaceholder { get; }
    string BackendServerTooltip { get; }
    string SteamLaunchOptionLabel { get; }
    string SteamLaunchOptionTooltip { get; }
    string CopyButton { get; }
    string CopiedButton { get; }
    string CopyLaunchOptionTooltip { get; }
    string StartRelayButton { get; }
    string StopRelayButton { get; }
    string StartRelayTooltip { get; }
    string StopRelayTooltip { get; }
    string DetailedLogsOnButton { get; }
    string DetailedLogsOffButton { get; }
    string DetailedLogsDescription { get; }
    string DetailedLogsPathLabel { get; }
    string StatusStopped { get; }
    string StatusSigningIn { get; }
    string StatusRunningWaitingForGame { get; }
    string StatusRunningConnectedToBackend { get; }
    string StatusBackendConnectionFailed { get; }
    string StatusSessionExpired { get; }
    string StatusStartFailed { get; }
    string RelayPorts { get; }
    string UdpLoggingOnButton { get; }
    string UdpLoggingOffButton { get; }
    string UdpLoggingDescription { get; }
    string BackendHostTitle { get; }
    string BackendHostDescription { get; }
    string BackendFolderLabel { get; }
    string BackendFolderTooltip { get; }
    string BackendFolderPlaceholder { get; }
    string BrowseButton { get; }
    string BrowseBackendTooltip { get; }
    string GuideButton { get; }
    string GuideTooltip { get; }
    string ZeroTierAddressLabel { get; }
    string ZeroTierAddressTooltip { get; }
    string ZeroTierAddressPlaceholder { get; }
    string DetectButton { get; }
    string DetectAddressTooltip { get; }
    string PlayersLabel { get; }
    string PlayersTooltip { get; }
    string FillWaitLabel { get; }
    string FillWaitTooltip { get; }
    string PlayerConnectionUrlLabel { get; }
    string PlayerConnectionUrlPlaceholder { get; }
    string CopyPlayerUrlTooltip { get; }
    string PlayerConnectionUrlDescription { get; }
    string AdvancedCommandHeader { get; }
    string StartBackendButton { get; }
    string StartBackendTooltip { get; }
    string StopBackendButton { get; }
    string StopBackendTooltip { get; }
    string BackendStoppedStatus { get; }
    string BackendRunningStatus { get; }
    string BackendStoppingStatus { get; }
    string BackendPorts { get; }
    string PleaseWait { get; }
    string SettingsLoadFailureStatus { get; }
    string DetectingZeroTierStatus { get; }
    string ZeroTierDetectedStatus { get; }
    string SelectBackendFolderDialogTitle { get; }
    string BackendFolderMissingFormat { get; }
    string RelayStartErrorSuffix { get; }
    string ErrorInvalidUsername { get; }
    string ErrorInvalidSecretKey { get; }
    string ErrorInvalidBackendUrl { get; }
    string ErrorSavedSettingsEmpty { get; }
    string ErrorRelayAlreadyRunning { get; }
    string ErrorBackendPointsToRelay { get; }
    string ErrorAccountSignInFailedFormat { get; }
    string ErrorBackendEmptyAccountResponse { get; }
    string ErrorBackendInvalidAccountResponse { get; }
    string ErrorDetectionTimedOut { get; }
    string ErrorIpconfigCouldNotStart { get; }
    string ErrorIpconfigFailed { get; }
    string ErrorIpconfigFailedFormat { get; }
    string ErrorNoZeroTierAddress { get; }
    string ErrorInvalidPlayerCount { get; }
    string ErrorInvalidFillWait { get; }
    string ErrorInvalidZeroTierAddress { get; }
    string ErrorBackendFolderDoesNotExist { get; }
    string ErrorBackendExecutableMissingFormat { get; }
    string ErrorBackendCommandRequired { get; }
    string ErrorBackendAlreadyRunning { get; }
    string ErrorBackendProcessCouldNotStart { get; }
    string PortsInUseFormat { get; }
    string ClosePortOwnersButtonFormat { get; }
    string ErrorPortOwnerCouldNotCloseFormat { get; }
    string AlreadyRunningMessage { get; }
    string ErrorBackendExecutablePermission { get; }
    string ErrorBackendUrlRequired { get; }
    string GetRelayStatusText(RelayStatus status);
    string GetRelayErrorText(RelayErrorCode code, int? statusCode);
    string GetDesktopErrorText(DesktopErrorCode code, string? detail, string? entryName);
    string GetBackendFolderMissingText(string entryName);
    string GetRelayStartErrorText(string details);
    string GetPortsInUseText(IEnumerable<string> processNames);
    string GetClosePortOwnersButtonText(IEnumerable<string> processNames);
}
