using System.Text.Json;
using AgentLink.Core;

internal static partial class BackendTests
{
    internal static async Task RunDiagnosticsAsync(Func<string, Func<Task>, Task> test)
    {
        await test("broker diagnostics record transitions and denied operations without heartbeat or payload logging", async () =>
        {
            await using var fixture = new Fixture();
            var directory = Path.Combine(fixture.DataDirectory, "diagnostics");
            var priorLevel = Environment.GetEnvironmentVariable("RALINK_LOG_LEVEL");
            Environment.SetEnvironmentVariable("RALINK_LOG_LEVEL", "info");
            try
            {
                await using (var log = Diagnostics.Start(DiagnosticComponent.Broker, directory))
                {
                    fixture.Host.Start();
                    var instance = Guid.NewGuid();
                    var epoch = Guid.NewGuid();
                    await using (var reporter = await OpenClientReporter(fixture))
                    {
                        for (var index = 0; index < 4; index++)
                            await reporter.WriteAsync(ClientRecord(instance, epoch, 1));
                        await Wait(() => fixture.Host.Connections.Count == 1);
                    }
                    await Wait(() => fixture.Host.Connections.Count == 0);
                    fixture.Session.SetDesktopAccessEnabled(false);
                    await ThrowsCode("device_access_disabled", () => LocalInvoke(fixture, "type_text",
                        new { text = "PRIVATE-KEYSTROKES-MUST-NOT-BE-LOGGED" }));
                    fixture.Host.StopDesktopControl();
                    await fixture.Host.DisposeAsync();
                }
                var lines = Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadAllLines).ToArray();
                var records = lines.Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).ToArray();
                string? Event(JsonElement record) => record.GetProperty("event").GetString();
                Check(records.Count(record => Event(record) == "ClientLifecycle") == 1 &&
                    records.Count(record => Event(record) == "ClientRemoved") == 1,
                    "Lifecycle heartbeats were logged as transitions, or the removal was missed.");
                foreach (var expected in new[] { "BrokerStarted", "DeviceAccessChanged", "ControlRevoked", "BrokerStopped" })
                    Check(records.Any(record => Event(record) == expected), "Missing diagnostic transition: " + expected);
                Check(records.Any(record => Event(record) == "OperationFailed" &&
                    record.GetProperty("operation").GetString() == "type_text" &&
                    record.GetProperty("errorCode").GetString() == "device_access_disabled"),
                    "Blocked operation did not explain its failure.");
                Check(lines.All(line => !line.Contains("PRIVATE-KEYSTROKES", StringComparison.Ordinal) &&
                    !line.Contains(fixture.Identity.Fingerprint, StringComparison.Ordinal) &&
                    !line.Contains(fixture.DataDirectory, StringComparison.Ordinal)),
                    "Broker diagnostics leaked operation data, endpoint identity or local paths.");
            }
            finally { Environment.SetEnvironmentVariable("RALINK_LOG_LEVEL", priorLevel); }
        });
    }
}
