using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReVerse.Relay;

namespace ReVerse.Relay.Desktop;

public sealed partial class MainWindow : Window
{
    private const string LaunchOptions = "/Rebe/HjmUriStr:http://127.0.0.1:5080";
    private const string GuideUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3800233626";
    private const int RelayHttpPort = 5080;
    private const int RelayHttpsPort = 5081;
    private const double CompactWidth = 560;
    private const double ExpandedWidth = 1120;
    private static bool SupportsBackendHosting => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#5E5751"));
    private static readonly IBrush WorkingBrush = new SolidColorBrush(Color.Parse("#C98F43"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#94A978"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C7212B"));
    private readonly BackendHostController backendHost = new();
    private readonly ILocalizationService localization = new TranslationService();
    private RelaySettings settings = new();
    private RelayService? service;
    private string? detectedZeroTierAddress;
    private Exception? currentError;
    private IReadOnlyList<PortOwner> portOwners = [];
    private Exception? zeroTierError;
    private Exception? backendStatusError;
    private RelayStatus relayStatus = RelayStatus.Stopped;
    private ZeroTierStatus zeroTierStatus;
    private BackendStatus backendStatus;
    private bool busy;
    private bool hostExpanded;
    private bool addressDetectionStarted;
    private bool applyingLanguage;
    private bool launchOptionsCopied;
    private bool backendUrlCopied;
    private bool apiKeyCopied;

    public MainWindow()
    {
        InitializeComponent();
        LanguageSelector.ItemsSource = localization.AvailableLanguages;
        localization.LanguageChanged += OnLanguageChanged;
        SecretEntry.Text = CreateSessionSecret();
        LaunchOptionsEntry.Text = LaunchOptions;
        PlayersEntry.Value = 2;
        FillWaitEntry.Value = 15;
        Closed += (_, _) => StopForShutdown();
        LoadSettings();
        HostToggleButton.IsVisible = SupportsBackendHosting;
        BackendHostPanel.IsVisible = false;
        if (SupportsBackendHosting)
            backendHost.RunningChanged += OnBackendRunningChanged;
        ApplyLanguage();
    }

    private static string CreateSessionSecret()
    {
        var seed = unchecked((int)DateTime.UtcNow.Ticks);
        return new Random(seed).Next(1, 10_000_001).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLanguage();
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (applyingLanguage || LanguageSelector.SelectedItem is not ITranslation language)
            return;
        if (!localization.SetLanguage(language.Code))
            return;

        settings.LanguageCode = localization.Current.Code;
        try
        {
            settings.Save();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ApplyLanguage()
    {
        var text = localization.Current;
        applyingLanguage = true;
        LanguageSelector.SelectedItem = text;
        applyingLanguage = false;

        Title = text.WindowTitle;
        BrandPrefixRun.Text = text.BrandPrefix;
        BrandAccentRun.Text = text.BrandAccent;
        BrandSuffixRun.Text = text.BrandSuffix;
        TaglineText.Text = text.Tagline;
        ToolTip.SetTip(LanguageSelector, text.LanguageSelectorTooltip);
        HostToggleButton.Content = hostExpanded ? text.RelayOnlyButton : text.HostBackendButton;
        ToolTip.SetTip(HostToggleButton, hostExpanded ? text.RelayOnlyTooltip : text.HostBackendTooltip);

        PlayerRelayTitleText.Text = text.PlayerRelayTitle;
        PlayerRelayDescriptionText.Text = text.PlayerRelayDescription;
        UsernameLabelText.Text = text.UsernameLabel;
        UsernameEntry.PlaceholderText = text.UsernamePlaceholder;
        BackendServerLabelText.Text = text.BackendServerLabel;
        BackendEntry.PlaceholderText = text.BackendServerPlaceholder;
        ToolTip.SetTip(BackendServerHelp, text.BackendServerTooltip);
        ServerApiKeyLabelText.Text = text.ServerApiKeyLabel;
        ServerApiKeyEntry.PlaceholderText = text.ServerApiKeyPlaceholder;
        ToolTip.SetTip(ServerApiKeyHelp, text.ServerApiKeyTooltip);
        SteamLaunchOptionLabelText.Text = text.SteamLaunchOptionLabel;
        ToolTip.SetTip(SteamLaunchOptionHelp, text.SteamLaunchOptionTooltip);
        ToolTip.SetTip(CopyButton, text.CopyLaunchOptionTooltip);
        CopyButton.Content = launchOptionsCopied ? text.CopiedButton : text.CopyButton;
        ToggleButton.Content = service is null ? text.StartRelayButton : text.StopRelayButton;
        ToolTip.SetTip(ToggleButton, service is null ? text.StartRelayTooltip : text.StopRelayTooltip);
        RenderDetailedLogs();
        RelayPortsText.Text = text.RelayPorts;
        PleaseWaitText.Text = text.PleaseWait;

        BackendHostTitleText.Text = text.BackendHostTitle;
        BackendHostDescriptionText.Text = text.BackendHostDescription;
        BackendFolderLabelText.Text = text.BackendFolderLabel;
        HostFolderEntry.PlaceholderText = text.BackendFolderPlaceholder;
        ToolTip.SetTip(BackendFolderHelp, text.BackendFolderTooltip);
        BrowseBackendButton.Content = text.BrowseButton;
        GuideButton.Content = text.GuideButton;
        ToolTip.SetTip(GuideButton, text.GuideTooltip);
        ToolTip.SetTip(BrowseBackendButton, text.BrowseBackendTooltip);
        ZeroTierAddressLabelText.Text = text.ZeroTierAddressLabel;
        ZeroTierAddressEntry.PlaceholderText = text.ZeroTierAddressPlaceholder;
        ToolTip.SetTip(ZeroTierAddressHelp, text.ZeroTierAddressTooltip);
        DetectAddressButton.Content = text.DetectButton;
        ToolTip.SetTip(DetectAddressButton, text.DetectAddressTooltip);
        PlayersLabelText.Text = text.PlayersLabel;
        ToolTip.SetTip(PlayersHelp, text.PlayersTooltip);
        FillWaitLabelText.Text = text.FillWaitLabel;
        ToolTip.SetTip(FillWaitHelp, text.FillWaitTooltip);
        HostApiKeyLabelText.Text = text.ServerApiKeyLabel;
        HostApiKeyEntry.PlaceholderText = text.HostApiKeyPlaceholder;
        ToolTip.SetTip(HostApiKeyHelp, text.HostApiKeyTooltip);
        GenerateApiKeyButton.Content = text.GenerateButton;
        ToolTip.SetTip(GenerateApiKeyButton, text.GenerateApiKeyTooltip);
        CopyApiKeyButton.Content = apiKeyCopied ? text.CopiedButton : text.CopyButton;
        ToolTip.SetTip(CopyApiKeyButton, text.CopyApiKeyTooltip);
        PlayerConnectionUrlLabelText.Text = text.PlayerConnectionUrlLabel;
        PlayerBackendUrlEntry.PlaceholderText = text.PlayerConnectionUrlPlaceholder;
        CopyBackendUrlButton.Content = backendUrlCopied ? text.CopiedButton : text.CopyButton;
        ToolTip.SetTip(CopyBackendUrlButton, text.CopyPlayerUrlTooltip);
        PlayerConnectionUrlDescriptionText.Text = text.PlayerConnectionUrlDescription;
        AdvancedCommandExpander.Header = text.AdvancedCommandHeader;
        StartBackendButton.Content = text.StartBackendButton;
        ToolTip.SetTip(StartBackendButton, text.StartBackendTooltip);
        StopBackendButton.Content = text.StopBackendButton;
        ToolTip.SetTip(StopBackendButton, text.StopBackendTooltip);
        BackendPortsText.Text = text.BackendPorts;
        RenderUdpLogging();
        UdpLoggingDescriptionText.Text = text.UdpLoggingDescription;

        SetStatus(relayStatus);
        RenderZeroTierStatus();
        RenderBackendStatus();
        UpdateBackendFolderStatus(HostFolderEntry.Text?.Trim() ?? "");

        if (currentError is not null)
            RenderError();
    }

    private static string GetBackendEntryName() => ZeroTierHost.BackendEntryName;

    private async void OnHostToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (!SupportsBackendHosting)
            return;

        hostExpanded = !hostExpanded;
        BackendHostPanel.IsVisible = hostExpanded;
        GuideButton.IsVisible = hostExpanded;
        ContentGrid.ColumnDefinitions[1].Width = hostExpanded
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        MinWidth = hostExpanded ? 1000 : 500;
        Width = hostExpanded ? ExpandedWidth : CompactWidth;
        ApplyLanguage();

        if (hostExpanded && !addressDetectionStarted)
        {
            addressDetectionStarted = true;
            await DetectZeroTierAddressAsync();
        }
    }

    private async void OnGuideClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var launcher = GetTopLevel(this)?.Launcher;
            if (launcher is not null)
                await launcher.LaunchUriAsync(new Uri(GuideUrl));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void LoadSettings()
    {
        try
        {
            settings = RelaySettings.Load();
            if (!localization.SetLanguage(settings.LanguageCode))
                settings.LanguageCode = localization.Current.Code;
            UsernameEntry.Text = settings.Username;
            BackendEntry.Text = settings.BackendAddress;
            HostFolderEntry.Text = settings.BackendFolder;
            PlayersEntry.Value = Math.Clamp(settings.BackendPlayers, 2, 10);
            FillWaitEntry.Value = Math.Clamp(settings.BackendFillWaitSeconds, 0, ZeroTierHost.MaxFillWaitSeconds);
        }
        catch (Exception exception)
        {
            ShowError(exception);
            ToggleButton.IsEnabled = false;
            SetStatus(RelayStatus.SettingsLoadFailed);
        }
    }

    private async void OnToggleClicked(object? sender, RoutedEventArgs e)
    {
        await ToggleAsync();
    }

    private void OnDetailedLogsClicked(object? sender, RoutedEventArgs e)
    {
        var previous = settings.DetailedLogsEnabled;
        try
        {
            var enabled = !previous;
            SetDetailedLogsMarker(enabled);
            settings.DetailedLogsEnabled = enabled;
            settings.Save();
            if (service is not null) service.DetailedLogsEnabled = enabled;
            RenderDetailedLogs();
            HideError();
        }
        catch (Exception exception)
        {
            settings.DetailedLogsEnabled = previous;
            try { SetDetailedLogsMarker(previous); } catch { }
            ShowError(exception);
        }
    }

    private void OnUdpLoggingClicked(object? sender, RoutedEventArgs e)
    {
        var previous = settings.BackendUdpLoggingAllowed;
        try
        {
            if (backendHost.IsRunning) return;
            settings.BackendUdpLoggingAllowed = !previous;
            settings.Save();
            UpdateHostCommand();
        }
        catch (Exception exception)
        {
            settings.BackendUdpLoggingAllowed = previous;
            SetBackendStatus(BackendStatus.Error, exception);
        }
        finally
        {
            RenderUdpLogging();
        }
    }

    private void RenderUdpLogging()
    {
        var text = localization.Current;
        UdpLoggingButton.Content = settings.BackendUdpLoggingAllowed ? text.UdpLoggingOnButton : text.UdpLoggingOffButton;
    }

    private void RenderDetailedLogs()
    {
        var text = localization.Current;
        DetailedLogsButton.Content = settings.DetailedLogsEnabled ? text.DetailedLogsOnButton : text.DetailedLogsOffButton;
        DetailedLogsDescriptionText.Text = text.DetailedLogsDescription;
        DetailedLogsPathText.IsVisible = settings.DetailedLogsEnabled;
        var paths = $"{text.DetailedLogsPathLabel}: {RelaySettings.DetailedLogsDirectory}";
        var folder = HostFolderEntry.Text?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            paths += $"\n{text.BackendHostTitle}: {Path.Combine(Path.GetFullPath(folder), "logs")}";
        DetailedLogsPathText.Text = paths;
    }

    private static void SetDetailedLogsMarker(bool enabled)
    {
        var marker = RelaySettings.DetailedLogsControlFile;
        if (enabled)
        {
            Directory.CreateDirectory(RelaySettings.DataDirectory);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        else if (File.Exists(marker)) File.Delete(marker);
    }

    private async void OnBackendKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && service is null)
            await ToggleAsync();
    }

    private async Task ToggleAsync()
    {
        if (busy)
            return;
        busy = true;
        ToggleButton.IsEnabled = false;
        BusyOverlay.IsVisible = true;
        HideError();
        try
        {
            if (service is not null)
            {
                await StopAsync();
                return;
            }

            settings.Username = UsernameEntry.Text ?? "";
            settings.SecretKey = SecretEntry.Text ?? "";
            settings.BackendAddress = BackendEntry.Text ?? "";
            settings.ServerApiKey = ServerApiKeyEntry.Text ?? "";
            settings.LanguageCode = localization.Current.Code;
            settings.Validate();
            settings.Save();
            SetInputsEnabled(false);
            SetStatus(RelayStatus.SigningIn);

            var current = new RelayService();
            current.DetailedLogsEnabled = settings.DetailedLogsEnabled;
            service = current;
            current.StatusChanged += status => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(service, current))
                    SetStatus(status);
            });
            current.DetailedLogError += exception => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(service, current)) ShowError(exception);
            });
            await current.StartAsync(settings);
            ToggleButton.Content = localization.Current.StopRelayButton;
        }
        catch (Exception exception)
        {
            var owners = FindRelayPortOwners();
            await StopAsync();
            SetStatus(RelayStatus.StartFailed);
            ShowError(exception, appendRelayStartAdvice: true, owners);
        }
        finally
        {
            busy = false;
            BusyOverlay.IsVisible = false;
            ToggleButton.IsEnabled = true;
        }
    }

    private async Task StopAsync()
    {
        var old = service;
        service = null;
        if (old is not null)
            await old.DisposeAsync();
        SetInputsEnabled(true);
        ToggleButton.Content = localization.Current.StartRelayButton;
        SetStatus(RelayStatus.Stopped);
    }

    private void SetInputsEnabled(bool enabled)
    {
        UsernameEntry.IsEnabled = enabled;
        SecretEntry.IsEnabled = enabled;
        BackendEntry.IsEnabled = enabled;
        ServerApiKeyEntry.IsEnabled = enabled;
    }

    private void SetStatus(RelayStatus status)
    {
        relayStatus = status;
        StatusText.Text = localization.Current.GetRelayStatusText(status);
        StatusDot.Background = StatusBrush(status);
    }

    private static IBrush StatusBrush(RelayStatus status) => status switch
    {
        RelayStatus.BackendConnectionFailed or RelayStatus.SessionExpired or
            RelayStatus.StartFailed or RelayStatus.SettingsLoadFailed => ErrorBrush,
        RelayStatus.RunningWaitingForGame or RelayStatus.RunningConnectedToBackend => RunningBrush,
        RelayStatus.Stopped => StoppedBrush,
        _ => WorkingBrush
    };

    private void ShowError(Exception exception, bool appendRelayStartAdvice = false, IReadOnlyList<PortOwner>? owners = null)
    {
        currentError = exception;
        portOwners = owners ?? [];
        errorIncludesRelayStartAdvice = appendRelayStartAdvice;
        RenderError();
    }

    private bool errorIncludesRelayStartAdvice;

    private void RenderError()
    {
        if (currentError is null)
            return;
        var text = localization.Current;
        var message = GetErrorText(currentError);
        if (errorIncludesRelayStartAdvice)
            message = text.GetRelayStartErrorText(message);
        var ownerNames = portOwners
            .Select(owner => owner.ProcessName.Length > 0 ? owner.ProcessName : owner.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ownerNames.Length > 0)
            message += "\n" + text.GetPortsInUseText(ownerNames);
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        ClosePortOwnersButton.Content = text.GetClosePortOwnersButtonText(ownerNames);
        ClosePortOwnersButton.IsVisible = ownerNames.Length > 0;
    }

    private static IReadOnlyList<PortOwner> FindRelayPortOwners()
    {
        try
        {
            return PortOwners.FindListeners(RelayHttpPort, RelayHttpsPort);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async void OnClosePortOwnersClicked(object? sender, RoutedEventArgs e)
    {
        if (busy || service is not null)
            return;
        var owners = portOwners;
        ClosePortOwnersButton.IsEnabled = false;
        try
        {
            var current = FindRelayPortOwners();
            foreach (var owner in owners.Where(current.Contains))
            {
                bool closed;
                try
                {
                    closed = await PortOwners.CloseAsync(owner, TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    throw new DesktopException(DesktopErrorCode.PortOwnerCouldNotClose, entryName: owner.ProcessName, innerException: exception);
                }
                if (!closed)
                    throw new DesktopException(DesktopErrorCode.PortOwnerCouldNotClose, entryName: owner.ProcessName);
            }
        }
        catch (DesktopException exception)
        {
            ShowError(exception, owners: FindRelayPortOwners());
            return;
        }
        finally
        {
            ClosePortOwnersButton.IsEnabled = true;
        }
        await ToggleAsync();
    }

    private string GetErrorText(Exception exception) => exception switch
    {
        RelayException relayException => localization.Current.GetRelayErrorText(relayException.Code, relayException.StatusCode),
        DesktopArgumentOutOfRangeException desktopArgumentException => localization.Current.GetDesktopErrorText(desktopArgumentException.Code, null, null),
        DesktopException desktopException => localization.Current.GetDesktopErrorText(desktopException.Code, desktopException.Detail, desktopException.EntryName),
        _ => exception.Message
    };

    private void HideError()
    {
        currentError = null;
        portOwners = [];
        ClosePortOwnersButton.IsVisible = false;
        errorIncludesRelayStartAdvice = false;
        ErrorText.Text = "";
        ErrorText.IsVisible = false;
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(LaunchOptions);
        launchOptionsCopied = true;
        CopyButton.Content = localization.Current.CopiedButton;
        await Task.Delay(1200);
        launchOptionsCopied = false;
        CopyButton.Content = localization.Current.CopyButton;
    }

    private void OnHostConfigurationChanged(object? sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ZeroTierAddressEntry) &&
            !ZeroTierHost.IsDetectedAddress(ZeroTierAddressEntry.Text, detectedZeroTierAddress))
        {
            zeroTierStatus = ZeroTierStatus.None;
            zeroTierError = null;
            RenderZeroTierStatus();
        }
        UpdateHostCommand();
        RenderDetailedLogs();
    }

    private void OnPlayerCountChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        UpdateHostCommand();
    }

    private void OnBackendCommandChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStartBackendAvailability();
    }

    private async void OnDetectAddress(object? sender, RoutedEventArgs e)
    {
        await DetectZeroTierAddressAsync();
    }

    private async Task DetectZeroTierAddressAsync()
    {
        DetectAddressButton.IsEnabled = false;
        detectedZeroTierAddress = null;
        zeroTierError = null;
        zeroTierStatus = ZeroTierStatus.Detecting;
        RenderZeroTierStatus();
        try
        {
            var address = await ZeroTierHost.DetectAddressAsync();
            if (address is null)
                throw new DesktopException(DesktopErrorCode.NoZeroTierAddress);
            detectedZeroTierAddress = address;
            ZeroTierAddressEntry.Text = address;
            zeroTierStatus = ZeroTierStatus.Detected;
            RenderZeroTierStatus();
        }
        catch (Exception exception)
        {
            detectedZeroTierAddress = null;
            zeroTierError = exception;
            zeroTierStatus = ZeroTierStatus.Error;
            RenderZeroTierStatus();
        }
        finally
        {
            DetectAddressButton.IsEnabled = !backendHost.IsRunning;
            UpdateHostCommand();
        }
    }

    private void RenderZeroTierStatus()
    {
        switch (zeroTierStatus)
        {
            case ZeroTierStatus.Detecting:
                ZeroTierStatusText.Text = localization.Current.DetectingZeroTierStatus;
                ZeroTierStatusText.Foreground = WorkingBrush;
                ZeroTierStatusText.IsVisible = true;
                break;
            case ZeroTierStatus.Detected:
                ZeroTierStatusText.Text = localization.Current.ZeroTierDetectedStatus;
                ZeroTierStatusText.Foreground = RunningBrush;
                ZeroTierStatusText.IsVisible = true;
                break;
            case ZeroTierStatus.Error when zeroTierError is not null:
                ZeroTierStatusText.Text = GetErrorText(zeroTierError);
                ZeroTierStatusText.Foreground = ErrorBrush;
                ZeroTierStatusText.IsVisible = true;
                break;
            default:
                ZeroTierStatusText.Text = "";
                ZeroTierStatusText.IsVisible = false;
                break;
        }
    }

    private async void OnBrowseBackendFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = localization.Current.SelectBackendFolderDialogTitle,
                AllowMultiple = false
            });
            if (folders.Count == 0)
                return;
            HostFolderEntry.Text = folders[0].Path.LocalPath;
            settings.BackendFolder = HostFolderEntry.Text;
            settings.Save();
            UpdateHostCommand();
        }
        catch (Exception exception)
        {
            SetBackendStatus(BackendStatus.Error, exception);
        }
    }

    private void UpdateHostCommand()
    {
        var folder = HostFolderEntry.Text?.Trim() ?? "";
        var address = ZeroTierAddressEntry.Text?.Trim() ?? "";
        UpdateBackendFolderStatus(folder);
        try
        {
            if (folder.Length == 0 || address.Length == 0)
            {
                BackendCommandEntry.Text = "";
                PlayerBackendUrlEntry.Text = "";
                CopyBackendUrlButton.IsEnabled = false;
                StartBackendButton.IsEnabled = false;
                return;
            }
            var players = decimal.ToInt32(PlayersEntry.Value ?? 2);
            var fillWait = decimal.ToInt32(FillWaitEntry.Value ?? 0);
            BackendCommandEntry.Text = ZeroTierHost.BuildCommand(folder, address, players, settings.BackendUdpLoggingAllowed, fillWait,
                HostApiKeyEntry.Text ?? "");
            if (backendStatusError is DesktopException { Code: DesktopErrorCode.InvalidServerApiKey })
                SetBackendStatus(BackendStatus.Stopped);
            PlayerBackendUrlEntry.Text = ZeroTierHost.BuildBackendUrl(address);
            CopyBackendUrlButton.IsEnabled = true;
            UpdateStartBackendAvailability();
        }
        catch (Exception exception)
        {
            BackendCommandEntry.Text = "";
            PlayerBackendUrlEntry.Text = "";
            CopyBackendUrlButton.IsEnabled = false;
            SetBackendStatus(BackendStatus.Error, exception);
            StartBackendButton.IsEnabled = false;
        }
    }

    private void UpdateBackendFolderStatus(string folder)
    {
        if (folder.Length == 0)
        {
            BackendFolderStatusText.Text = "";
            BackendFolderStatusText.IsVisible = false;
            return;
        }

        var missing = true;
        try
        {
            missing = !File.Exists(ZeroTierHost.GetBackendEntryPath(folder));
        }
        catch (Exception) when (folder.Length > 0)
        {
            missing = true;
        }

        BackendFolderStatusText.IsVisible = missing;
        BackendFolderStatusText.Text = missing
            ? localization.Current.GetBackendFolderMissingText(GetBackendEntryName())
            : "";
    }

    private async void OnCopyBackendUrlClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        var url = PlayerBackendUrlEntry.Text;
        if (clipboard is null || string.IsNullOrWhiteSpace(url))
            return;
        await clipboard.SetTextAsync(url);
        backendUrlCopied = true;
        CopyBackendUrlButton.Content = localization.Current.CopiedButton;
        await Task.Delay(1200);
        backendUrlCopied = false;
        CopyBackendUrlButton.Content = localization.Current.CopyButton;
    }

    private void OnGenerateApiKey(object? sender, RoutedEventArgs e)
    {
        HostApiKeyEntry.Text = ZeroTierHost.GenerateApiKey();
    }

    private async void OnCopyApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        var key = HostApiKeyEntry.Text?.Trim();
        if (clipboard is null || string.IsNullOrEmpty(key))
            return;
        await clipboard.SetTextAsync(key);
        apiKeyCopied = true;
        CopyApiKeyButton.Content = localization.Current.CopiedButton;
        await Task.Delay(1200);
        apiKeyCopied = false;
        CopyApiKeyButton.Content = localization.Current.CopyButton;
    }

    private void OnStartBackend(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = HostFolderEntry.Text?.Trim() ?? "";
            var command = BackendCommandEntry.Text ?? "";
            var backendUrl = PlayerBackendUrlEntry.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(backendUrl))
                throw new DesktopException(DesktopErrorCode.BackendUrlRequired);
            settings.BackendFolder = folder;
            settings.BackendAddress = backendUrl;
            settings.BackendPlayers = decimal.ToInt32(PlayersEntry.Value ?? 2);
            settings.BackendFillWaitSeconds = decimal.ToInt32(FillWaitEntry.Value ?? 0);
            settings.Save();
            SetDetailedLogsMarker(settings.DetailedLogsEnabled);
            backendHost.Start(folder, command);
            BackendEntry.Text = backendUrl;
            ServerApiKeyEntry.Text = ZeroTierHost.ValidateApiKey(HostApiKeyEntry.Text);
            SetBackendStatus(BackendStatus.Running);
        }
        catch (Exception exception)
        {
            SetBackendStatus(BackendStatus.Error, exception);
        }
    }

    private async void OnStopBackend(object? sender, RoutedEventArgs e)
    {
        StopBackendButton.IsEnabled = false;
        SetBackendStatus(BackendStatus.Stopping);
        try
        {
            await backendHost.StopAsync();
            SetBackendStatus(BackendStatus.Stopped);
        }
        catch (Exception exception)
        {
            SetBackendStatus(BackendStatus.Error, exception);
        }
        UpdateStartBackendAvailability();
    }

    private void SetBackendStatus(BackendStatus status, Exception? exception = null)
    {
        backendStatus = status;
        backendStatusError = exception;
        RenderBackendStatus();
    }

    private void RenderBackendStatus()
    {
        switch (backendStatus)
        {
            case BackendStatus.Running:
                BackendHostStatusText.Text = localization.Current.BackendRunningStatus;
                BackendHostStatusText.Foreground = RunningBrush;
                break;
            case BackendStatus.Stopping:
                BackendHostStatusText.Text = localization.Current.BackendStoppingStatus;
                BackendHostStatusText.Foreground = WorkingBrush;
                break;
            case BackendStatus.Error when backendStatusError is not null:
                BackendHostStatusText.Text = GetErrorText(backendStatusError);
                BackendHostStatusText.Foreground = ErrorBrush;
                break;
            default:
                BackendHostStatusText.Text = localization.Current.BackendStoppedStatus;
                BackendHostStatusText.Foreground = StoppedBrush;
                break;
        }
    }

    private void OnBackendRunningChanged(bool running)
    {
        Dispatcher.UIThread.Post(() => SetBackendRunning(running));
    }

    private void SetBackendRunning(bool running)
    {
        HostFolderEntry.IsEnabled = !running;
        BrowseBackendButton.IsEnabled = !running;
        ZeroTierAddressEntry.IsEnabled = !running;
        PlayersEntry.IsEnabled = !running;
        FillWaitEntry.IsEnabled = !running;
        HostApiKeyEntry.IsEnabled = !running;
        GenerateApiKeyButton.IsEnabled = !running;
        UdpLoggingButton.IsEnabled = !running;
        DetectAddressButton.IsEnabled = !running;
        StartBackendButton.IsEnabled = !running;
        StopBackendButton.IsEnabled = running;
        if (!running)
        {
            SetBackendStatus(BackendStatus.Stopped);
            UpdateStartBackendAvailability();
        }
    }

    private void UpdateStartBackendAvailability()
    {
        var folder = HostFolderEntry.Text?.Trim().Trim('"') ?? "";
        StartBackendButton.IsEnabled = !backendHost.IsRunning &&
                                       !string.IsNullOrWhiteSpace(BackendCommandEntry.Text) &&
                                       File.Exists(ZeroTierHost.GetBackendEntryPath(folder));
    }

    private void StopForShutdown()
    {
        var old = service;
        service = null;
        backendHost.RunningChanged -= OnBackendRunningChanged;
        Task.Run(async () =>
        {
            if (old is not null)
                await old.DisposeAsync();
            await backendHost.DisposeAsync();
        }).GetAwaiter().GetResult();
    }

    private enum ZeroTierStatus
    {
        None,
        Detecting,
        Detected,
        Error
    }

    private enum BackendStatus
    {
        Stopped,
        Running,
        Stopping,
        Error
    }
}
