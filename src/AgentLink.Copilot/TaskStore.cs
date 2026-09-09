using AgentLink.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AgentLink.Copilot;

internal sealed class TaskStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    internal const int MaximumEventsPerTask = 1000;
    internal static readonly TimeSpan ResultRetention = TimeSpan.FromDays(30);

    internal TaskStore(string privateDirectory)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(privateDirectory, "tasks.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, owner TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks(
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id),
              idempotency_key TEXT NOT NULL, active INTEGER NOT NULL, payload TEXT NOT NULL,
              UNIQUE(session_id,idempotency_key));
            CREATE UNIQUE INDEX IF NOT EXISTS one_task_writer ON tasks(session_id) WHERE active=1;
            CREATE TABLE IF NOT EXISTS events(
              cursor INTEGER PRIMARY KEY AUTOINCREMENT, task_id TEXT NOT NULL REFERENCES tasks(id),
              kind TEXT NOT NULL, data TEXT NOT NULL, timestamp TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS task_events ON events(task_id,cursor);
            CREATE TABLE IF NOT EXISTS event_retention(task_id TEXT PRIMARY KEY, pruned_through INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS usage(
              task_id TEXT NOT NULL REFERENCES tasks(id), source_id TEXT NOT NULL,
              amount REAL NOT NULL, PRIMARY KEY(task_id,source_id));
            CREATE TABLE IF NOT EXISTS settings(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            """);
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    {
        using var command = Command(sql, transaction, values);
        command.ExecuteNonQuery();
    }

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Json.Options) ?? throw new InvalidDataException("Invalid durable record.");

    private T? One<T>(string sql, params (string, object?)[] values)
    {
        using var command = Command(sql, null, values);
        var value = command.ExecuteScalar();
        return value is string payload ? Deserialize<T>(payload) : default;
    }

    internal AgentSession? Session(string id)
    {
        lock (_gate) return One<AgentSession>("SELECT payload FROM sessions WHERE id=$id", ("$id", id));
    }

    internal IReadOnlyList<AgentSession> Sessions(string? owner = null)
    {
        lock (_gate)
        {
            using var command = Command("SELECT payload FROM sessions WHERE $owner IS NULL OR owner=$owner ORDER BY id", null, ("$owner", owner));
            using var reader = command.ExecuteReader();
            List<AgentSession> result = [];
            while (reader.Read()) result.Add(Deserialize<AgentSession>(reader.GetString(0)));
            return result;
        }
    }

    internal AgentSession SaveSession(AgentSession session)
    {
        lock (_gate)
        {
            if (Session(session.SessionId)?.RequiresReconciliation == true)
                session = session with { RequiresReconciliation = true };
            WriteSession(session);
            return session;
        }
    }

    private void WriteSession(AgentSession session, SqliteTransaction? transaction = null) =>
        Execute("INSERT INTO sessions VALUES($id,$owner,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload",
            transaction, ("$id", session.SessionId), ("$owner", session.Owner),
            ("$payload", JsonSerializer.Serialize(session, Json.Options)));

    internal AgentTask? Task(string id)
    {
        lock (_gate) return One<AgentTask>("SELECT payload FROM tasks WHERE id=$id", ("$id", id));
    }

    internal IReadOnlyList<AgentTask> LocalTasks(int limit, bool activeOnly, string? beforeTaskId)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT payload FROM tasks
                WHERE ($active=0 OR active=1)
                  AND ($before IS NULL OR rowid<(SELECT rowid FROM tasks WHERE id=$before))
                ORDER BY rowid DESC LIMIT $limit
                """, null, ("$active", activeOnly ? 1 : 0), ("$before", beforeTaskId), ("$limit", limit));
            using var reader = command.ExecuteReader();
            List<AgentTask> tasks = [];
            while (reader.Read()) tasks.Add(Deserialize<AgentTask>(reader.GetString(0)));
            return tasks;
        }
    }

    internal AgentTask? Find(string sessionId, string key)
    {
        lock (_gate) return One<AgentTask>(
            "SELECT payload FROM tasks WHERE session_id=$session AND idempotency_key=$key",
            ("$session", sessionId), ("$key", key));
    }

    internal bool Busy(string sessionId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT COUNT(*) FROM tasks WHERE session_id=$id AND active=1", null, ("$id", sessionId));
            return (long)command.ExecuteScalar()! > 0;
        }
    }

    internal void Accept(AgentTask task)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("INSERT INTO tasks VALUES($id,$session,$key,1,$payload)", transaction,
                ("$id", task.TaskId), ("$session", task.SessionId), ("$key", task.IdempotencyKey),
                ("$payload", JsonSerializer.Serialize(task, Json.Options)));
            AddEvent(task.TaskId, "accepted", Json.Element(new { task.State, task.Deadline, task.Grant }), transaction);
            transaction.Commit();
        }
    }

    internal AgentTask Update(string id, Func<AgentTask, AgentTask> update, string? eventKind = null, object? data = null)
    {
        lock (_gate)
        {
            var previous = Task(id) ?? throw new AgentLinkException("task_not_found", "Task not found.");
            if (TaskStates.Terminal(previous.State)) return previous;
            var next = update(previous);
            var uncertainSession = next.State == "outcome_unknown"
                ? Session(next.SessionId) ?? throw new InvalidDataException("Task session is missing.")
                : null;
            if (uncertainSession is not null)
                next = next with { ReconciliationAcknowledged = false };
            using var transaction = _connection.BeginTransaction();
            Execute("UPDATE tasks SET active=$active,payload=$payload WHERE id=$id", transaction,
                ("$active", TaskStates.Terminal(next.State) ? 0 : 1),
                ("$payload", JsonSerializer.Serialize(next, Json.Options)), ("$id", id));
            if (uncertainSession is not null)
                WriteSession(uncertainSession with { RequiresReconciliation = true }, transaction);
            AddEvent(id, eventKind ?? "state", Json.Element(data ?? new { next.State, next.ErrorCode }), transaction);
            transaction.Commit();
            return next;
        }
    }

    internal void Append(string taskId, string kind, object data)
    {
        lock (_gate)
        {
            if (Task(taskId) is not { } task || TaskStates.Terminal(task.State)) return;
            using var transaction = _connection.BeginTransaction();
            AddEvent(taskId, kind, Json.Element(data), transaction);
            transaction.Commit();
        }
    }

    internal void RecordUsage(string taskId, string sourceId, double nanoAiu)
    {
        if (!double.IsFinite(nanoAiu) || nanoAiu < 0 || sourceId.Length is < 1 or > 256) return;
        lock (_gate)
        {
            if (Task(taskId) is not { } task || TaskStates.Terminal(task.State)) return;
            using var transaction = _connection.BeginTransaction();
            using var insert = Command("INSERT OR IGNORE INTO usage VALUES($task,$source,$amount)",
                transaction, ("$task", taskId), ("$source", sourceId), ("$amount", nanoAiu));
            if (insert.ExecuteNonQuery() > 0)
            {
                task = task with { ObservedNanoAiu = task.ObservedNanoAiu + nanoAiu };
                Execute("UPDATE tasks SET payload=$payload WHERE id=$id", transaction,
                    ("$id", taskId), ("$payload", JsonSerializer.Serialize(task, Json.Options)));
                AddEvent(taskId, "usage", Json.Element(new { nanoAiu, task.ObservedNanoAiu }), transaction);
            }
            transaction.Commit();
        }
    }

    private void AddEvent(string taskId, string kind, JsonElement data, SqliteTransaction transaction)
    {
        Execute("INSERT INTO events(task_id,kind,data,timestamp) VALUES($task,$kind,$data,$time)", transaction,
            ("$task", taskId), ("$kind", kind), ("$data", data.GetRawText()), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        Execute("""
            INSERT INTO event_retention(task_id,pruned_through)
            SELECT $task, MAX(cursor) FROM events WHERE task_id=$task AND cursor NOT IN
              (SELECT cursor FROM events WHERE task_id=$task ORDER BY cursor DESC LIMIT $maximum)
            HAVING COUNT(*)>0
            ON CONFLICT(task_id) DO UPDATE SET pruned_through=MAX(pruned_through,excluded.pruned_through)
            """, transaction, ("$task", taskId), ("$maximum", MaximumEventsPerTask));
        Execute("""
            DELETE FROM events WHERE task_id=$task AND cursor NOT IN
              (SELECT cursor FROM events WHERE task_id=$task ORDER BY cursor DESC LIMIT $maximum)
            """, transaction, ("$task", taskId), ("$maximum", MaximumEventsPerTask));
    }

    internal EventPage ReadEvents(string taskId, long after, int limit)
    {
        lock (_gate)
        {
            using var oldestCommand = Command("SELECT MIN(cursor) FROM events WHERE task_id=$task", null, ("$task", taskId));
            var first = oldestCommand.ExecuteScalar();
            long oldest = first is long number ? number : 0;
            using var retentionCommand = Command("SELECT pruned_through FROM event_retention WHERE task_id=$task", null, ("$task", taskId));
            long prunedThrough = retentionCommand.ExecuteScalar() is long pruned ? pruned : 0;
            using var command = Command(
                "SELECT cursor,kind,data,timestamp FROM events WHERE task_id=$task AND cursor>$after ORDER BY cursor LIMIT $limit",
                null, ("$task", taskId), ("$after", after), ("$limit", limit + 1));
            using var reader = command.ExecuteReader();
            List<AgentEvent> events = [];
            while (reader.Read())
                events.Add(new(reader.GetInt64(0), taskId, reader.GetString(1), reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)));
            bool more = events.Count > limit;
            if (more) events.RemoveAt(events.Count - 1);
            return new(events, events.Count == 0 ? after : events[^1].Cursor, oldest, more,
                prunedThrough > after || Task(taskId)?.ResultsExpired == true);
        }
    }

    internal void Recover()
    {
        lock (_gate)
        {
            foreach (var task in AllTasks().Where(t => !TaskStates.Terminal(t.State)))
            {
                Update(task.TaskId, t => t with
                {
                    State = t.DispatchStarted ? "outcome_unknown" : t.CancellationRequested ? "cancelled" : "interrupted",
                    ErrorCode = t.DispatchStarted ? "broker_restarted_after_dispatch" :
                        t.CancellationRequested ? "cancelled_before_dispatch" : "broker_restarted_before_dispatch"
                });
            }
            // Repair older split-write records, while preserving explicit local acknowledgements.
            foreach (var task in AllTasks().Where(t => t.State == "outcome_unknown" && !t.ReconciliationAcknowledged))
                if (Session(task.SessionId) is { } uncertainSession)
                    SaveSession(uncertainSession with { RequiresReconciliation = true });
            foreach (var session in Sessions())
                SaveSession(session with { Attached = false });
            Prune(DateTimeOffset.UtcNow);
        }
    }

    internal void ReconcileStoppedManagedSession(string sessionId)
    {
        lock (_gate)
        {
            var session = Session(sessionId) ?? throw new AgentLinkException("session_not_found", "Session not found.");
            if (session.Kind != "managed" || Busy(sessionId))
                throw new AgentLinkException("session_busy", "Only a stopped, reconciled managed session can be acknowledged locally.");
            var unknown = AllTasks().Where(task => task.SessionId == sessionId &&
                task.State == "outcome_unknown" && !task.ReconciliationAcknowledged).ToArray();
            using var transaction = _connection.BeginTransaction();
            foreach (var task in unknown)
                Execute("UPDATE tasks SET payload=$payload WHERE id=$id", transaction,
                    ("$id", task.TaskId), ("$payload", JsonSerializer.Serialize(
                        task with { ReconciliationAcknowledged = true }, Json.Options)));
            WriteSession(session with { RequiresReconciliation = false, Attached = false }, transaction);
            transaction.Commit();
        }
    }

    private List<AgentTask> AllTasks()
    {
        using var command = Command("SELECT payload FROM tasks");
        using var reader = command.ExecuteReader();
        List<AgentTask> tasks = [];
        while (reader.Read()) tasks.Add(Deserialize<AgentTask>(reader.GetString(0)));
        return tasks;
    }

    internal void Prune(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var task in AllTasks().Where(t => TaskStates.Terminal(t.State) && !t.ResultsExpired && t.Deadline < now - ResultRetention))
            {
                using var transaction = _connection.BeginTransaction();
                var tombstone = task with { Prompt = "", Result = null, ResultsExpired = true, Grant = task.Grant with { ApprovedCommands = [] } };
                Execute("UPDATE tasks SET payload=$payload WHERE id=$id", transaction,
                    ("$id", task.TaskId), ("$payload", JsonSerializer.Serialize(tombstone, Json.Options)));
                Execute("DELETE FROM events WHERE task_id=$id", transaction, ("$id", task.TaskId));
                Execute("DELETE FROM usage WHERE task_id=$id", transaction, ("$id", task.TaskId));
                transaction.Commit();
            }
        }
    }

    internal void SetSetting<T>(string id, T value)
    {
        lock (_gate)
            Execute("INSERT INTO settings VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload",
                null, ("$id", id), ("$payload", JsonSerializer.Serialize(value, Json.Options)));
    }

    internal T? GetSetting<T>(string id)
    {
        lock (_gate) return One<T>("SELECT payload FROM settings WHERE id=$id", ("$id", id));
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
