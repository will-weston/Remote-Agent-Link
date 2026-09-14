using AgentLink.Core;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentLink.Copilot;

public sealed class CopilotOperations : IOperationHandler, IAsyncDisposable
{
    private const string Capability = "agent-delegation";
    private readonly IConsentService _consent;
    private readonly string _directory;
    private readonly ICopilotRuntimeFactory _factory;
    private readonly TaskStore _store;
    private readonly FileStream _writerLease;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, SessionHandle> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionLifecycle = new(1);
    private readonly object _connectionGate = new();
    private readonly object _invocationGate = new();
    private readonly TaskCompletionSource _invocationsDrained = NewSignal();
    private int _invocations;
    private TaskCompletionSource _connectionAvailable = NewSignal();
    private CancellationTokenSource _connectionEpoch = new();
    private bool _connected;
    private bool _deviceAccessEnabled;
    private readonly HashSet<string> _disconnectedPrincipals = new(StringComparer.Ordinal);
    private int _disposed;

    public CopilotOperations(IConsentService consent, string dataDirectory)
        : this(consent, dataDirectory, new OfficialSdkRuntimeFactory(dataDirectory)) { }

    public CopilotOperations(IConsentService consent, string dataDirectory, ICopilotRuntimeFactory runtimeFactory)
    {
        _consent = consent;
        _factory = runtimeFactory;
        _directory = LocalSetup.EnsurePrivateDirectory(dataDirectory);
        try { _writerLease = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AgentLinkException("broker_already_running", "A task writer already owns this Copilot data directory."); }
        try
        {
            _store = new TaskStore(_directory);
            _store.Recover();
        }
        catch { _writerLease.Dispose(); throw; }
        _connected = consent.IsConnected;
        _deviceAccessEnabled = consent.DeviceAccessEnabled;
        if (_connected) _connectionAvailable.TrySetResult();
    }

    public IReadOnlyList<ToolDefinition> Tools => AgentToolCatalog.All;

    /// <summary>Local-human Control UI only: lists this Windows user's tasks across authenticated peers, even offline.</summary>
    public IReadOnlyList<AgentTask> ListLocalTasks(int limit = 100, bool activeOnly = false, string? beforeTaskId = null)
    {
        EnterInvocation();
        try
        {
            if (limit is < 1 or > 200)
                throw new AgentLinkException("invalid_arguments", "Local task page limit must be 1–200.");
            if (beforeTaskId is not null) ReadLocalTask(beforeTaskId);
            return _store.LocalTasks(limit, activeOnly, beforeTaskId);
        }
        finally { ExitInvocation(); }
    }

    /// <summary>Local-human Control UI only. Does not require a connected peer or a new delegation grant.</summary>
    public AgentTask GetLocalTask(string taskId)
    {
        EnterInvocation();
        try { return ReadLocalTask(taskId); }
        finally { ExitInvocation(); }
    }

    /// <summary>Local-human Control UI only: records a cancellation request; poll the task for the actual outcome.</summary>
    public AgentTask CancelLocalTask(string taskId)
    {
        EnterInvocation();
        try { return RequestCancellation(ReadLocalTask(taskId), "local-control"); }
        finally { ExitInvocation(); }
    }

    public async Task<OperationResult> InvokeAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        EnterInvocation();
        var previousActor = OperationContext.ActorId.Value;
        try
        {
            EnsureDeviceAccess();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token,
                _consent.DeviceAccessLifetime);
            var ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            var owner = Arguments.Required(request.ActorId ?? OperationContext.ActorId.Value ?? "local", "actorId", 256);
            OperationContext.ActorId.Value = owner;
            return request.Method switch
            {
                "agent_list_sessions" => List(owner, request.Arguments),
                "agent_start_session" => await StartAsync(owner, Arguments.Read<StartArguments>(request.Arguments), ct).ConfigureAwait(false),
                "agent_attach_session" => await AttachAsync(owner, Arguments.Read<AttachArguments>(request.Arguments), ct).ConfigureAwait(false),
                "agent_detach_session" => await DetachAsync(owner, Arguments.Read<SessionArguments>(request.Arguments), ct).ConfigureAwait(false),
                "agent_send" => await SendAsync(owner, Arguments.Read<SendArguments>(request.Arguments), ct).ConfigureAwait(false),
                "agent_get_task" => Result(PublicTask(OwnedTask(owner, Arguments.Read<TaskArguments>(request.Arguments).TaskId))),
                "agent_read_events" => ReadEvents(owner, Arguments.Read<ReadArguments>(request.Arguments)),
                "agent_cancel_task" => Cancel(owner, Arguments.Read<TaskArguments>(request.Arguments)),
                "agent_answer_question" => Answer(owner, Arguments.Read<AnswerArguments>(request.Arguments)),
                _ => throw new AgentLinkException("unknown_method", "Unknown agent operation.")
            };
        }
        finally
        {
            OperationContext.ActorId.Value = previousActor;
            ExitInvocation();
        }
    }

    public void OnConnectionChanged(bool connected)
    {
        lock (_connectionGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_connected == connected) return;
            _connected = connected;
            if (connected)
            {
                _connectionEpoch.Dispose();
                _connectionEpoch = new CancellationTokenSource();
                _connectionAvailable.TrySetResult();
            }
            else
            {
                _connectionEpoch.Cancel();
                _connectionAvailable = NewSignal();
                foreach (var worker in _workers.Values.Where(w => !w.Task.Grant.Unattended))
                    worker.Cancel("connection_lost");
            }
        }
    }

    public void OnDeviceAccessChanged(bool enabled)
    {
        lock (_connectionGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _deviceAccessEnabled = enabled;
            if (!enabled)
                foreach (var worker in _workers.Values)
                {
                    _store.Update(worker.Task.TaskId, task => task with { CancellationRequested = true },
                        "cancellation_requested", new { deliveredToBroker = true, requestedBy = "device-access" });
                    worker.Cancel("device_access_disabled");
                }
        }
    }

    public void OnPrincipalDisconnected(string principal)
    {
        lock (_connectionGate)
        {
            _disconnectedPrincipals.Add(principal);
            foreach (var handle in _sessions.Values.Where(handle => handle.Session.Owner == principal))
                if (handle.Worker is { Task.Grant.Unattended: false } worker)
                    worker.Cancel("connection_lost");
        }
    }

    public void OnPrincipalConnected(string principal)
    {
        lock (_connectionGate) _disconnectedPrincipals.Remove(principal);
    }

    private void EnsureDeviceAccess()
    {
        lock (_connectionGate)
            if (!_deviceAccessEnabled || !_consent.DeviceAccessEnabled)
                throw new AgentLinkException("device_access_disabled", "Allow agents to use this device is turned off.");
    }

    private bool Connected { get { lock (_connectionGate) return _connected && _consent.IsConnected; } }

    private OperationResult List(string owner, JsonElement arguments)
    {
        Arguments.Read<EmptyArguments>(arguments);
        _store.Prune(DateTimeOffset.UtcNow);
        return Result(new
        {
            sessions = _store.Sessions(owner).Take(200),
            liveCompatibilityRequirements = OfficialSdkRuntimeFactory.LiveCompatibilityRequirements,
            sdkVersion = OfficialSdkRuntimeFactory.SdkVersion,
            requiredRuntimeVersion = OfficialSdkRuntimeFactory.RequiredRuntimeVersion
        });
    }

    private async Task<OperationResult> StartAsync(string owner, StartArguments arguments, CancellationToken ct)
    {
        await _sessionLifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool resume = arguments.SessionId is not null;
            var session = resume ? OwnedSession(owner, arguments.SessionId!) :
                new AgentSession(Guid.NewGuid().ToString("N"),
                    PathPolicy.Workspace(Arguments.Required(arguments.WorkingDirectory, "workingDirectory"), _directory), owner);
            if (session.Kind != "managed")
                throw new AgentLinkException("invalid_session", "Use explicit registered-runtime attachment for live sessions.");
            if (session.RequiresReconciliation)
                throw new AgentLinkException("outcome_unknown", "A local human must reconcile the previous runtime turn before this session can resume.");
            if (arguments.WorkingDirectory is not null &&
                !PathPolicy.Workspace(arguments.WorkingDirectory, _directory).Equals(session.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                throw new AgentLinkException("invalid_path", "Resume cannot change the session's working directory.");
            PathPolicy.Workspace(session.WorkingDirectory, _directory);
            if (_sessions.ContainsKey(session.SessionId)) return Result(session);
            await RequireConsentAsync(new(Capability, $"{(resume ? "Resume" : "Create")} broker-managed agent session in {session.WorkingDirectory}.",
                session.WorkingDirectory), ct).ConfigureAwait(false);
            var handle = new SessionHandle(session);
            handle.Runtime = await _factory.OpenManagedAsync(session, resume, Callbacks(handle), ct).ConfigureAwait(false);
            try
            {
                if (handle.Runtime.IsExternal)
                    throw new AgentLinkException("incompatible_runtime", "The managed adapter did not return an owned runtime.");
                ct.ThrowIfCancellationRequested();
                EnsureDeviceAccess();
                session = session with { Attached = true };
                handle.Session = session;
                session = _store.SaveSession(session);
                handle.Session = session;
                _sessions[session.SessionId] = handle;
                return Result(session);
            }
            catch
            {
                await handle.Runtime.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _sessionLifecycle.Release(); }
    }

    private async Task<OperationResult> AttachAsync(string owner, AttachArguments arguments, CancellationToken ct)
    {
        Arguments.Required(arguments.RegistrationId, "registrationId", 128);
        Arguments.Required(arguments.SessionId, "sessionId", 128);
        await _sessionLifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var registration = LocalSetup.ReadRegistration(_store, arguments.RegistrationId);
            if (registration.SessionId != arguments.SessionId)
                throw new AgentLinkException("invalid_registration", "Select the session explicitly registered by the local human.");
            var previous = _store.Session(arguments.SessionId);
            if (previous is not null && (previous.Owner != owner || previous.Kind != "live" ||
                previous.RegistrationId != registration.RegistrationId))
                throw new AgentLinkException("session_conflict", "This session identity is already owned by another registration.");
            if (previous?.RequiresReconciliation == true)
                throw new AgentLinkException("outcome_unknown", "A local human must reconcile the previous turn.");
            if (_sessions.TryGetValue(arguments.SessionId, out var existing)) return Result(existing.Session);
            var workspace = PathPolicy.Workspace(registration.WorkingDirectory, _directory);
            await RequireConsentAsync(new(Capability,
                $"Attach to explicitly registered interactive session {registration.SessionId}; preserve its existing permissions and foreground.",
                workspace), ct).ConfigureAwait(false);
            var session = new AgentSession(registration.SessionId, workspace, owner, "live", registration.RegistrationId);
            var handle = new SessionHandle(session);
            handle.Runtime = await _factory.AttachAsync(registration, Callbacks(handle), ct).ConfigureAwait(false);
            try
            {
                if (!handle.Runtime.IsExternal || !await handle.Runtime.IsIdleAsync(ct).ConfigureAwait(false))
                    throw new AgentLinkException("session_busy", "The interactive session is not available for exclusive delegated work.");
                ct.ThrowIfCancellationRequested();
                EnsureDeviceAccess();
                session = _store.SaveSession(session);
                _sessions[session.SessionId] = handle;
                return Result(session);
            }
            catch
            {
                await handle.Runtime.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _sessionLifecycle.Release(); }
    }

    private async Task<OperationResult> DetachAsync(string owner, SessionArguments arguments, CancellationToken ct)
    {
        var session = OwnedSession(owner, arguments.SessionId);
        var gate = SessionLock(session.SessionId);
        await _sessionLifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_store.Busy(session.SessionId))
                    throw new AgentLinkException("session_busy", "Cancel and reconcile the delegated task before detaching.");
                if (_sessions.TryRemove(session.SessionId, out var handle))
                    await handle.Runtime!.DisposeAsync().ConfigureAwait(false);
                session = session with { Attached = false };
                _store.SaveSession(session);
                return Result(session);
            }
            finally { gate.Release(); }
        }
        finally { _sessionLifecycle.Release(); }
    }

    private async Task<OperationResult> SendAsync(string owner, SendArguments arguments, CancellationToken ct)
    {
        var session = OwnedSession(owner, arguments.SessionId);
        string prompt = Arguments.Required(arguments.Prompt, "prompt");
        string key = Arguments.Required(arguments.IdempotencyKey, "idempotencyKey", 128);
        if (arguments.TimeoutSeconds is < 1 or > 86400 || arguments.MaxAiCredits is { } budget && (!double.IsFinite(budget) || budget <= 0) ||
            arguments.Unattended && arguments.MaxAiCredits is null)
            throw new AgentLinkException("invalid_arguments", "Use a finite timeout (1–86400 seconds) and a positive explicit AI-credit budget for unattended work.");
        var workspace = PathPolicy.Workspace(arguments.WorkingDirectory ?? session.WorkingDirectory, _directory);
        if (!workspace.Equals(session.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            throw new AgentLinkException("invalid_path", "The task working directory must match the explicitly granted session directory.");
        var commands = arguments.ApprovedCommands ?? [];
        if (commands.Length > 16 || commands.Any(command => command is null || !PathPolicy.SafeCommand(command)))
            throw new AgentLinkException("invalid_arguments", "Only explicit non-UI build/read commands without shell expressions can be proposed for preauthorization.");
        var grant = new TaskGrant(workspace, arguments.AllowWorkspaceRead, arguments.AllowWorkspaceWrite,
            commands.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            arguments.Unattended, arguments.TimeoutSeconds, arguments.MaxAiCredits);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { prompt, grant }, Json.Options))));
        var gate = SessionLock(session.SessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.Find(session.SessionId, key) is { } prior)
            {
                if (prior.Fingerprint != fingerprint)
                    throw new AgentLinkException("idempotency_conflict", "The idempotency key was already used for a different task or grant.");
                return Result(PublicTask(prior));
            }
            if (!_sessions.TryGetValue(session.SessionId, out var handle) || !session.Attached)
                throw new AgentLinkException("session_detached", "Explicitly start/resume or attach this session first.");
            if (handle.Session.RequiresReconciliation)
                throw new AgentLinkException("outcome_unknown", "The previous turn needs local reconciliation.");
            if (_store.Busy(session.SessionId) || handle.Worker is not null || !await handle.Runtime!.IsIdleAsync(ct).ConfigureAwait(false))
                throw new AgentLinkException("session_busy", "A delegated or interactive turn already owns this session.");
            string summary = $"Delegate this task:\n{prompt}\nWorkspace: {workspace}\n" +
                $"Workspace read: {grant.AllowWorkspaceRead}; write: {grant.AllowWorkspaceWrite}.\n" +
                $"Exact non-UI commands: {string.Join("; ", grant.ApprovedCommands)}.\n" +
                $"Timeout: {grant.TimeoutSeconds}s. AI-credit soft limit: {grant.MaxAiCredits?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not requested"}.\n" +
                "Trusted workspace build scripts can execute code; this is not an OS sandbox.";
            await RequireConsentAsync(new(Capability, summary, workspace, grant.Unattended), ct).ConfigureAwait(false);
            if (grant.Unattended)
                await RequireConsentAsync(new("unattended-execution", $"Continue non-UI work during RDP disconnection.\n{summary}",
                    workspace, true), ct).ConfigureAwait(false);
            if (!await handle.Runtime.IsIdleAsync(ct).ConfigureAwait(false))
                throw new AgentLinkException("session_busy", "The interactive session became busy before acceptance.");
            EnsureConnected();
            ct.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var task = new AgentTask(Guid.NewGuid().ToString("N"), session.SessionId, key, fingerprint, prompt,
                "accepted", grant, now, now.AddSeconds(grant.TimeoutSeconds));
            lock (_connectionGate)
            {
                EnsureDeviceAccess();
                if (_disconnectedPrincipals.Contains(owner))
                    throw new AgentLinkException("disconnected", "The authenticated endpoint disconnected before task acceptance.");
                ct.ThrowIfCancellationRequested();
                // Capture and register the epoch before committing. No during the
                // transaction must cancel a worker, never strand an accepted row.
                var worker = new Worker(task, _lifetime.Token, _consent.DeviceAccessLifetime);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _store.Accept(task);
                }
                catch
                {
                    worker.Dispose();
                    throw;
                }
                handle.Worker = worker;
                _workers[task.TaskId] = worker;
                worker.Execution = Task.Run(() => RunAsync(handle, worker), CancellationToken.None);
            }
            return Result(PublicTask(task));
        }
        finally { gate.Release(); }
    }

    private async Task RunAsync(SessionHandle handle, Worker worker)
    {
        var ct = worker.Token;
        try
        {
            if (_store.Task(worker.Task.TaskId)?.CancellationRequested == true) worker.Cancel("cancelled");
            if (!worker.Task.Grant.Unattended && !Connected) worker.Cancel("connection_lost");
            ct.ThrowIfCancellationRequested();
            await handle.Runtime!.PrepareAsync(worker.Task.Grant.MaxAiCredits, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _store.Update(worker.Task.TaskId, task => task with { State = "running", DispatchStarted = true });
            var messageId = await handle.Runtime.SendAsync(worker.Task.Prompt, ct).WaitAsync(ct).ConfigureAwait(false);
            _store.Update(worker.Task.TaskId, task => task with { MessageId = messageId }, "dispatched", new { messageId });
            var outcome = await worker.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            if (outcome == "error")
                throw new AgentLinkException("runtime_error", "The runtime reported an error.");
            _store.Update(worker.Task.TaskId, task => task with
            {
                State = outcome == "aborted" ? "cancelled" : "completed",
                ErrorCode = outcome == "aborted" ? "runtime_aborted" : null
            });
        }
        catch (Exception ex)
        {
            string code = ex is OperationCanceledException
                ? worker.CancelReason ?? (_lifetime.IsCancellationRequested ? "broker_stopping" : "timeout")
                : ex is AgentLinkException known ? known.Code : "runtime_error";
            var saved = _store.Task(worker.Task.TaskId)!;
            bool knownStopped = !saved.DispatchStarted;
            if (saved.MessageId is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    knownStopped = await handle.Runtime!.AbortOwnedTurnAsync(saved.MessageId, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) { knownStopped = false; }
            }
            string state = !knownStopped ? "outcome_unknown" :
                code is "connection_lost" or "broker_stopping" ? "interrupted" :
                code is "cancelled" or "device_access_disabled" ? "cancelled" : "failed";
            _store.Update(saved.TaskId, task => task with
            {
                State = state, ErrorCode = code,
                CancellationRequested = task.CancellationRequested || code == "device_access_disabled"
            });
            if (!knownStopped)
            {
                handle.Session = handle.Session with { RequiresReconciliation = true };
            }
        }
        finally
        {
            worker.PendingQuestion?.Answer.TrySetCanceled();
            handle.Worker = null;
            _workers.TryRemove(worker.Task.TaskId, out _);
            worker.Dispose();
        }
    }

    private RuntimeCallbacks Callbacks(SessionHandle session) => new(
        (permission, _) => session.Worker is { } worker ? PermissionAsync(worker, permission) : Task.FromResult(false),
        (question, _) => session.Worker is { } worker ? QuestionAsync(worker, question) :
            Task.FromException<string>(new AgentLinkException("no_active_task", "No delegated task owns this question.")),
        value =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (session.Worker is not { } worker) return;
            switch (value.Kind)
            {
                case "final":
                    _store.Update(worker.Task.TaskId, task => task with { Result = Limit(value.Text ?? "", 65536) },
                        "final_answer", new { text = Limit(value.Text ?? "", 65536) });
                    break;
                case "tool_summary":
                    _store.Append(worker.Task.TaskId, "tool_summary", new { text = Limit(value.Text ?? "", 8192) });
                    break;
                case "idle":
                case "aborted":
                case "error":
                    worker.Completion.TrySetResult(value.Kind);
                    break;
                case "budget_exhausted":
                    if (value.UsedAiCredits is { } credits && double.IsFinite(credits) && credits >= 0)
                        _store.Update(worker.Task.TaskId, task => task with { CreditWindowUsed = credits }, "budget_usage", new { usedAiCredits = credits });
                    worker.Cancel("budget_exhausted");
                    break;
                case "usage" when value.EventId is not null && value.NanoAiu is { } amount:
                    _store.RecordUsage(worker.Task.TaskId, value.EventId, amount);
                    break;
            }
        });

    private async Task<bool> PermissionAsync(Worker worker, RuntimePermission permission)
    {
        var ct = worker.Token;
        await worker.Interaction.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureDeviceAccess();
            if (permission.Path is not null)
            {
                try
                {
                    var path = PathPolicy.Canonical(permission.Path);
                    if (PathPolicy.Within(path, _directory)) return false;
                }
                catch (AgentLinkException) { return false; }
            }
            foreach (var candidate in permission.PossiblePaths ?? [])
            {
                try
                {
                    if (PathPolicy.Within(PathPolicy.Canonical(candidate), _directory)) return false;
                }
                catch (AgentLinkException) { return false; }
            }
            // Removing dialogs must not broaden workspace, command or managed-policy
            // grants. Unsupported permissions are denied, not queued or replayed.
            var allowed = PathPolicy.Preauthorized(permission, worker.Task.Grant, _directory);
            ct.ThrowIfCancellationRequested();
            EnsureDeviceAccess();
            return allowed;
        }
        finally { worker.Interaction.Release(); }
    }

    private async Task<string> QuestionAsync(Worker worker, RuntimeQuestion question)
    {
        var ct = worker.Token;
        await worker.Interaction.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (question.Choices.Length > 50 || question.Choices.Any(choice => choice.Length > 4096))
                throw new AgentLinkException("invalid_question", "The runtime question exceeds supported bounds.");
            var pending = new PendingQuestion(Guid.NewGuid().ToString("N"),
                new(Limit(question.Question, 8192), question.Choices, question.AllowFreeform));
            worker.PendingQuestion = pending;
            _store.Update(worker.Task.TaskId, task => task with { State = "waiting_for_input" }, "question",
                new { questionId = pending.Id, question = pending.Question.Question, choices = question.Choices, question.AllowFreeform });
            var answer = await pending.Answer.Task.WaitAsync(ct).ConfigureAwait(false);
            await WaitConnectedAsync(ct).ConfigureAwait(false);
            SetState(worker, "running");
            return answer;
        }
        finally
        {
            worker.PendingQuestion = null;
            worker.Interaction.Release();
        }
    }

    private OperationResult Answer(string owner, AnswerArguments arguments)
    {
        EnsureConnected();
        var task = OwnedTask(owner, arguments.TaskId);
        var answer = Arguments.Required(arguments.Answer, "answer", 8192);
        if (!_workers.TryGetValue(task.TaskId, out var worker) || worker.PendingQuestion is not { } question ||
            question.Id != arguments.QuestionId || task.State != "waiting_for_input")
            throw new AgentLinkException("question_not_found", "Only an existing non-permission question can be answered.");
        if (!question.Question.AllowFreeform && !question.Question.Choices.Contains(answer, StringComparer.Ordinal))
            throw new AgentLinkException("invalid_answer", "Choose one of the offered answers.");
        if (!question.Answer.TrySetResult(answer))
            throw new AgentLinkException("question_already_answered", "This question was already answered.");
        _store.Append(task.TaskId, "question_answered", new { questionId = question.Id });
        return Result(new { taskId = task.TaskId, questionId = question.Id, accepted = true });
    }

    private OperationResult Cancel(string owner, TaskArguments arguments)
    {
        var task = RequestCancellation(OwnedTask(owner, arguments.TaskId), owner);
        return Result(new { task = PublicTask(task), task.CancellationRequested });
    }

    private AgentTask RequestCancellation(AgentTask task, string requestedBy)
    {
        if (TaskStates.Terminal(task.State)) return task;
        task = _store.Update(task.TaskId, value => value with { CancellationRequested = true },
            "cancellation_requested", new { deliveredToBroker = true, requestedBy });
        if (_workers.TryGetValue(task.TaskId, out var worker))
            worker.Cancel("cancelled");
        return _store.Task(task.TaskId)!;
    }

    private AgentTask ReadLocalTask(string id)
    {
        Arguments.Required(id, "taskId", 128);
        return _store.Task(id) ?? throw new AgentLinkException("task_not_found", "Task not found for this Windows user.");
    }

    private OperationResult ReadEvents(string owner, ReadArguments arguments)
    {
        var task = OwnedTask(owner, arguments.TaskId);
        if (arguments.AfterCursor < 0 || arguments.Limit is < 1 or > 200)
            throw new AgentLinkException("invalid_arguments", "Cursor must be nonnegative; page limit must be 1–200.");
        return Result(_store.ReadEvents(task.TaskId, arguments.AfterCursor, arguments.Limit));
    }

    private AgentSession OwnedSession(string owner, string id)
    {
        Arguments.Required(id, "sessionId", 128);
        var session = _store.Session(id);
        return session is not null && session.Owner == owner ? session :
            throw new AgentLinkException("session_not_found", "Session not found for this peer.");
    }

    private AgentTask OwnedTask(string owner, string id)
    {
        Arguments.Required(id, "taskId", 128);
        var task = _store.Task(id) ?? throw new AgentLinkException("task_not_found", "Task not found for this peer.");
        if (_store.Session(task.SessionId)?.Owner != owner)
            throw new AgentLinkException("task_not_found", "Task not found for this peer.");
        return task;
    }

    private async Task RequireConsentAsync(ConsentRequest request, CancellationToken ct)
    {
        EnsureConnected();
        CancellationToken epoch;
        lock (_connectionGate) epoch = _connectionEpoch.Token;
        using var approval = CancellationTokenSource.CreateLinkedTokenSource(ct, epoch);
        if (!await _consent.RequestAsync(request, approval.Token).WaitAsync(approval.Token).ConfigureAwait(false))
            throw new AgentLinkException("consent_denied", "The endpoint policy did not authorize this action.");
        EnsureConnected();
        approval.Token.ThrowIfCancellationRequested();
    }

    private async Task WaitConnectedAsync(CancellationToken ct)
    {
        while (!Connected)
        {
            Task signal;
            lock (_connectionGate) signal = _connectionAvailable.Task;
            await signal.WaitAsync(ct).ConfigureAwait(false);
            if (!Connected) await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private void EnsureConnected()
    {
        EnsureDeviceAccess();
        if (!Connected) throw new AgentLinkException("disconnected", "An authenticated session is required.");
    }

    private void SetState(Worker worker, string state, string? summary = null) =>
        _store.Update(worker.Task.TaskId, task => task with { State = state }, "state", new { state, summary });

    private SemaphoreSlim SessionLock(string id) => _sessionLocks.GetOrAdd(id, _ => new(1));
    private void EnterInvocation()
    {
        lock (_invocationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _invocations++;
        }
    }

    private void ExitInvocation()
    {
        lock (_invocationGate)
        {
            _invocations--;
            if (_disposed != 0 && _invocations == 0) _invocationsDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Limit(string value, int size) => value.Length <= size ? value : value[..size];
    private static OperationResult Result<T>(T data) => new(Data: Json.Element(data));
    private static object PublicTask(AgentTask task) => new
    {
        task.TaskId, task.SessionId, task.IdempotencyKey, task.State, task.CreatedAt, task.Deadline,
        task.Grant, task.MessageId, task.Result, task.ErrorCode, task.ResultsExpired, task.ObservedNanoAiu, task.CreditWindowUsed,
        task.CancellationRequested, task.ReconciliationAcknowledged
    };

    public async ValueTask DisposeAsync()
    {
        lock (_invocationGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            if (_invocations == 0) _invocationsDrained.TrySetResult();
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _invocationsDrained.Task.ConfigureAwait(false);
        var workers = _workers.Values.ToArray();
        await Task.WhenAll(workers.Select(worker => worker.Execution)).ConfigureAwait(false);
        foreach (var handle in _sessions.Values)
        {
            try { await handle.Runtime!.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* Durable task outcomes are retained even when runtime cleanup fails. */ }
            _store.SaveSession(handle.Session with { Attached = false });
        }
        _store.Dispose();
        _writerLease.Dispose();
        _lifetime.Dispose();
    }

    private sealed class SessionHandle(AgentSession session)
    {
        internal AgentSession Session = session;
        internal IAgentRuntimeSession? Runtime;
        internal Worker? Worker;
    }

    private sealed class Worker : IDisposable
    {
        private readonly CancellationTokenRegistration _deviceRegistration;
        internal Worker(AgentTask task, CancellationToken lifetime, CancellationToken deviceLifetime)
        {
            Task = task;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Token = Cancellation.Token;
            _deviceRegistration = deviceLifetime.Register(() => Cancel("device_access_disabled"));
            Cancellation.CancelAfter(task.Deadline - DateTimeOffset.UtcNow > TimeSpan.Zero ?
                task.Deadline - DateTimeOffset.UtcNow : TimeSpan.Zero);
        }
        internal AgentTask Task { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal CancellationToken Token { get; }
        private readonly object _gate = new();
        private bool _disposed;
        internal string? CancelReason;
        internal Task Execution = System.Threading.Tasks.Task.CompletedTask;
        internal TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SemaphoreSlim Interaction = new(1);
        internal PendingQuestion? PendingQuestion;
        internal void Cancel(string reason)
        {
            lock (_gate)
            {
                if (_disposed) return;
                Interlocked.CompareExchange(ref CancelReason, reason, null);
                Cancellation.Cancel();
            }
        }
        public void Dispose()
        {
            _deviceRegistration.Dispose();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Cancellation.Cancel();
                Cancellation.Dispose();
            }
        }
    }

    private sealed record PendingQuestion(string Id, RuntimeQuestion Question)
    {
        internal TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record EmptyArguments;
    private sealed record StartArguments(string? WorkingDirectory = null, string? SessionId = null);
    private sealed record SessionArguments(string SessionId);
    private sealed record AttachArguments(string RegistrationId, string SessionId);
    private sealed record TaskArguments(string TaskId);
    private sealed record ReadArguments(string TaskId, long AfterCursor = 0, int Limit = 100);
    private sealed record AnswerArguments(string TaskId, string QuestionId, string Answer);
    private sealed record SendArguments(string SessionId, string Prompt, string IdempotencyKey,
        int TimeoutSeconds, string? WorkingDirectory = null, bool Unattended = false, double? MaxAiCredits = null,
        bool AllowWorkspaceRead = false, bool AllowWorkspaceWrite = false, string[]? ApprovedCommands = null);

}
