using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentLink.Core;

internal static class DiagnosticTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("diagnostics redact arbitrary protocol strings and exception payloads", async () =>
        {
            using var folder = new Folder();
            var correlation = Guid.NewGuid();
            await using (var log = new DiagnosticLog(DiagnosticComponent.Broker, folder.Path, DiagnosticLevel.Debug))
            {
                log.Write(DiagnosticEvent.OperationFailed, DiagnosticLevel.Warning, "private typed text\nforged",
                    "secret prompt", correlation, long.MaxValue, new AgentLinkException("private_token", "private payload"));
                log.Write(DiagnosticEvent.OperationCompleted, operation: "take_screenshot", outcome: "ok", correlation: correlation);
            }
            var records = Read(folder.Path);
            Check(records.Length == 2, "Unexpected diagnostic records.");
            var text = string.Join("", records.Select(record => record.GetRawText()));
            Check(!text.Contains("private", StringComparison.Ordinal) && !text.Contains("secret", StringComparison.Ordinal) &&
                !text.Contains("forged", StringComparison.Ordinal), "Diagnostic payload escaped its allowlist.");
            Check(records[0].GetProperty("operation").GetString() == "unknown" &&
                records[0].GetProperty("outcome").GetString() == "unknown" &&
                records[0].GetProperty("errorCode").GetString() == "other_agent_error" &&
                records[0].GetProperty("durationMs").GetInt64() == 86_400_000 &&
                records[1].GetProperty("correlation").GetGuid() == correlation &&
                records[1].GetProperty("operation").GetString() == "take_screenshot",
                "Safe diagnostic fields or correlation were lost.");
            using var identity = WindowsIdentity.GetCurrent();
            var access = new DirectoryInfo(folder.Path).GetAccessControl(AccessControlSections.Access);
            Check(access.AreAccessRulesProtected &&
                access.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .All(rule => rule.IdentityReference.Equals(identity.User)), "Diagnostic directory is not user-private.");
        });
        await test("diagnostics filter successes by default and off creates no files", async () =>
        {
            using var folder = new Folder();
            await using (var log = new DiagnosticLog(DiagnosticComponent.Broker, folder.Path))
            {
                log.Write(DiagnosticEvent.OperationCompleted, DiagnosticLevel.Debug, operation: "type_text");
                log.Write(DiagnosticEvent.OperationFailed, DiagnosticLevel.Warning, exception: new IOException("secret"));
            }
            Check(Read(folder.Path) is [var record] && record.GetProperty("event").GetString() == "OperationFailed",
                "Default logging captured debug successes or omitted failure.");
            var disabledPath = System.IO.Path.Combine(folder.Path, "disabled");
            await using (var disabled = new DiagnosticLog(DiagnosticComponent.Mcp, disabledPath, DiagnosticLevel.Off))
                disabled.Write(DiagnosticEvent.ProcessStarted);
            Check(!Directory.Exists(disabledPath), "Off touched the filesystem.");
        });
        await test("diagnostics bound repeat floods and disclose suppressed counts", async () =>
        {
            using var folder = new Folder();
            var clock = new Clock();
            var options = new DiagnosticLogOptions { Clock = clock, MaximumPerEventPerMinute = 2 };
            await using (var log = new DiagnosticLog(DiagnosticComponent.Broker, folder.Path, DiagnosticLevel.Information, options))
            {
                for (var index = 0; index < 1000; index++)
                    log.Write(DiagnosticEvent.ConnectionFailed, DiagnosticLevel.Warning);
                Check(log.SuppressedCount == 998, "Repeat storm was not bounded.");
                clock.Advance(TimeSpan.FromMinutes(1));
                log.Write(DiagnosticEvent.ConnectionFailed, DiagnosticLevel.Warning);
            }
            var records = Read(folder.Path);
            Check(records.Count(record => record.GetProperty("event").GetString() == "ConnectionFailed") == 3 &&
                records.Where(record => record.GetProperty("event").GetString() == "LoggingSummary")
                    .Sum(record => record.GetProperty("suppressed").GetInt64()) == 998,
                "Window reset or suppressed-event accounting failed.");
        });
        await test("diagnostics cap total traffic and a full queue never waits for disk", async () =>
        {
            using var folder = new Folder();
            var options = new DiagnosticLogOptions
            {
                QueueCapacity = 2, FlushInterval = TimeSpan.FromHours(1),
                MaximumPerMinute = 1000, MaximumPerEventPerMinute = 1000
            };
            var log = new DiagnosticLog(DiagnosticComponent.Broker, folder.Path, DiagnosticLevel.Information, options);
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < 100; index++) log.Write(DiagnosticEvent.ConnectionFailed);
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && log.DroppedCount == 98,
                "Full diagnostic queue blocked producers or lost drop accounting.");
            await log.DisposeAsync();
            Check(Read(folder.Path).Single(record => record.GetProperty("event").GetString() == "LoggingSummary")
                .GetProperty("dropped").GetInt64() == 98, "Dropped records were not reported.");
            using var capped = new Folder();
            await using var total = new DiagnosticLog(DiagnosticComponent.Mcp, capped.Path, DiagnosticLevel.Debug,
                options with { QueueCapacity = 32, MaximumPerMinute = 3 });
            foreach (var eventId in Enum.GetValues<DiagnosticEvent>().Take(10)) total.Write(eventId);
            Check(total.SuppressedCount == 7, "Global rate bound did not apply across event categories.");
        });
        await test("diagnostics rotate by bytes and reuse bounded slots across restarts", async () =>
        {
            using var folder = new Folder();
            var options = new DiagnosticLogOptions
            {
                MaximumFileBytes = 1024, RetainedFiles = 3, Slots = 1,
                MaximumPerEventPerMinute = 1000, MaximumPerMinute = 1000
            };
            for (var run = 0; run < 3; run++)
            {
                await using var log = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path, DiagnosticLevel.Debug, options);
                for (var index = 0; index < 40; index++)
                    log.Write(DiagnosticEvent.OperationCompleted, operation: "take_screenshot", correlation: Guid.NewGuid());
            }
            var files = Directory.GetFiles(folder.Path, "*.jsonl");
            Check(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 1024),
                "Rotation exceeded its byte/file budget or accumulated files by run.");
            Check(Read(folder.Path).Length > 0, "Rotated logs were not valid JSONL.");
        });
        await test("diagnostics preserve interrupted tails without corrupting new records", async () =>
        {
            using var folder = new Folder();
            var current = System.IO.Path.Combine(folder.Path, "slot-0.0.jsonl");
            File.WriteAllText(current, "{\"interrupted\":");
            await using (var log = new DiagnosticLog(DiagnosticComponent.Broker, folder.Path))
                log.Write(DiagnosticEvent.ProcessStarted);
            using var record = JsonDocument.Parse(File.ReadAllText(current));
            Check(record.RootElement.GetProperty("event").GetString() == "ProcessStarted" &&
                File.ReadAllText(System.IO.Path.Combine(folder.Path, "slot-0.1.jsonl")) == "{\"interrupted\":",
                "New records were appended to a partial JSON line or the prior tail was lost.");
        });
        await test("diagnostics isolate concurrent writers and fail safely when slots are occupied", async () =>
        {
            using var folder = new Folder();
            var options = new DiagnosticLogOptions { Slots = 2, FlushInterval = TimeSpan.FromMilliseconds(10) };
            await using var first = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path, DiagnosticLevel.Information, options);
            await Ready(first);
            await using var second = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path, DiagnosticLevel.Information, options);
            await Ready(second);
            await using (var third = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path, DiagnosticLevel.Information, options))
            {
                await Failed(third);
                third.Write(DiagnosticEvent.ProcessStarted);
                Check(third.DroppedCount == 1, "Excess process stole an occupied slot or retained records forever.");
            }
            first.Write(DiagnosticEvent.ProcessStarted);
            second.Write(DiagnosticEvent.ProcessStarted);
            await first.DisposeAsync();
            await second.DisposeAsync();
            Check(Read(folder.Path).Select(record => record.GetProperty("run").GetGuid()).Distinct().Count() == 2,
                "Concurrent writers mixed or replaced each other's records.");
        });
        await test("diagnostics release replacement handles when rotation or inspection fails", async () =>
        {
            foreach (var failInspection in new[] { false, true })
            {
                using var folder = new Folder();
                var current = System.IO.Path.Combine(folder.Path, "slot-0.0.jsonl");
                if (!failInspection)
                    File.WriteAllText(current, JsonSerializer.Serialize(new { padding = new string('x', 850) }) + "\n");
                var opened = 0;
                var options = new DiagnosticLogOptions
                {
                    Slots = 1, MaximumFileBytes = 1024, FlushInterval = TimeSpan.FromMilliseconds(10),
                    OpenOutput = (path, mode) => ++opened == (failInspection ? 1 : 2)
                        ? new FaultingOutput(path, mode, failInspection)
                        : new FileStream(path, mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                            4096, FileOptions.Asynchronous)
                };
                await using (var log = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path, DiagnosticLevel.Information, options))
                {
                    log.Write(DiagnosticEvent.ProcessStarted);
                    await Failed(log);
                }
                // No GC/finalizer: a new writer must be able to use the released slot immediately.
                using (var probe = new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(probe.CanWrite, "A failed diagnostic stream retained its file handle.");
                await using (var recovered = new DiagnosticLog(DiagnosticComponent.Mcp, folder.Path))
                    recovered.Write(DiagnosticEvent.ProcessStarted);
                Check(File.ReadAllText(current).Contains("\"event\":\"ProcessStarted\"", StringComparison.Ordinal),
                    "The next process could not reuse a slot after an append failure.");
            }
        });
        await test("diagnostic filesystem failures cannot fail operations or shutdown", async () =>
        {
            using var folder = new Folder();
            var blocked = System.IO.Path.Combine(folder.Path, "not-a-directory");
            File.WriteAllText(blocked, "owned test fixture");
            await using var log = new DiagnosticLog(DiagnosticComponent.Broker, blocked);
            await Failed(log);
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < 10000; index++)
                log.Write(DiagnosticEvent.OperationFailed, DiagnosticLevel.Error, exception: new IOException("private"));
            await log.DisposeAsync();
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(2) && File.ReadAllText(blocked) == "owned test fixture",
                "Unavailable diagnostic storage blocked work or overwrote an existing file.");
        });
    }

    private static JsonElement[] Read(string directory) => Directory.GetFiles(directory, "*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private static async Task Ready(DiagnosticLog log)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (log.Status == "Starting") await Task.Delay(10, deadline.Token);
        Check(log.Status == "Writing bounded local logs", "Diagnostic writer did not start: " + log.Status);
    }

    private static async Task Failed(DiagnosticLog log)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!log.Status.StartsWith("File logging unavailable", StringComparison.Ordinal)) await Task.Delay(10, deadline.Token);
        Check(log.Status.StartsWith("File logging unavailable", StringComparison.Ordinal), "Sink failure was not visible.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class FaultingOutput(string path, FileMode mode, bool failInspection)
        : FileStream(path, mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous)
    {
        public override long Seek(long offset, SeekOrigin origin) => failInspection
            ? throw new IOException("Injected inspection failure.") : base.Seek(offset, origin);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected replacement write failure."));
    }

    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ralink-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
