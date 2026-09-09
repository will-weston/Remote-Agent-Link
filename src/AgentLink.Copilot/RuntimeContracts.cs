namespace AgentLink.Copilot;

public sealed record AgentSession(
    string SessionId, string WorkingDirectory, string Owner, string Kind = "managed",
    string? RegistrationId = null, bool Attached = true, bool RequiresReconciliation = false);

public sealed record TaskGrant(
    string WorkingDirectory, bool AllowWorkspaceRead, bool AllowWorkspaceWrite,
    string[] ApprovedCommands, bool Unattended, int TimeoutSeconds, double? MaxAiCredits);

public sealed record AgentTask(
    string TaskId, string SessionId, string IdempotencyKey, string Fingerprint, string Prompt,
    string State, TaskGrant Grant, DateTimeOffset CreatedAt, DateTimeOffset Deadline,
    bool DispatchStarted = false, string? MessageId = null, string? Result = null,
    string? ErrorCode = null, bool ResultsExpired = false, double ObservedNanoAiu = 0,
    double? CreditWindowUsed = null, bool CancellationRequested = false,
    bool ReconciliationAcknowledged = false);

public sealed record AgentEvent(long Cursor, string TaskId, string Kind, string Data, DateTimeOffset Timestamp);
public sealed record EventPage(
    IReadOnlyList<AgentEvent> Events, long NextCursor, long OldestAvailableCursor,
    bool HasMore, bool HistoryTruncated);

public sealed record RuntimePermission(
    string Kind, string Summary, string? Path = null, string? Command = null,
    string[]? PossiblePaths = null, bool ManagedApprovalRequired = false,
    bool SandboxBypass = false, bool HasRedirection = false, bool HasUrls = false,
    bool RequiresDesktop = false);

public sealed record RuntimeQuestion(string Question, string[] Choices, bool AllowFreeform);
public sealed record RuntimeEvent(
    string Kind, string? Text = null, string? EventId = null, double? NanoAiu = null, double? UsedAiCredits = null);
public sealed record RuntimeCallbacks(
    Func<RuntimePermission, CancellationToken, Task<bool>> Permission,
    Func<RuntimeQuestion, CancellationToken, Task<string>> Question,
    Action<RuntimeEvent> Event);

/// <summary>Implementations must never attach by scraping terminal output or copying authentication.</summary>
public interface ICopilotRuntimeFactory
{
    Task<IAgentRuntimeSession> OpenManagedAsync(
        AgentSession session, bool resume, RuntimeCallbacks callbacks, CancellationToken cancellationToken);
    Task<IAgentRuntimeSession> AttachAsync(
        LiveRuntimeRegistration registration, RuntimeCallbacks callbacks, CancellationToken cancellationToken);
}

/// <summary>
/// One exclusive delegated writer. External implementations must provide runtime-enforced turn ownership,
/// preserve existing policy, and dispose only their connection without stopping the external runtime.
/// </summary>
public interface IAgentRuntimeSession : IAsyncDisposable
{
    bool IsExternal { get; }
    Task<bool> IsIdleAsync(CancellationToken cancellationToken);
    Task PrepareAsync(double? maxAiCredits, CancellationToken cancellationToken);
    Task<string> SendAsync(string prompt, CancellationToken cancellationToken);
    Task<bool> AbortOwnedTurnAsync(string messageId, CancellationToken cancellationToken);
}

internal static class TaskStates
{
    internal static bool Terminal(string state) =>
        state is "completed" or "failed" or "cancelled" or "interrupted" or "outcome_unknown";
}
