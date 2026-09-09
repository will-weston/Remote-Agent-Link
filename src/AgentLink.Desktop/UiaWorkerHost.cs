using AgentLink.Core;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentLink.Desktop;

internal static class UiaWorkerHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int disabled;
    internal static async Task<JsonElement> RunAsync(UiaRequest request, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disabled) != 0)
                throw new AgentLinkException("uia_unavailable", "An earlier UIA worker could not be reaped. Restart the broker; no further providers will be launched.");
            string path = Path.Combine(AppContext.BaseDirectory, "DesktopWorker", "AgentLink.Desktop.Worker.exe");
            if (!File.Exists(path))
                throw new AgentLinkException("uia_unavailable", "The packaged DesktopWorker\\AgentLink.Desktop.Worker.exe payload is missing.");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(path)!
                }
            };
            process.StartInfo.ArgumentList.Add("--uia-worker");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using WorkerJob job = WorkerJob.Create();
            bool started = false;
            Task<byte[]>? output = null, errors = null;
            try
            {
                if (!process.Start()) throw new AgentLinkException("uia_unavailable", "The isolated UIA worker did not start.");
                started = true;
                job.Assign(process);
                output = ReadBoundedAsync(process.StandardOutput.BaseStream, Rules.MaxUiaBytes + 4096, deadline.Token);
                errors = ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json.Options).AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new AgentLinkException("uia_provider_failed", $"Isolated UIA worker exited with code {process.ExitCode}; no partial result was returned.");
                WorkerResponse response = JsonSerializer.Deserialize<WorkerResponse>(await output.ConfigureAwait(false), Json.Options)
                    ?? throw new AgentLinkException("uia_provider_failed", "The UIA worker returned an empty response.");
                if (response.Code is not null) throw new AgentLinkException(response.Code, response.Error ?? "UI Automation failed.");
                JsonElement result = response.Data ?? throw new AgentLinkException("uia_provider_failed", "The UIA worker returned no data.");
                if (JsonSerializer.SerializeToUtf8Bytes(result).Length > Rules.MaxUiaBytes)
                    throw new AgentLinkException("result_too_large", "UI Automation response exceeds 192 KiB.");
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new AgentLinkException("uia_timeout", "UI Automation exceeded its 5-second hard deadline. Its isolated worker is terminated; no partial result is returned.");
            }
            catch (JsonException)
            {
                throw new AgentLinkException("uia_provider_failed", "The UIA worker returned malformed output.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new AgentLinkException("uia_unavailable", "Windows could not launch or isolate the UIA worker.");
            }
            finally
            {
                deadline.Cancel();
                if (started && !TryReap(process)) Interlocked.Exchange(ref disabled, 1);
                job.Dispose();
                // Readers use cancellation and bounded buffers; always observe their completion.
                if (output is not null) await ObserveReader(output).ConfigureAwait(false);
                if (errors is not null) await ObserveReader(errors).ConfigureAwait(false);
            }
        }
        finally { Gate.Release(); }
    }
    private static async Task ObserveReader(Task<byte[]> reader)
    {
        try { await reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref disabled, 1);
            _ = reader.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception) { }
    }
    private static bool TryReap(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            return process.WaitForExit(1000);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new AgentLinkException("result_too_large", "UI Automation worker output exceeded its bounded channel.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }
    private sealed class WorkerJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private WorkerJob() : base(true) { }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public nuint MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(WorkerJob job, int infoClass, ref ExtendedLimit limit, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(WorkerJob job, nint process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
        internal static WorkerJob Create()
        {
            var job = new WorkerJob();
            job.SetHandle(CreateJobObject(0, null));
            var limit = new ExtendedLimit
            {
                Basic = new BasicLimit { Flags = 0x2000 | 0x100 | 0x8, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = 512 * 1024 * 1024
            };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>()))
            {
                job.Dispose();
                throw new AgentLinkException("uia_unavailable", "Cannot establish the UIA worker's kill-on-close, single-process, 512 MiB job.");
            }
            return job;
        }
        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle))
                throw new AgentLinkException("uia_unavailable", "Cannot isolate the UIA worker in its bounded job.");
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
