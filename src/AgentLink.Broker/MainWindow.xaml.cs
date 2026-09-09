using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using AgentLink.Broker.Shell;
using AgentLink.Broker.ViewModels;
using AgentLink.Broker.Views;
using AgentLink.Copilot;
using AgentLink.Core;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace AgentLink.Broker;

public sealed partial class MainWindow : Window
{
    private readonly BrokerSession _session;
    private readonly UiStartupDiagnostics _startup;
    private readonly ShellLifetime _lifetime = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly ActivityViewModel _activity = new();
    private readonly SettingsViewModel _settings;
    private readonly ConnectionsView _connectionsView;
    private readonly ActivityView _activityView;
    private readonly SettingsView _settingsView;
    private readonly AgentSetupViewModel _agentSetup = new();
    private readonly AgentsView _agentsView;
    private readonly DispatcherQueueTimer _refresh;
    private readonly nint _handle;
    private readonly TrayIcon _tray;
    private PendingConsent? _active;
    private ContentDialog? _approvalDialog;
    private Task? _approvalLoop;
    private bool _started;
    private volatile bool _isClosed;
    private bool _quitInProgress;
    private volatile bool _safeToClose;
    private bool _reviewInProgress;
    private PluginLoadingSettings? _pluginLoading;
    private bool _canManageLegacy;
    private bool _pluginLoadingBusy;
    public ShellViewModel Model { get; } = new();

    internal MainWindow(BrokerSession session, UiStartupDiagnostics startup)
    {
        _session = session;
        Model.DesktopAccessEnabled = session.Consent.DesktopAccessEnabled;
        _startup = startup;
        _settings = new SettingsViewModel
        {
            Fingerprint = session.Identity.Fingerprint,
            DataDirectory = session.DataDirectory,
            Role = $"Endpoint role: {session.Role} · Windows session {Process.GetCurrentProcess().SessionId}"
        };
        _startup.Run(UiStartupPhase.MainWindowInitializeComponent, InitializeComponent);
        _startup.Run(UiStartupPhase.MainWindowTitle, () => Title = "Remote Agent Link");
        _startup.Run(UiStartupPhase.MainWindowApplicationIcon,
            () => AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLink.ico")));
        _startup.Run(UiStartupPhase.MainWindowTitlebarImage, () => WindowTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo44.png")))
        });
        _startup.Run(UiStartupPhase.MainWindowExtendTitlebar, () => ExtendsContentIntoTitleBar = true);
        _startup.Run(UiStartupPhase.MainWindowAttachTitlebar, () => SetTitleBar(WindowTitleBar));
        _startup.Run(UiStartupPhase.MainWindowMica, () => SystemBackdrop = new MicaBackdrop());
        _handle = _startup.Run(UiStartupPhase.MainWindowHandle, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _startup.Run(UiStartupPhase.MainWindowResize, () => AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820)));
        _startup.Checkpoint(UiStartupPhase.MainWindowEvents);
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;
        Root.ActualThemeChanged += ThemeChanged;
        _startup.Run(UiStartupPhase.MainWindowCaptionTheme, ApplyCaptionTheme);

        _connectionsView = _startup.Run(UiStartupPhase.ConnectionsViewConstruct, () => new ConnectionsView(Model));
        _activityView = _startup.Run(UiStartupPhase.ActivityViewConstruct, () => new ActivityView(_activity, Model));
        _settingsView = _startup.Run(UiStartupPhase.SettingsViewConstruct, () => new SettingsView(_settings));
        _agentsView = _startup.Run(UiStartupPhase.AgentsViewConstruct, () => new AgentsView(_agentSetup));
        _startup.Checkpoint(UiStartupPhase.MainWindowBindActions);
        _connectionsView.VerifyRequested += peer => RunUi(() => VerifyPeerAsync(peer));
        _connectionsView.CopyMcpRequested += CopyPeerMcpConfiguration;
        _connectionsView.SetupRequested += ShowSettings;
        _connectionsView.AgentsRequested += ShowAgents;
        _connectionsView.DesktopAccessRequested += enabled => RunUi(() => SetDesktopAccessAsync(enabled));
        _agentsView.RegisterRequested += () => RunUi(RegisterMcpAsync);
        _agentsView.RefreshRequested += RefreshMcpSetup;
        _agentsView.SettingsRequested += ShowSettings;
        _agentsView.CopyCommandRequested += () => CopyText(McpSetup.SetupCommand, "Setup command copied. Run it in the Copilot profile you use.");
        _agentsView.CopyJsonRequested += () => CopyMcpConfiguration(0, "");
        RefreshMcpSetup();
        _activityView.RefreshRequested += RefreshTasks;
        _activityView.DetailsRequested += ShowTaskDetails;
        _activityView.CancelRequested += () => RunUi(CancelTaskAsync);
        _activity.RefreshRequested += RefreshTasks;
        _settingsView.StartupRequested += () => RunUi(OpenStartupAsync);
        _settingsView.StartupRefreshRequested += () => RunUi(RefreshStartupAsync);
        _settingsView.RuntimeRequested += () => RunUi(ConfigureRuntimeAsync);
        _settingsView.PairRequested += fingerprint => RunUi(() => PairAsync(fingerprint, "Manually entered device"));
        _settingsView.CopyFingerprintRequested += () => CopyText(_settings.Fingerprint, "Device fingerprint copied.");
        _settingsView.RevokeRequested += fingerprint => RunUi(() => RevokeAsync(fingerprint));
        _settingsView.RevokeAllRequested += () => RunUi(RevokeAllAsync);
        _settingsView.OpenDataRequested += () => RunUi(() =>
        {
            Process.Start(new ProcessStartInfo(_session.DataDirectory) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        _settingsView.CopyMcpRequested += CopyMcpConfiguration;
        _settingsView.ThemeRequested += SetTheme;
        _settingsView.PluginLoadingRequested += modern => RunUi(() => SetPluginLoadingAsync(modern));
        _settingsView.PluginLoadingRefreshRequested += RefreshPluginLoading;
        _startup.Run(UiStartupPhase.MainWindowLoadTheme, LoadTheme);
        _startup.Run(UiStartupPhase.MainWindowSelectPage, () =>
        {
            Navigation.SelectedItem = ConnectionsNavigation;
            PageContent.Content = _connectionsView;
        });

        _tray = _startup.Run(UiStartupPhase.MainWindowTray, () => new TrayIcon(_handle));
        _lifetime.TrayAvailable = _tray.Available;
        _tray.Invoked += OnTrayInvoked;
        _tray.Lost += OnTrayLost;
        _tray.SessionUnavailable += _session.Host.OnSessionUnavailable;
        _tray.SessionEnding += OnSessionEnding;
        _tray.AppearanceChanged += SystemAppearanceChanged;
        _tray.Failed += message => Post(() => { Model.Notice = message; Show(); });
        if (_tray.RegistrationWarning is { } warning) Model.Notice = warning;
        if (!_tray.Available) Model.Notice = "The notification-area icon is unavailable. Closing minimizes to the taskbar so your controls stay reachable.";
        _startup.Checkpoint(UiStartupPhase.MainWindowSubscribeHost);
        _session.Host.Log += WriteLog;
        _session.Host.StatusChanged += StatusChanged;
        _session.Host.ShowRequested += ShowRequested;
        _session.Consent.Changed += ConsentChanged;
        _session.Identity.UntrustedPeer += UntrustedPeer;
        _session.Identity.TrustChanged += TrustChanged;
        _refresh = _startup.Run(UiStartupPhase.MainWindowRefreshTimer, DispatcherQueue.CreateTimer);
        _refresh.Interval = TimeSpan.FromSeconds(2);
        _refresh.Tick += RefreshTick;
        Root.Loaded += RootLoaded;
        _startup.Checkpoint(UiStartupPhase.MainWindowConstructed);
    }

    private async void RootLoaded(object sender, RoutedEventArgs args)
    {
        if (_started) return;
        _started = true;
        _startup.Checkpoint(UiStartupPhase.RootLoaded);
        _approvalLoop = _startup.Run(UiStartupPhase.ApprovalConsumer, ConsumeApprovalsAsync);
        try
        {
            await _startup.RunAsync(UiStartupPhase.BrokerStart, () => Task.Run(_session.Host.Start));
            WriteLog("Broker started. Automatic discovery does not grant desktop or agent permission.");
            _startup.Run(UiStartupPhase.InitialStatus, RefreshStatus);
            _startup.Run(UiStartupPhase.InitialTrustedPeers, RefreshTrustedPeers);
            await _startup.RunAsync(UiStartupPhase.InitialStartupSetting, RefreshStartupAsync);
            InitializePluginLoading();
            _startup.Run(UiStartupPhase.RefreshTimerStart, _refresh.Start);
            _startup.Checkpoint(UiStartupPhase.Ready);
        }
        catch (Exception exception)
        {
            _startup.Failure(_startup.CurrentPhase, exception);
            await _session.StopAsync();
            Model.Notice = $"The broker could not start at {_startup.FailurePhase}: {_startup.FailureSummary}. See ui-startup.log; quit and reopen to retry.";
            WriteLog(Model.Notice);
            Show();
        }
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_settingsView is null || _activityView is null || _connectionsView is null || _agentsView is null) return;
        if (ReferenceEquals(args.SelectedItem, SettingsNavigation))
        {
            PageContent.Content = _settingsView;
            RefreshTrustedPeers();
            RunUi(RefreshStartupAsync);
            RefreshPluginLoading();
        }
        else if (ReferenceEquals(args.SelectedItem, ActivityNavigation))
        {
            PageContent.Content = _activityView;
            RefreshTasks();
        }
        else if (ReferenceEquals(args.SelectedItem, AgentsNavigation))
        {
            PageContent.Content = _agentsView;
            RefreshMcpSetup();
        }
        else PageContent.Content = _connectionsView;
    }

    private void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (ReferenceEquals(args.InvokedItemContainer, QuitNavigation)) RunUi(QuitAsync);
    }

    private void ShowSettings() { Show(); Navigation.SelectedItem = SettingsNavigation; }
    private void ShowAgents() { Show(); Navigation.SelectedItem = AgentsNavigation; }
    private void Show(bool activity = false)
    {
        if (_isClosed || _safeToClose) return;
        _lifetime.Show();
        NativeWindow.Show(_handle);
        Activate();
        if (activity) Navigation.SelectedItem = ActivityNavigation;
    }

    private void HideOrMinimize()
    {
        _lifetime.TrayAvailable = _tray.EnsurePresent();
        if (_lifetime.RequestClose() == CloseAction.Hide) NativeWindow.Hide(_handle);
        else NativeWindow.Minimize(_handle);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_safeToClose || _lifetime.SessionEnding) return;
        args.Cancel = true;
        if (_quitInProgress) return;
        HideOrMinimize();
    }

    private void OnSessionEnding(bool ending)
    {
        _lifetime.SessionEnding = ending;
        if (ending)
        {
            _active?.Answer.TrySetResult(false);
            _session.Host.OnSessionUnavailable();
        }
    }

    private void OnTrayInvoked(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Open: Show(); break;
            case TrayAction.Activity: Show(activity: true); break;
            case TrayAction.Stop: RunUi(() => SetDesktopAccessAsync(false, showNotice: true)); break;
            case TrayAction.Quit: RunUi(QuitAsync); break;
        }
    }

    private void OnTrayLost()
    {
        if (_lifetime.LoseTray()) Show();
        Model.Notice = "The Windows notification-area icon was lost. The control window will stay reachable from the taskbar until it recovers.";
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && _started && !_isClosed)
        {
            RefreshMcpSetup();
            RunUi(RefreshStartupAsync);
            RefreshPluginLoading();
        }
    }

    private void ThemeChanged(FrameworkElement sender, object args) => ApplyCaptionTheme();
    private void SystemAppearanceChanged() => Post(ApplyCaptionTheme);
    private void ApplyCaptionTheme()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        // null lets Windows choose accessible caption colors, including contrast themes.
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
        titleBar.PreferredTheme = NativeWindow.HighContrast ? TitleBarTheme.UseDefaultAppMode :
            Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    private void ReviewClicked(object sender, RoutedEventArgs args) => RunUi(ReviewApprovalAsync);
    private void NoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => Model.Notice = "";
    private Task SetDesktopAccessAsync(bool enabled, bool showNotice = false)
    {
        try
        {
            _session.SetDesktopAccessEnabled(enabled);
            if (showNotice) Model.ShowDesktopBlockNotice();
        }
        finally
        {
            RefreshStatus();
            _connectionsView.SynchronizeDesktopAccess();
        }
        return Task.CompletedTask;
    }

    private async Task QuitAsync()
    {
        if (_quitInProgress || _isClosed) return;
        _quitInProgress = true;
        Show();
        try
        {
            int? activeCount;
            try { activeCount = _session.Host.ListLocalTasks(true).Count; }
            catch (Exception exception) when (exception is AgentLinkException or SqliteException or IOException)
            { activeCount = null; }
            if (activeCount is not 0 || _session.Consent.HasDesktopLease || _active is not null)
            {
                if (!await ConfirmAsync("Quit and stop the broker?",
                    (activeCount is null ? "Active task state could not be read. " : $"{activeCount} active tasks on this device. ") +
                    "Quitting interrupts owned work and ends desktop access. " +
                    "Already performed side effects are not rolled back.\n\nTo leave approved work running, close the window instead.", "Quit and stop"))
                    return;
            }
            _lifetime.Quitting = true;
            Model.IsBusy = true;
            Model.Notice = "Stopping the broker and its owned work. Please wait…";
            _active?.Answer.TrySetResult(false);
            _session.Host.StopDesktopControl();
            await _session.StopAsync();
            _safeToClose = true;
            Close();
        }
        finally
        {
            if (!_safeToClose) { _quitInProgress = false; _lifetime.Quitting = false; Model.IsBusy = false; }
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed) return;
        _isClosed = true;
        _refresh.Stop();
        _refresh.Tick -= RefreshTick;
        _closed.Cancel();
        _active?.Answer.TrySetResult(false);
        _session.Host.Log -= WriteLog;
        _session.Host.StatusChanged -= StatusChanged;
        _session.Host.ShowRequested -= ShowRequested;
        _session.Consent.Changed -= ConsentChanged;
        _session.Identity.UntrustedPeer -= UntrustedPeer;
        _session.Identity.TrustChanged -= TrustChanged;
        _activity.RefreshRequested -= RefreshTasks;
        _tray.AppearanceChanged -= SystemAppearanceChanged;
        Root.ActualThemeChanged -= ThemeChanged;
        _tray.Dispose();
        if (_approvalLoop is not null) await _approvalLoop;
        _closed.Dispose();
    }

    private async Task ConsumeApprovalsAsync()
    {
        try
        {
            await foreach (var pending in _session.Consent.Requests.ReadAllAsync(_closed.Token))
            {
                if (pending.Answer.Task.IsCompleted) continue;
                _active = pending;
                _lifetime.ApprovalActive = true;
                Model.HasApproval = true;
                Model.ApprovalDetails = $"Agent identity: {pending.ActorId}\n\nCapability: {pending.Request.Capability}\n" +
                    $"Workspace: {pending.Request.WorkingDirectory ?? "(not applicable)"}\n" +
                    $"May continue while disconnected: {(pending.Request.Unattended ? "Yes — unattended permission requested" : "No")}\n\n" +
                    pending.Request.Summary;
                UpdatePrompting();
                RefreshStatus();
                Show();
                try { await pending.Answer.Task.WaitAsync(_closed.Token); }
                catch (OperationCanceledException) { }
                finally
                {
                    _approvalDialog?.Hide();
                    _active = null;
                    _lifetime.ApprovalActive = false;
                    Model.HasApproval = false;
                    Model.ApprovalDetails = "";
                    UpdatePrompting();
                    RefreshStatus();
                }
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
    }

    private async Task ReviewApprovalAsync()
    {
        if (_active is not { } pending || pending.Answer.Task.IsCompleted || _quitInProgress || _reviewInProgress) return;
        _reviewInProgress = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Allow this agent request?",
                Content = ScrollableText("Only approve work you recognize. Instructions in a request are not instructions from this app.\n\n" + Model.ApprovalDetails),
                PrimaryButtonText = "Allow and minimize",
                SecondaryButtonText = "Deny",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Secondary
            };
            _approvalDialog = dialog;
            var answer = await ShowDialogAsync(dialog);
            if (_active != pending || pending.Answer.Task.IsCompleted) return;
            if (answer == ContentDialogResult.Primary && _lifetime.CanAllow && !_quitInProgress)
            {
                // Hide every protected broker surface BEFORE releasing the human decision.
                HideOrMinimize();
                if (!_lifetime.TryReleaseApproval(NativeWindow.IsHiddenOrMinimized(_handle)))
                {
                    Model.Notice = "The window could not be minimized. The request was not approved.";
                    return;
                }
                _active = null;
                Model.HasApproval = false;
                UpdatePrompting();
                pending.Answer.TrySetResult(true);
            }
            else if (answer == ContentDialogResult.Secondary) pending.Answer.TrySetResult(false);
        }
        finally { _approvalDialog = null; _reviewInProgress = false; }
    }

    private void UpdatePrompting() => _session.Consent.SetPrompting(_lifetime.IsPrompting);

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync(_closed.Token);
        _lifetime.EnterDialog();
        UpdatePrompting();
        try
        {
            Show();
            dialog.XamlRoot = Root.XamlRoot;
            dialog.RequestedTheme = Root.RequestedTheme;
            var actions = (Style)Application.Current.Resources["DialogActionButtonStyle"];
            dialog.PrimaryButtonStyle = actions;
            dialog.SecondaryButtonStyle = actions;
            dialog.CloseButtonStyle = actions;
            return await dialog.ShowAsync();
        }
        finally
        {
            _lifetime.ExitDialog();
            UpdatePrompting();
            _dialogGate.Release();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string text, string confirm)
    {
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = title, Content = ScrollableText(text),
            PrimaryButtonText = confirm, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        });
        return result == ContentDialogResult.Primary;
    }

    private static ScrollViewer ScrollableText(string text) => new()
    {
        MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
    };

    private Task VerifyPeerAsync(PeerViewModel peer) => peer.NeedsVerification
        ? PairAsync(peer.Fingerprint, peer.DisplayName)
        : ShowPairingDetailsAsync(peer);

    private async Task ShowPairingDetailsAsync(PeerViewModel peer)
    {
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = peer.DisplayName,
            Content = ScrollableText($"Device name is not proof of identity.\n\nCertificate SHA-256:\n{peer.Fingerprint}\n\n" +
                $"{peer.Pairing}\n\n{peer.McpDetails}\n\n{peer.Detail}\n\nTrust can be revoked in Settings."),
            CloseButtonText = "Done", PrimaryButtonText = "Copy details"
        });
        if (result == ContentDialogResult.Primary) CopyText($"{peer.Fingerprint}\n\n{peer.McpDetails}", "Pairing details copied.");
    }

    private async Task PairAsync(string fingerprint, string displayName)
    {
        var normalized = fingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Enter the full 64 hexadecimal characters of the certificate SHA-256 fingerprint.");
        var check = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "I compared all 64 characters with the other device over a trusted connection.",
                TextWrapping = TextWrapping.Wrap
            }
        };
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock { Text = $"{displayName}\nA device name is only a label, not identity proof.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBox { Header = "Other device’s certificate SHA-256", Text = normalized, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Compare this complete fingerprint with the other endpoint’s Settings. Pairing does not authorize desktop access or agent work.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(check);
        var dialog = new ContentDialog
        {
            Title = "Verify device identity",
            Content = new ScrollViewer { Content = content, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "Pair device", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        check.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || check.IsChecked != true) return;
        _session.Identity.Trust(normalized);
        Model.Notice = "Identity saved. Verify this device on the other endpoint too. Pairing does not grant desktop or agent permissions.";
        RefreshTrustedPeers();
        RefreshStatus();
    }

    private async Task RevokeAsync(string? fingerprint)
    {
        if (fingerprint is null) { Model.Notice = "Select a trusted fingerprint first."; return; }
        if (await ConfirmAsync("Revoke this device?", $"Disconnect this identity and remove its pairing?\n\n{fingerprint}\n\nThis does not undo prior task side effects.", "Revoke device"))
        {
            _session.Identity.Revoke(fingerprint);
            Model.Notice = "Device trust revoked. Separately approved task outcomes remain in Activity.";
            RefreshTrustedPeers();
        }
    }

    private async Task RevokeAllAsync()
    {
        if (_settings.TrustedFingerprints.Count == 0) { Model.Notice = "There are no paired devices to revoke."; return; }
        if (await ConfirmAsync("Revoke all paired devices?", "Disconnect and remove every trusted device identity? You will need to verify fingerprints again. This does not undo prior task side effects.", "Revoke all"))
        {
            _session.Identity.RevokeAll();
            RefreshTrustedPeers();
            Model.Notice = "All paired device trust revoked.";
        }
    }

    private void RefreshTrustedPeers()
    {
        var fingerprints = _session.Identity.TrustedFingerprints.Order(StringComparer.Ordinal).ToArray();
        if (_settings.TrustedFingerprints.SequenceEqual(fingerprints)) return;
        _settings.TrustedFingerprints.Clear();
        foreach (var fingerprint in fingerprints) _settings.TrustedFingerprints.Add(fingerprint);
    }

    private void RefreshTasks()
    {
        if (_isClosed || !_started) return;
        try { _activity.Update(_session.Host.ListLocalTasks(_activity.ActiveOnly, _activity.BeforeTaskId)); ShowTaskDetails(); }
        catch (Exception exception) when (exception is AgentLinkException or SqliteException or IOException)
        { _activity.Status = $"Task refresh failed: {exception.Message}. Use Refresh to retry."; }
    }

    private void ShowTaskDetails()
    {
        if (_activity.Selected is not { } selected) { _activity.Details = "Select a task to see its prompt, permission grant and result."; return; }
        try { _activity.ShowDetails(_session.Host.GetLocalTask(selected.TaskId)); }
        catch (Exception exception) when (exception is AgentLinkException or SqliteException or IOException)
        { _activity.Details = $"Task details unavailable: {exception.Message}"; }
    }

    private async Task CancelTaskAsync()
    {
        if (_activity.Selected is not { } task) return;
        if (!await ConfirmAsync("Cancel this task?", $"{task.TaskId}\n\nAlready performed side effects are not rolled back. Cancellation is durable; the task’s actual outcome may take time to update.", "Request cancellation"))
            return;
        _session.Host.CancelLocalTask(task.TaskId);
        RefreshTasks();
        _activity.Status = "Cancellation requested and saved. Refresh for the actual outcome.";
    }

    private async Task ConfigureRuntimeAsync()
    {
        await _dialogGate.WaitAsync(_closed.Token);
        _lifetime.EnterDialog();
        UpdatePrompting();
        Windows.Storage.StorageFile? file;
        try
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _handle);
            file = await picker.PickSingleFileAsync();
        }
        finally
        {
            _lifetime.ExitDialog();
            UpdatePrompting();
            _dialogGate.Release();
        }
        if (file is null) return;
        if (!await ConfirmAsync("Trust this installed runtime?",
            $"{file.Path}\n\nIt runs with your Windows user rights. Select the actual executable, not a shim, and keep its adjacent runtime files. " +
            "This selection does not start it, sign in or authorize work.", "Save runtime"))
            return;
        LocalSetup.ConfigureManagedRuntime(_session.DataDirectory, file.Path);
        _settings.RuntimeStatus = $"Saved: {file.Path}\nIts exact version is checked when a human-approved session starts.";
    }

    private void InitializePluginLoading()
    {
        try
        {
            var package = Package.Current;
            _canManageLegacy = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
                PluginLoadingPackage.HasScopedRegistration(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml"));
            _pluginLoading = new PluginLoadingSettings(new RegistryPluginLoadingStore(package.Id.FamilyName), _canManageLegacy);
            RefreshPluginLoading();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or IOException or
            UnauthorizedAccessException or SecurityException or XmlException)
        {
            _settings.UpdatePluginLoading(null, false, false, false,
                $"RDP loading setup is unavailable: {exception.Message}. Launch the installed MSIX application to configure it.");
            _settingsView.UpdatePluginLoading();
            WriteLog(_settings.PluginLoadingStatus);
        }
    }

    private void RefreshPluginLoading()
    {
        if (_pluginLoading is null || _pluginLoadingBusy) return;
        try { _settings.UpdatePluginLoading(_pluginLoading.Read(), _canManageLegacy, false, true); }
        catch (Exception exception) when (exception is AgentLinkException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _settings.UpdatePluginLoading(null, _canManageLegacy, false, true, $"RDP loading settings could not be read: {exception.Message}");
            WriteLog(_settings.PluginLoadingStatus);
        }
        _settingsView.UpdatePluginLoading();
    }

    private async Task SetPluginLoadingAsync(bool modern)
    {
        if (_pluginLoading is null)
            throw new AgentLinkException("plugin_loading_unavailable", "Launch the installed MSIX application to change RDP loading.");
        if (_pluginLoadingBusy)
            throw new AgentLinkException("plugin_loading_busy", "RDP loading settings are already being saved.");
        _pluginLoadingBusy = true;
        _settings.BeginPluginLoadingChange();
        _settingsView.UpdatePluginLoading();
        string? failure = null;
        try
        {
            var mode = modern ? PluginLoadingMode.Modern : PluginLoadingMode.Legacy;
            await Task.Run(() => PluginLoadingOperation.Run(() => _pluginLoading.Apply(mode)));
            WriteLog($"RDP loading preference saved: {mode}. Existing connections and permissions were not changed.");
        }
        catch (Exception exception) when (exception is AgentLinkException or IOException or UnauthorizedAccessException or SecurityException)
        {
            failure = $"RDP loading was not fully applied: {exception.Message} The status after refresh reflects the saved mode; retry the repair action if needed.";
            WriteLog(failure);
        }
        finally
        {
            _pluginLoadingBusy = false;
            RefreshPluginLoading();
            if (failure is not null) _settings.PluginLoadingStatus += "\n" + failure;
        }
    }

    private async Task RefreshStartupAsync()
    {
        try { _ = Package.Current.Id; }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            _settings.StartupStatus = "Unavailable · this unpackaged build has no Windows startup task registration.";
            return;
        }
        try
        {
            var task = await StartupTask.GetAsync("RemoteAgentLinkBroker");
            _settings.StartupStatus = task.State switch
            {
                StartupTaskState.Enabled => "On · Windows will start the broker when you sign in.",
                StartupTaskState.EnabledByPolicy => "On · enabled by your organization’s policy.",
                StartupTaskState.DisabledByUser => "Off · disabled in Windows Startup Apps.",
                StartupTaskState.DisabledByPolicy => "Off · disabled by your organization’s policy.",
                _ => "Off · startup has not been enabled."
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or UnauthorizedAccessException)
        { _settings.StartupStatus = "Startup state could not be read. Open Windows Startup Apps to check the current setting."; }
    }

    private void LoadTheme()
    {
        try
        {
            var path = Path.Combine(_session.DataDirectory, "ui-theme.txt");
            if (File.Exists(path) && new FileInfo(path).Length <= 32 &&
                Enum.TryParse<ElementTheme>(File.ReadAllText(path), out var theme) && Enum.IsDefined(theme))
            {
                Root.RequestedTheme = theme;
                _settingsView.SetThemeSelection(theme);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Model.Notice = "The saved appearance setting could not be read. Using your Windows theme."; }
    }

    private void SetTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        try { File.WriteAllText(Path.Combine(_session.DataDirectory, "ui-theme.txt"), theme.ToString()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Model.Notice = "Appearance changed for this window, but could not be saved."; }
    }

    private async Task OpenStartupAsync()
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")))
            Model.Notice = "Windows Startup Apps could not be opened. Open Settings → Apps → Startup manually.";
        await RefreshStartupAsync();
    }

    private void CopyMcpConfiguration(int target, string connectionId)
    {
        if (target == 2 && string.IsNullOrWhiteSpace(connectionId))
        { Model.Notice = "Enter the exact connection ID from rdp_list_connections before copying a fixed-target configuration."; return; }
        CopyText(McpConfiguration.Create((McpTarget)target, connectionId.Trim()),
            "MCP configuration copied. Add it to your client’s MCP server configuration.");
    }

    private void RefreshMcpSetup()
    {
        if (_agentSetup.Busy) return;
        try
        {
            var registration = McpSetup.Inspect(McpSetup.ConfigurationPath, McpSetup.AliasPath);
            _agentSetup.CanRegister = registration == McpRegistration.Missing;
            (_agentSetup.Status, _agentSetup.Detail) = registration switch
            {
                McpRegistration.Saved => ("Saved in Copilot configuration", "Start a new Copilot CLI session to load the server. A running broker alone does not attach it to an agent."),
                McpRegistration.Custom => ("A custom entry already exists", "Your existing target and settings were left unchanged. Review remote-agent-link using /mcp in that Copilot profile."),
                McpRegistration.Invalid => ("Configuration needs attention", "The configuration could not be interpreted safely. Open Copilot's /mcp settings; this app will not replace it."),
                _ => ("Not added to Copilot yet", "Add the installed MCP command to this Copilot profile. This does not grant any desktop or agent permission.")
            };
            Model.McpSetupStatus = _agentSetup.Status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _agentSetup.CanRegister = false;
            _agentSetup.Status = "Setup status unavailable";
            _agentSetup.Detail = "Copilot configuration could not be read. Check the displayed profile path and use /mcp in your agent.";
            Model.McpSetupStatus = _agentSetup.Status;
        }
    }

    private async Task RegisterMcpAsync()
    {
        if (_agentSetup.Busy) return;
        _agentSetup.Busy = true;
        _agentSetup.CanRegister = false;
        try
        {
            await McpSetup.RegisterAsync(_closed.Token);
            Model.Notice = "MCP setup saved. Start a new Copilot CLI session, then run /mcp show remote-agent-link. No desktop permissions were granted.";
        }
        finally { _agentSetup.Busy = false; RefreshMcpSetup(); }
    }

    private void CopyPeerMcpConfiguration(PeerViewModel peer)
    {
        var snapshot = _session.Host.PeerConnections.FirstOrDefault(current => current.Id == peer.Id &&
            current.SessionId == peer.SessionId &&
            string.Equals(current.Fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase) &&
            (current.Direction == PeerDirection.IncomingAgent) == peer.IsIncoming);
        var currentPeer = snapshot is null ? null : new PeerViewModel(snapshot, _session.Host.Connections);
        if (currentPeer?.CanCopyMcpConfiguration != true)
        {
            Model.Notice = "This device no longer has one ready MCP target. Refresh Connections and try again. No configuration was copied.";
            RefreshStatus();
            return;
        }
        var target = currentPeer.IsIncoming ? McpTarget.CurrentSession : McpTarget.FixedConnection;
        CopyText(McpConfiguration.Create(target, currentPeer.McpConnectionId), currentPeer.IsIncoming
            ? "MCP configuration copied for this Windows session. It still requires the same human permissions."
            : $"MCP configuration copied for {currentPeer.DisplayName}. This target is fixed and will never be replaced by another device.");
    }

    private void CopyText(string text, string status)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            Model.Notice = status;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        { Model.Notice = $"The clipboard is unavailable: {exception.Message}"; }
    }

    private void RefreshTick(DispatcherQueueTimer sender, object args)
    {
        RefreshStatus();
        if (ReferenceEquals(PageContent.Content, _activityView)) RefreshTasks();
    }
    private void RefreshStatus()
    {
        if (_isClosed) return;
        Model.Update(_session.Host.PeerConnections, _session.Host.IsRunning, _session.Host.LastTransportError,
            _session.Consent.HasDesktopLease, _session.Host.Connections, _session.Consent.DesktopAccessEnabled);
        _tray.Update($"Remote Agent Link · {(Model.HasApproval ? "Approval needed" : !Model.DesktopAccessEnabled ? "Agent desktop access blocked on this PC" : Model.Headline)}");
        _lifetime.TrayAvailable = _tray.Available;
    }
    private void StatusChanged() => Post(RefreshStatus);
    private void ShowRequested() => WindowActivation.Request(
        !_isClosed && !_safeToClose && !_lifetime.Quitting && !_lifetime.SessionEnding,
        () => DispatcherQueue.TryEnqueue(() => Show()));
    private void ConsentChanged() => Post(() =>
    {
        if (!_session.Consent.IsConnected) _active?.Answer.TrySetResult(false);
        RefreshStatus();
    });
    private void TrustChanged() => Post(() => { RefreshTrustedPeers(); RefreshStatus(); });
    private void UntrustedPeer(string fingerprint) => WriteLog($"Unverified channel identity: {fingerprint}. Compare the complete fingerprint before pairing.");
    private void WriteLog(string message) => Post(() =>
    {
        var existing = Model.ActivityLog;
        if (existing.Length > 32000) existing = existing[^16000..];
        Model.ActivityLog = $"{existing}{DateTime.Now:HH:mm:ss} {message}\n";
    });
    private void Post(Action action)
    {
        if (_isClosed) return;
        DispatcherQueue.TryEnqueue(() => { if (!_isClosed) action(); });
    }
    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested || _isClosed) { }
        catch (Exception exception)
        {
            if (_isClosed) return;
            Model.Notice = $"{exception.GetType().Name}: {exception.Message}";
            WriteLog(Model.Notice);
            Show();
        }
    }
}
