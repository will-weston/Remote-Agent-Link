using AgentLink.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AgentLink.Copilot;

public sealed class OfficialSdkRuntimeFactory(string dataDirectory) : ICopilotRuntimeFactory
{
    public const string SdkVersion = "1.0.11";
    public const string RequiredRuntimeVersion = "1.0.79";
    public const string LiveCompatibilityRequirements =
        "Authenticated same-logon registration; non-mutating session attach; atomic exclusive turn handoff/busy rejection; " +
        "per-turn cancellation; existing permission-policy preservation; connection-only cleanup preserving external sessions and runtime.";

    public async Task<IAgentRuntimeSession> OpenManagedAsync(
        AgentSession session, bool resume, RuntimeCallbacks callbacks, CancellationToken cancellationToken)
    {
        using var store = new TaskStore(LocalSetup.EnsurePrivateDirectory(dataDirectory));
        var settings = store.GetSetting<ManagedRuntimeSettings>("managed-runtime")
            ?? throw new AgentLinkException("runtime_not_configured", "A local human must configure the installed Copilot runtime executable.");
        var path = PathPolicy.Canonical(settings.ExecutablePath);
        if (!File.Exists(path))
            throw new AgentLinkException("runtime_not_configured", "The explicitly configured runtime executable is missing.");
        var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(path: path),
            WorkingDirectory = session.WorkingDirectory,
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.Error
        });
        var adapter = new SdkSession(client, callbacks);
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Version != RequiredRuntimeVersion || status.ProtocolVersion < 3)
                throw new AgentLinkException("incompatible_runtime",
                    $"SDK {SdkVersion} requires its pinned runtime {RequiredRuntimeVersion}; no mutable runtime is downloaded.");
            if (resume)
            {
                var sessions = await client.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!sessions.Any(item => item.SessionId == session.SessionId))
                    throw new AgentLinkException("session_not_found", "The broker-owned SDK session is no longer available.");
                var config = new ResumeSessionConfig { ContinuePendingWork = false };
                adapter.Configure(config, session.WorkingDirectory);
                adapter.Bind(await client.ResumeSessionAsync(session.SessionId, config, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                var config = new SessionConfig { SessionId = session.SessionId };
                adapter.Configure(config, session.WorkingDirectory);
                adapter.Bind(await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false));
            }
            return adapter;
        }
        catch (Exception error)
        {
            try { await adapter.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
            if (error is AgentLinkException or OperationCanceledException) throw;
            throw new AgentLinkException("runtime_unavailable",
                "The configured Copilot runtime could not initialize. Inspect its logs locally; SDK diagnostics are not forwarded.");
        }
    }

    public Task<IAgentRuntimeSession> AttachAsync(
        LiveRuntimeRegistration registration, RuntimeCallbacks callbacks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Resume changes options, AbortAsync is session-wide, and session disposal
        // sends session.destroy. Authentication alone is not exclusive turn ownership.
        throw new AgentLinkException("incompatible_runtime",
            $"SDK {SdkVersion} cannot safely attach to a user-owned interactive turn. Required: {LiveCompatibilityRequirements}");
    }

    private sealed class SdkSession(CopilotClient client, RuntimeCallbacks callbacks) : IAgentRuntimeSession
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private CopilotSession? _session;
        private IDisposable? _subscription;
        private bool _busy;
        private string? _messageId;
        private string? _answer;
        private bool _disposed;
        public bool IsExternal => false;

        internal void Configure(SessionConfigBase config, string workingDirectory)
        {
            config.WorkingDirectory = workingDirectory;
            config.ClientName = "Remote Agent Link";
            config.Streaming = false;
            config.AvailableTools = ["view", "glob", "rg", "powershell", "apply_patch", "ask_user"];
            config.SkipCustomInstructions = true;
            config.EnableConfigDiscovery = false;
            config.EnableFileHooks = false;
            config.EnableHostGitOperations = false;
            config.EnableSessionStore = false;
            config.EnableSkills = false;
            config.ManageScheduleEnabled = false;
            config.OnPermissionRequest = async (request, invocation) =>
            {
                var permission = ToPermission(request, invocation.ManagedSettingsEnabled);
                var allowed = await callbacks.Permission(permission, _lifetime.Token).ConfigureAwait(false);
#pragma warning disable GHCP001
                return allowed ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject();
#pragma warning restore GHCP001
            };
            config.OnUserInputRequest = async (request, _) =>
            {
                var choices = request.Choices?.ToArray() ?? [];
                var answer = await callbacks.Question(new(request.Question, choices, request.AllowFreeform ?? true),
                    _lifetime.Token).ConfigureAwait(false);
                return new UserInputResponse { Answer = answer, WasFreeform = !choices.Contains(answer, StringComparer.Ordinal) };
            };
            config.OnElicitationRequest = async context =>
            {
                // Arbitrary MCP forms/URL challenges may request credentials or approval, not ordinary answers.
                await callbacks.Permission(new RuntimePermission("elicitation",
                    "A local-only elicitation requires attention; remote credential and authorization forms are disabled.",
                    RequiresDesktop: context.Mode?.ToString() == "url"), _lifetime.Token).ConfigureAwait(false);
#pragma warning disable GHCP001
                return new ElicitationResult { Action = UIElicitationResponseAction.Cancel };
#pragma warning restore GHCP001
            };
        }

        private static RuntimePermission ToPermission(PermissionRequest request, bool managedSettings) => request switch
        {
            PermissionRequestRead read => new("read", $"Read {read.Path}", Path: read.Path,
                ManagedApprovalRequired: read.ManagedApprovalRequired == true,
                SandboxBypass: read.RequestSandboxBypass == true),
            PermissionRequestWrite write => new("write", $"Write {write.FileName}", Path: write.FileName,
                ManagedApprovalRequired: write.ManagedApprovalRequired == true,
                SandboxBypass: write.RequestSandboxBypass == true),
            PermissionRequestShell shell => new("shell", shell.FullCommandText, Command: shell.FullCommandText,
                PossiblePaths: shell.PossiblePaths, ManagedApprovalRequired: shell.ManagedApprovalRequired == true,
                SandboxBypass: shell.RequestSandboxBypass == true, HasRedirection: shell.HasWriteFileRedirection,
                HasUrls: shell.PossibleUrls.Length > 0),
            _ => new(request.Kind, $"Copilot requests a {request.Kind} permission.",
                ManagedApprovalRequired: managedSettings || request.ManagedApprovalRequired == true)
        };

        internal void Bind(CopilotSession session)
        {
            _session = session;
            _subscription = session.On<SessionEvent>(OnEvent);
        }

        private void OnEvent(SessionEvent value)
        {
            lock (_gate)
            {
                if (!_busy) return;
                switch (value)
                {
                    case AssistantMessageEvent message when message.Data.Phase is null or "final" or "final_answer":
                        _answer = Limit(message.Data.Content, 65536);
                        break;
                    case ToolExecutionStartEvent tool:
                        callbacks.Event(new("tool_summary", $"Started {Limit(tool.Data.ToolName, 128)}"));
                        break;
                    case ToolExecutionCompleteEvent tool:
                        callbacks.Event(new("tool_summary", tool.Data.Success ? "Tool completed." : "Tool failed."));
                        break;
                    case SessionErrorEvent:
                        callbacks.Event(new("error", "The runtime reported an error; consult the local runtime."));
                        break;
                    case SessionIdleEvent idle:
                        _busy = false;
                        if (_answer is not null) callbacks.Event(new("final", _answer));
                        callbacks.Event(new(idle.Data.Aborted == true ? "aborted" : "idle"));
                        break;
                    case AssistantUsageEvent usage when usage.Data.CopilotUsage is { } cost:
                        callbacks.Event(new("usage", EventId: value.Id.ToString(), NanoAiu: cost.TotalNanoAiu));
                        break;
                    case SessionLimitsExhaustedRequestedEvent limit:
                        callbacks.Event(new("budget_exhausted", "The approved soft AI-credit limit was reached.",
                            UsedAiCredits: limit.Data.UsedAiCredits));
                        break;
                    default:
                        break;
                }
            }
        }

        public Task<bool> IsIdleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) return Task.FromResult(!_busy);
        }

        public async Task PrepareAsync(double? maxAiCredits, CancellationToken cancellationToken)
        {
            if (maxAiCredits is null) return;
            try
            {
                // Pinned SDK exposes session accounting-window limits via this experimental typed API.
#pragma warning disable GHCP001
                await _session!.Rpc.Options.UpdateAsync(
                    sessionLimits: new SessionLimitsConfig { MaxAiCredits = maxAiCredits },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore GHCP001
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AgentLinkException("incompatible_runtime", "The runtime did not accept the required session credit limit.");
            }
        }

        public async Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_busy) throw new AgentLinkException("session_busy", "An agent turn is already running.");
                _busy = true;
                _answer = null;
                _messageId = null;
            }
            var id = await _session!.SendAsync(new MessageOptions { Prompt = prompt, Mode = "enqueue" },
                cancellationToken).ConfigureAwait(false);
            lock (_gate) _messageId = id;
            return id;
        }

        public async Task<bool> AbortOwnedTurnAsync(string messageId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_busy) return true;
                if (_messageId != messageId) return false;
            }
            await _session!.AbortAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _subscription?.Dispose();
            await _lifetime.CancelAsync().ConfigureAwait(false);
            // This adapter only owns spawned runtimes. Never use this disposal path for ForUri.
            await client.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
