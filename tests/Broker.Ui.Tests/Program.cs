using AgentLink.Broker;
using AgentLink.Broker.ViewModels;
using AgentLink.Broker.Shell;
using AgentLink.Copilot;
using AgentLink.Core;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var lifetime = new ShellLifetime();
Check(lifetime.RequestClose() == CloseAction.Minimize, "No tray: preserve a taskbar control surface.");
Check(!lifetime.Hidden, "Minimized is not hidden.");
lifetime.TrayAvailable = true;
Check(lifetime.RequestClose() == CloseAction.Hide && lifetime.Hidden, "Close hides only with a tray.");
Check(lifetime.LoseTray() && !lifetime.Hidden, "Losing a hidden window's tray must reveal it.");
Check(!lifetime.LoseTray(), "Losing the tray while visible needs no redundant activation.");
lifetime.Quitting = true;
Check(lifetime.RequestClose() == CloseAction.Close, "Explicit quit can close.");
lifetime.Quitting = false;
lifetime.SessionEnding = true;
Check(lifetime.RequestClose() == CloseAction.Close, "Windows logoff must not be vetoed.");
lifetime.SessionEnding = false;
lifetime.ApprovalActive = true;
Check(lifetime.IsPrompting && lifetime.CanAllow, "A pending human approval protects operations.");
Check(!lifetime.TryReleaseApproval(false) && lifetime.ApprovalActive, "Visible protected surfaces cannot release approval.");
lifetime.EnterDialog();
lifetime.EnterDialog();
Check(lifetime.IsPrompting && !lifetime.CanAllow, "Nested modal UI blocks approval release.");
Check(!lifetime.TryReleaseApproval(true), "Even a hidden main window cannot release approval behind a protected dialog.");
lifetime.ExitDialog();
Check(lifetime.IsPrompting && !lifetime.CanAllow, "One remaining dialog still blocks release.");
lifetime.ExitDialog();
Check(lifetime.CanAllow, "Only after all dialogs close may a human decision be released.");
lifetime.SessionEnding = true;
Check(!lifetime.TryReleaseApproval(true), "Logoff cannot release a pending approval.");
lifetime.SessionEnding = false;
Check(lifetime.TryReleaseApproval(true) && !lifetime.IsPrompting, "Only hidden, non-modal UI can release the human decision.");
lifetime.ApprovalActive = true;
lifetime.Quitting = true;
Check(!lifetime.CanAllow, "Shutdown cannot release approval.");
lifetime.Quitting = false;
lifetime.ApprovalActive = false;
Check(!lifetime.IsPrompting && !lifetime.CanAllow, "No pending request cannot be approved.");
try { lifetime.ExitDialog(); throw new Exception("Dialog underflow accepted."); }
catch (InvalidOperationException) { checks++; }

var enqueueAttempts = 0;
Check(NativeWindow.IsAppearanceChange(0x0015), "System color changes refresh accessible caption colors.");
Check(NativeWindow.IsAppearanceChange(0x001A), "Settings broadcasts refresh high-contrast state.");
Check(NativeWindow.IsAppearanceChange(0x031A), "Theme changes refresh the desktop window appearance.");
Check(!NativeWindow.IsAppearanceChange(0x0011) && !NativeWindow.IsAppearanceChange(0x0016) &&
    !NativeWindow.IsAppearanceChange(0x0312) && !NativeWindow.IsAppearanceChange(0x02B1),
    "Appearance notifications do not replace shutdown, stop or session-lifetime handling.");
_ = NativeWindow.HighContrast;
checks++;
try
{
    WindowActivation.Request(false, () => { enqueueAttempts++; return true; });
    throw new Exception("Unavailable control surface accepted foreground activation.");
}
catch (InvalidOperationException) { checks++; }
Check(enqueueAttempts == 0, "An unavailable window must not enqueue foreground activation.");
try
{
    WindowActivation.Request(true, () => false);
    throw new Exception("Rejected dispatcher delivery reported success.");
}
catch (InvalidOperationException) { checks++; }
WindowActivation.Request(true, () => { enqueueAttempts++; return true; });
Check(enqueueAttempts == 1, "Available foreground activation is queued exactly once.");

const string privateDiagnosticText = @"private-prompt credential=do-not-log C:\private\customer-workspace";
Exception startupFailure;
try { throw new System.Runtime.InteropServices.COMException(privateDiagnosticText, unchecked((int)0x80070490)); }
catch (Exception exception) { startupFailure = exception; }
startupFailure.Data["private"] = privateDiagnosticText;
var startupReport = UiStartupDiagnostics.DescribeException(startupFailure);
Check(startupReport.Contains("System.Runtime.InteropServices.COMException") && startupReport.Contains("HRESULT=0x80070490"),
    "Startup diagnostics retain the actionable exception type and HRESULT.");
Check(!startupReport.Contains(privateDiagnosticText) && !startupReport.Contains("credential") &&
    !startupReport.Contains(@"C:\") && !startupReport.Contains(".cs:"),
    "Startup diagnostics exclude messages, data dictionaries and source paths.");
Exception deepFailure = startupFailure;
for (var depth = 0; depth < 20; depth++) deepFailure = new InvalidOperationException(privateDiagnosticText, deepFailure);
var boundedReport = UiStartupDiagnostics.DescribeException(deepFailure);
Check(boundedReport.Length <= UiStartupDiagnostics.MaxExceptionCharacters && boundedReport.Contains("inner-exceptions=truncated"),
    "Startup exception chains and report size are bounded.");

var diagnosticDirectory = Path.Combine(AppContext.BaseDirectory, "startup-diagnostics-" + Guid.NewGuid().ToString("N"));
var blockedDirectory = Path.Combine(diagnosticDirectory, "blocked");
var diagnosticLog = Path.Combine(diagnosticDirectory, "ui-startup.log");
Directory.CreateDirectory(diagnosticDirectory);
var originalError = Console.Error;
using var diagnosticError = new StringWriter();
Console.SetError(diagnosticError);
try
{
    var startup = new UiStartupDiagnostics(diagnosticDirectory);
    startup.Run(UiStartupPhase.AppInitializeComponent, () => { });
    Check(startup.CurrentPhase == UiStartupPhase.AppInitializeComponent, "Successful startup phases are recorded.");
    try
    {
        startup.Run(UiStartupPhase.MainWindowConstruct, () =>
            startup.Run(UiStartupPhase.MainWindowInitializeComponent, () => { throw startupFailure; }));
        throw new Exception("Startup failure was swallowed.");
    }
    catch (Exception exception) when (ReferenceEquals(exception, startupFailure)) { checks++; }
    Check(startup.FailurePhase == UiStartupPhase.MainWindowInitializeComponent,
        "Nested startup failures retain the innermost failing phase.");
    Check(startup.FailureSummary.Contains("0x80070490"), "The visible failure summary retains the original HRESULT.");
    var startupLog = File.ReadAllText(diagnosticLog);
    Check(startupLog.Contains("phase=MainWindowInitializeComponent") && startupLog.Contains("kind=failure") &&
        startupLog.Contains("mvid=") && startupLog.Contains("parent.ResourcesPri="),
        "Persistent diagnostics identify the build, payload presence and failing phase.");
    Check(!startupLog.Contains(privateDiagnosticText) && !diagnosticError.ToString().Contains(privateDiagnosticText),
        "Neither startup log nor stderr exposes exception payloads.");
    File.WriteAllText(diagnosticLog, new string('x', UiStartupDiagnostics.MaxLogBytes + 1));
    startup.Checkpoint(UiStartupPhase.MainWindowActivate);
    Check(new FileInfo(diagnosticLog).Length <= UiStartupDiagnostics.MaxLogBytes &&
        File.ReadAllText(diagnosticLog).Contains("phase=MainWindowActivate"),
        "An oversized diagnostic file is bounded without losing the latest checkpoint.");
    Directory.CreateDirectory(Path.Combine(blockedDirectory, "ui-startup.log"));
    var blockedStartup = new UiStartupDiagnostics(blockedDirectory);
    Check(!blockedStartup.LogAvailable, "An unavailable file sink is explicitly reported.");
    try
    {
        blockedStartup.Run(UiStartupPhase.MainWindowInitializeComponent, () => { throw startupFailure; });
        throw new Exception("A failed diagnostic sink swallowed the UI failure.");
    }
    catch (Exception exception) when (ReferenceEquals(exception, startupFailure)) { checks++; }
}
finally
{
    Console.SetError(originalError);
    File.Delete(diagnosticLog);
    if (Directory.Exists(Path.Combine(blockedDirectory, "ui-startup.log")))
        Directory.Delete(Path.Combine(blockedDirectory, "ui-startup.log"));
    if (Directory.Exists(blockedDirectory)) Directory.Delete(blockedDirectory);
    Directory.Delete(diagnosticDirectory);
}

foreach (var status in Enum.GetValues<PeerConnectionStatus>())
foreach (var here in new[] { false, true })
foreach (var there in new[] { false, true })
{
    var peer = new PeerViewModel(new("id", "Laptop", new string('A', 64), 2, PeerDirection.RemoteDesktop, status, here, there, DateTimeOffset.UtcNow));
    var ready = status == PeerConnectionStatus.Connected && here && there;
    Check(peer.IsReady == ready, "A channel is not operational without both trust directions.");
    Check(peer.StatusGlyph == (ready ? "\uE73E" : "\uE946"), "Success icon must match actual pairing state.");
    Check(peer.NeedsVerification == !here, "Local verification is distinct from remote verification.");
}
Check(PeerViewModel.FriendlyName("\u202Epretend\u0000\u2066").Equals("pretend", StringComparison.Ordinal), "Untrusted names cannot contain bidi/control formatting.");
Check(PeerViewModel.FriendlyName("") == "Windows device", "Missing friendly names have a useful fallback.");
Check(PeerViewModel.FriendlyName(new string('x', 500)).Length == 120, "Untrusted labels are bounded.");

var outgoing = new PeerConnectionSnapshot("transient-channel-id", "Desktop", new string('A', 64), 7,
    PeerDirection.RemoteDesktop, PeerConnectionStatus.Connected, true, true, DateTimeOffset.UnixEpoch);
var bound = new ConnectionInfo("actual-fingerprint:logon-connection-id", outgoing.Fingerprint, 7, "logon", "epoch");
Check(new PeerViewModel(outgoing, [bound]).McpConnectionId == bound.ConnectionId, "MCP binding uses the actual ConnectionInfo ID, not the transient peer ID.");
Check(new PeerViewModel(outgoing, [bound]).CanCopyMcpConfiguration, "A uniquely matched ready outgoing device offers contextual configuration.");
Check(new PeerViewModel(outgoing, [bound]).McpDetails.Contains(bound.ConnectionId) &&
    !new PeerViewModel(outgoing, [bound]).McpDetails.Contains(outgoing.Id), "Pairing details must not label a transient channel ID as an MCP binding ID.");
Check(!new PeerViewModel(outgoing).CanCopyMcpConfiguration, "Missing outgoing target cannot offer guessed configuration.");
Check(!new PeerViewModel(outgoing, [bound with { RemoteSessionId = 8 }]).CanCopyMcpConfiguration, "Target matching requires the exact remote session.");
Check(!new PeerViewModel(outgoing, [bound with { PeerFingerprint = new string('B', 64) }]).CanCopyMcpConfiguration, "Target matching requires the paired fingerprint.");
Check(!new PeerViewModel(outgoing, [bound, bound with { ConnectionId = "another-logon" }]).CanCopyMcpConfiguration, "Ambiguous targets must not be silently selected.");
Check(!new PeerViewModel(outgoing with { TrustedRemotely = false }, [bound]).CanCopyMcpConfiguration, "Remote trust is required before contextual binding.");
Check(!new PeerViewModel(outgoing with { TrustedLocally = false }, [bound]).CanCopyMcpConfiguration, "Local trust is required before contextual binding.");
Check(!new PeerViewModel(outgoing with { Status = PeerConnectionStatus.Connecting }, [bound]).CanCopyMcpConfiguration, "Connecting is not ready for contextual binding.");
var incoming = new PeerViewModel(outgoing with { Direction = PeerDirection.IncomingAgent }, [bound]);
Check(incoming.CanCopyMcpConfiguration && incoming.McpConnectionId is null && incoming.McpDetails.Contains("--current-session"),
    "Incoming connections target this desktop, never an outgoing connection ID.");
var unresolved = new PeerViewModel(outgoing with
{
    Fingerprint = "", SessionId = 0, Status = PeerConnectionStatus.Connecting,
    TrustedLocally = false, TrustedRemotely = false
});
Check(unresolved.Direction.Contains("Windows session unknown") && !unresolved.Direction.Contains("session 0"),
    "Unlearned session metadata is not described as the system's real Session 0.");
Check(!unresolved.CanReviewPairing && unresolved.ActionLabel == "Waiting for device identity",
    "Pairing is not offered before a certificate fingerprint is learned.");
Check(unresolved.ConnectedSince.StartsWith("Connection attempt started", StringComparison.Ordinal),
    "A connecting channel is not falsely described as already opened.");
var legacyIncoming = new PeerViewModel(outgoing with { Direction = PeerDirection.IncomingAgent, SessionId = 0 }, [bound]);
Check(legacyIncoming.IsReady && legacyIncoming.CanCopyMcpConfiguration && legacyIncoming.Direction.Contains("session unknown"),
    "Legacy incoming peers can use own-session configuration without inventing their remote session number.");
Check(!new PeerViewModel(outgoing with { SessionId = 0 }, [bound with { RemoteSessionId = 0 }]).CanCopyMcpConfiguration,
    "An unknown outgoing session cannot be resolved into an assumed fixed MCP target.");
string[] McpArguments(McpTarget target, string? id = null)
{
    using var document = JsonDocument.Parse(McpConfiguration.Create(target, id));
    var config = document.RootElement.GetProperty("mcpServers").GetProperty("remote-agent-link");
    Check(config.GetProperty("command").GetString() == "ralink-mcp.exe", "MCP configuration uses the installed supported alias.");
    return config.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
}
Check(McpArguments(McpTarget.Discover).Length == 0, "General setup leaves discovery unbound.");
Check(McpArguments(McpTarget.CurrentSession, bound.ConnectionId).SequenceEqual(["--current-session"]), "Current-session mode never combines mutually exclusive flags.");
Check(McpArguments(McpTarget.FixedConnection, bound.ConnectionId).SequenceEqual(["--connection", bound.ConnectionId]), "Fixed-target configuration preserves the actual binding ID exactly.");
try { McpConfiguration.Create(McpTarget.FixedConnection, " "); throw new Exception("Empty fixed target accepted."); }
catch (ArgumentException) { checks++; }

var shell = new ShellViewModel();
shell.Update([outgoing], true, null, false);
Check(!shell.Peers[0].CanCopyMcpConfiguration, "A channel can precede the actual MCP connection.");
shell.Update([outgoing], true, null, false, [bound]);
Check(shell.Peers[0].McpConnectionId == bound.ConnectionId, "A newly available binding refreshes the contextual action without channel identity changes.");
shell.Update([outgoing], true, null, false, [bound with { ConnectionId = "replacement-logon" }]);
Check(shell.Peers[0].McpConnectionId == "replacement-logon", "The current UI reflects changed actual binding IDs.");
shell.Update([], true, null, false);
Check(shell.IsEmpty && shell.Headline == "Ready when you are", "Waiting state is friendly and truthful.");
shell.Update([], false, null, false);
Check(shell.Headline == "Broker is not running", "Listener failures must not appear as readiness.");
shell.HasApproval = true;
shell.Update([], true, null, false);
Check(shell.Headline == "Your approval is needed", "Approval is visible on the dashboard.");
shell.HasApproval = false;
shell.Update([], true, "Channel unavailable", true);
Check(shell.Summary.Contains("Channel unavailable") && shell.DesktopStatus.Contains("active"), "Transport and permission states are independently reported.");
shell.Notice = "Copied.";
Check(shell.HasNotice, "Status announcements follow notices.");
shell.Notice = "";
Check(!shell.HasNotice, "Empty status notices are hidden.");
shell.Update([], true, null, false, desktopAccessEnabled: false);
Check(!shell.DesktopAccessEnabled && shell.DesktopStatus.Contains("Blocked on this PC"), "The off state explicitly identifies its local desktop scope.");
shell.Update([], true, null, false, desktopAccessEnabled: true);
Check(shell.DesktopAccessEnabled && shell.DesktopStatus.Contains("needs your approval"), "Enabling requests does not claim a control grant.");
shell.DesktopAccessEnabled = false;
shell.ShowDesktopBlockNotice();
Check(shell.HasNotice && shell.Notice.Contains("desktop access"), "The tray notice explicitly identifies desktop access.");
shell.Update([], true, null, false, desktopAccessEnabled: false);
Check(shell.HasNotice, "An unchanged blocked state retains its notice.");
shell.Update([], true, null, false, desktopAccessEnabled: true);
Check(!shell.HasNotice && shell.Notice.Length == 0, "Re-enabling clears the obsolete tray block notice.");
shell.DesktopAccessEnabled = false;
shell.ShowDesktopBlockNotice();
shell.Notice = "An unrelated operation failed.";
shell.DesktopAccessEnabled = true;
Check(shell.HasNotice && shell.Notice == "An unrelated operation failed.", "Re-enabling must not hide a replacement error.");
shell.Notice = "";
shell.DesktopAccessEnabled = false;
shell.ShowDesktopBlockNotice();
shell.Notice = "";
shell.DesktopAccessEnabled = true;
Check(!shell.HasNotice, "A dismissed block notice does not return after re-enabling.");

var mcpSetupDirectory = Path.Combine(AppContext.BaseDirectory, "mcp-setup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(mcpSetupDirectory);
var mcpSetupPath = Path.Combine(mcpSetupDirectory, "mcp-config.json");
const string aliasPath = @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\ralink-mcp.exe";
try
{
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Missing, "An absent client config is not a running MCP server.");
    File.WriteAllText(mcpSetupPath, McpConfiguration.Create(McpTarget.Discover));
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Saved, "The supported bare alias is recognized.");
    File.WriteAllText(mcpSetupPath, JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
    {
        ["remote-agent-link"] = new { type = "stdio", command = aliasPath, args = Array.Empty<string>() },
        ["other"] = new { token = "do-not-disclose-or-change" }
    }}));
    var originalConfig = File.ReadAllText(mcpSetupPath);
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Saved, "The absolute execution alias and stdio type are recognized.");
    Check(File.ReadAllText(mcpSetupPath) == originalConfig, "Setup inspection never rewrites other server settings.");
    File.WriteAllText(mcpSetupPath, McpConfiguration.Create(McpTarget.CurrentSession));
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Custom, "A different target is never silently retargeted.");
    File.WriteAllText(mcpSetupPath, """{"mcpServers":{"remote-agent-link":{"type":"local","command":"other.exe","args":[]}}}""");
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Custom, "A name collision preserves the existing command.");
    foreach (var invalid in new[] { "[", "[]", """{"mcpServers":null}""", """{"mcpServers":{"remote-agent-link":null}}""" })
    {
        File.WriteAllText(mcpSetupPath, invalid);
        Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Invalid, "Invalid config is not an invitation to overwrite it.");
    }
    File.WriteAllText(mcpSetupPath, new string(' ', 1024 * 1024 + 1));
    Check(McpSetup.Inspect(mcpSetupPath, aliasPath) == McpRegistration.Invalid, "Client config inspection is bounded.");
}
finally { File.Delete(mcpSetupPath); Directory.Delete(mcpSetupDirectory); }

var activity = new ActivityViewModel();
var refreshes = 0;
activity.RefreshRequested += () => refreshes++;
var grant = new TaskGrant(@"C:\workspace", true, false, [], false, 30, null);
AgentTask TaskRow(int id, string state = "running") => new(id.ToString("D4"), "session", "key", "fingerprint", "prompt",
    state, grant, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
activity.Update(Enumerable.Range(0, 100).Select(id => TaskRow(id)).ToArray());
Check(activity.HasTasks && activity.HasOlder && activity.Tasks.Count == 100, "A full page offers older tasks and details.");
activity.Selected = activity.Tasks[4];
activity.Update(Enumerable.Range(0, 100).Select(id => TaskRow(id)).ToArray());
Check(activity.Selected?.TaskId == "0004" && activity.CanCancel, "Refresh preserves selected task.");
activity.Older();
Check(activity.BeforeTaskId == "0099" && refreshes == 1, "Paging uses the last returned task ID.");
activity.ActiveOnly = true;
Check(activity.BeforeTaskId is null && refreshes == 2, "Changing filters resets pagination.");
activity.Update([TaskRow(4, "completed")]);
Check(!activity.HasOlder && !activity.CanCancel, "Terminal tasks are not offered cancellation.");
activity.ShowDetails(TaskRow(4, "completed") with { CancellationRequested = true, Result = "finished", ResultsExpired = true, ReconciliationAcknowledged = true });
Check(activity.Details.Contains("Cancellation requested: True") && activity.Details.Contains("finished") &&
    activity.Details.Contains("Result retention expired: True") && activity.Details.Contains("Reconciliation acknowledged: True"),
    "Task detail safety and outcome fields are retained.");
activity.Update([]);
Check(activity.IsEmpty && !activity.HasTasks && activity.Selected is null, "Empty history cannot leave a stale cancellation target or empty details editor.");
foreach (var terminal in new[] { "completed", "failed", "cancelled", "interrupted", "outcome_unknown" })
    Check(ActivityViewModel.IsTerminal(terminal), "All backend terminal states are recognized.");
Check(!ActivityViewModel.IsTerminal("cancelling"), "Cancellation request is not confused with terminal outcome.");

PluginLoadingTests.Run(Check);

Console.WriteLine($"All {checks} broker presentation/lifecycle assertions passed (no UI, trust or live broker modified).");
