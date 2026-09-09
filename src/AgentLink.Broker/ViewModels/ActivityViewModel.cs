using System.Collections.ObjectModel;
using System.Text.Json;
using AgentLink.Copilot;
using AgentLink.Core;

namespace AgentLink.Broker.ViewModels;

public sealed record TaskItem(string TaskId, string State, string Workspace, string Created)
{
    public string Label => $"{State} · {TaskId}";
}

public sealed class ActivityViewModel : ObservableModel
{
    private bool _activeOnly;
    private bool _hasOlder;
    private bool _isEmpty = true;
    private string _status = "Task history is stored on this device.";
    private string _details = "Select a task to see its prompt, permission grant and result.";
    private TaskItem? _selected;
    internal string? BeforeTaskId { get; private set; }
    private string? _lastTaskId;
    public ObservableCollection<TaskItem> Tasks { get; } = [];
    public event Action? RefreshRequested;
    public bool ActiveOnly
    {
        get => _activeOnly;
        set { if (Set(ref _activeOnly, value)) Newest(); }
    }
    public bool HasOlder { get => _hasOlder; private set => Set(ref _hasOlder, value); }
    public bool IsEmpty { get => _isEmpty; private set { if (Set(ref _isEmpty, value)) Notify(nameof(HasTasks)); } }
    public bool HasTasks => !IsEmpty;
    public string Status { get => _status; internal set => Set(ref _status, value); }
    public string Details { get => _details; internal set => Set(ref _details, value); }
    public TaskItem? Selected { get => _selected; set { if (Set(ref _selected, value)) Notify(nameof(CanCancel)); } }
    public bool CanCancel => Selected is not null && !IsTerminal(Selected.State);
    internal static bool IsTerminal(string state) => state is "completed" or "failed" or "cancelled" or "interrupted" or "outcome_unknown";
    public void Newest() { BeforeTaskId = null; RefreshRequested?.Invoke(); }
    public void Older() { if (HasOlder) { BeforeTaskId = _lastTaskId; RefreshRequested?.Invoke(); } }

    internal void Update(IReadOnlyList<AgentTask> tasks)
    {
        var id = Selected?.TaskId;
        var rows = tasks.Select(task => new TaskItem(task.TaskId, task.State, task.Grant.WorkingDirectory, task.CreatedAt.ToLocalTime().ToString("g"))).ToArray();
        if (!Tasks.SequenceEqual(rows))
        {
            Tasks.Clear();
            foreach (var row in rows) Tasks.Add(row);
            Selected = Tasks.FirstOrDefault(row => row.TaskId == id);
        }
        HasOlder = tasks.Count == 100;
        _lastTaskId = HasOlder ? tasks[^1].TaskId : null;
        IsEmpty = Tasks.Count == 0;
        Status = $"{tasks.Count} tasks on this page · cancellation is available here even when RDP is disconnected.";
    }

    internal void ShowDetails(AgentTask task) => Details =
        $"Task: {task.TaskId}\nSession: {task.SessionId}\nState: {task.State}\n" +
        $"Cancellation requested: {task.CancellationRequested}\nDeadline: {task.Deadline:u}\n" +
        $"Result retention expired: {task.ResultsExpired}\nReconciliation acknowledged: {task.ReconciliationAcknowledged}\n" +
        $"Permission grant:\n{JsonSerializer.Serialize(task.Grant, new JsonSerializerOptions(Json.Options) { WriteIndented = true })}\n" +
        $"Error: {task.ErrorCode ?? "(none)"}\n\nPrompt:\n{task.Prompt}\n\nResult:\n{task.Result ?? "(not available)"}";
}
