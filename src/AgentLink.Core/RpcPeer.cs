using System.Collections.Concurrent;

namespace AgentLink.Core;

public sealed class RpcPeer : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Func<OperationRequest, CancellationToken, Task<OperationResult>>? _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OperationResult>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly ConcurrentDictionary<string, Task> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _cancelled = new();
    private int _disposed;

    public RpcPeer(Stream stream, Func<OperationRequest, CancellationToken, Task<OperationResult>>? handler = null)
    {
        _stream = stream;
        _handler = handler;
        Completion = ReadLoopAsync();
    }

    public Task Completion { get; }

    public async Task<OperationResult> CallAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        await _slots.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var sent = false;
        try
        {
            await SendAsync(new("request", id, request.Method, request.Arguments, ActorId: request.ActorId), cancellationToken);
            sent = true;
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sent && !_lifetime.IsCancellationRequested)
            {
                if (_cancelled.Count >= 128)
                    _lifetime.Cancel();
                else
                {
                    _cancelled.TryAdd(id, 0);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(new("cancel", id), timeout.Token);
                }
            }
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _slots.Release();
        }
    }

    private async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _writer.WaitAsync(linked.Token);
        try
        {
            await Wire.WriteAsync(_stream, message, linked.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // A partially written frame cannot be followed by another message.
            _lifetime.Cancel();
            _stream.Dispose();
            throw;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception reason = new EndOfStreamException("Peer disconnected; unacknowledged operations may have executed.");
        try
        {
            while (await Wire.ReadAsync(_stream, _lifetime.Token) is { } message)
            {
                if (message.Kind == "response")
                {
                    if (_pending.TryRemove(message.Id, out var completion))
                    {
                        _cancelled.TryRemove(message.Id, out _);
                        if (message.Error is { } error)
                            completion.TrySetException(new AgentLinkException(error.Code, error.Message));
                        else
                            completion.TrySetResult(message.Result!);
                    }
                    else if (!_cancelled.TryRemove(message.Id, out _))
                        throw new AgentLinkException("invalid_response", "Unsolicited or duplicate response.");
                }
                else if (message.Kind == "cancel")
                {
                    if (_incoming.TryGetValue(message.Id, out var operation))
                    {
                        try { await operation.CancelAsync(); }
                        catch (ObjectDisposedException)
                        {
                            // Completion can win a race with cancellation already read from the wire.
                        }
                    }
                }
                else
                {
                    if (_handler is null)
                        throw new AgentLinkException("reverse_call_denied", "This endpoint does not accept reverse requests.");
                    if (_incoming.Count >= 16)
                    {
                        await SendAsync(new("response", message.Id, Error: new("busy", "Too many outstanding operations.")), _lifetime.Token);
                        continue;
                    }
                    var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    if (!_incoming.TryAdd(message.Id, operation))
                    {
                        operation.Dispose();
                        throw new AgentLinkException("duplicate_request", "Request ID is already active.");
                    }
                    var task = Task.Run(() => HandleAsync(message, operation));
                    _handlers[message.Id] = task;
                    _ = task.ContinueWith(_ => _handlers.TryRemove(message.Id, out var ignored),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or AgentLinkException or ObjectDisposedException)
        {
            reason = exception;
        }
        finally
        {
            await _lifetime.CancelAsync();
            foreach (var pending in _pending.Values)
                pending.TrySetException(reason);
        }
    }

    private async Task HandleAsync(WireMessage message, CancellationTokenSource operation)
    {
        WireMessage response;
        try
        {
            var result = await _handler!(new(message.Method!, message.Arguments!.Value, message.ActorId), operation.Token);
            response = new("response", message.Id, Result: result);
        }
        catch (AgentLinkException exception)
        {
            response = new("response", message.Id, Error: new(exception.Code, exception.Message));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            response = new("response", message.Id, Error: new("cancelled", "Operation was cancelled; observe state before retrying a mutation."));
        }
        catch (Exception exception)
        {
            // Isolate handler faults at the RPC boundary; do not expose remote stacks.
            Diagnostics.Write(DiagnosticEvent.RpcFailed, DiagnosticLevel.Error, operation: message.Method,
                correlation: Guid.ParseExact(message.Id, "N"), exception: exception);
            response = new("response", message.Id, Error: new("operation_failed",
                "The operation failed and may have executed. Inspect task or desktop state before retrying a mutation. See endpoint diagnostics."));
        }
        try
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try { await SendAsync(response, _lifetime.Token); }
                catch (AgentLinkException exception)
                {
                    // Size/schema rejection happens before any bytes are written.
                    await SendAsync(new("response", message.Id,
                        Error: new(exception.Code, exception.Message)), _lifetime.Token);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Diagnostics.Write(DiagnosticEvent.RpcDeliveryFailed, DiagnosticLevel.Warning, operation: message.Method,
                correlation: Guid.ParseExact(message.Id, "N"), exception: exception);
        }
        finally
        {
            _incoming.TryRemove(message.Id, out _);
            operation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync();
        _stream.Dispose();
        await Completion;
        await Task.WhenAll(_handlers.Values).WaitAsync(TimeSpan.FromSeconds(10));
        _lifetime.Dispose();
        // Pending callers release these managed-only semaphores in their finally
        // blocks. Disposing them here would race those continuations.
    }
}
