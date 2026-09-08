using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NovaClip.App;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "NovaClip.SingleInstance.v1";
    private const string PipeName = "NovaClip.SingleInstance.v1";
    private const int MaxCommandBytes = 16_384;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Task _serverTask;
    private int _disposed;

    private SingleInstanceCoordinator(Mutex mutex)
    {
        _mutex = mutex;
        _serverTask = ListenAsync();
    }

    public event EventHandler? ActivateRequested;

    public static async Task<SingleInstanceCoordinator?> AcquireOrForwardAsync(CancellationToken cancellationToken = default)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }

            if (ownsMutex) return new SingleInstanceCoordinator(mutex);

            mutex.Dispose();
            if (!await ForwardActivateAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Another NovaClip instance is already running but did not accept the activation request.");
            }
            return null;
        }
        catch
        {
            if (!ownsMutex) mutex.Dispose();
            throw;
        }
    }

    private static async Task<bool> ForwardActivateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, leaveOpen: true)
            {
                AutoFlush = true
            };
            var payload = JsonSerializer.Serialize(new ActivationCommand("activate"));
            if (Encoding.UTF8.GetByteCount(payload) > MaxCommandBytes) return false;
            await writer.WriteLineAsync(payload).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stopSource.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stopSource.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 256, leaveOpen: false);
                var command = await reader.ReadLineAsync().ConfigureAwait(false);
                if (command is null || Encoding.UTF8.GetByteCount(command) > MaxCommandBytes) continue;
                try
                {
                    var activation = JsonSerializer.Deserialize<ActivationCommand>(command);
                    if (activation?.Type == "activate") ActivateRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (JsonException)
                {
                    // Ignore malformed activation requests.
                }
            }
        }
        catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Warning("Single-instance IPC listener stopped.", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopSource.Cancel();
        try { _serverTask.Wait(1000); } catch { }
        _stopSource.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (Exception exception) when (exception is ApplicationException or ObjectDisposedException) { }
        _mutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record ActivationCommand(string Type);
}
