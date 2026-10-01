using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NovaClip.Infrastructure;

namespace NovaClip.App;

public sealed class SingleInstanceActivationEventArgs : EventArgs
{
    public SingleInstanceActivationEventArgs(string? argument) => Argument = argument;

    public string? Argument { get; }
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "NovaClip.SingleInstance.v1";
    private const string PipeName = "NovaClip.SingleInstance.v1";
    private const int MaxCommandBytes = 16_384;
    private const int MaxArgumentCharacters = 4_096;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Task _serverTask;
    private int _disposed;

    private SingleInstanceCoordinator(Mutex mutex)
    {
        _mutex = mutex;
        _serverTask = ListenAsync();
    }

    public event EventHandler<SingleInstanceActivationEventArgs>? ActivateRequested;

    public static async Task<SingleInstanceCoordinator?> AcquireOrForwardAsync(
        string? argument = null,
        CancellationToken cancellationToken = default)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }

            if (ownsMutex) return new SingleInstanceCoordinator(mutex);

            if (!await ForwardActivateAsync(argument, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Another NovaClip instance is already running but did not accept the activation request.");
            }

            mutex.Dispose();
            return null;
        }
        catch
        {
            if (ownsMutex)
            {
                try { mutex.ReleaseMutex(); } catch (Exception exception) when (exception is ApplicationException or ObjectDisposedException) { }
            }

            mutex.Dispose();
            throw;
        }
    }

    private static async Task<bool> ForwardActivateAsync(string? argument, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, leaveOpen: true)
            {
                AutoFlush = true
            };

            var boundedArgument = string.IsNullOrWhiteSpace(argument)
                ? null
                : argument.Trim() is { Length: <= MaxArgumentCharacters } normalized ? normalized : null;
            var payload = JsonSerializer.Serialize(new ActivationCommand("activate", boundedArgument));
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
                string? command;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopSource.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        command = await ActivationMessageReader.ReadAsync(server, MaxCommandBytes, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_stopSource.IsCancellationRequested) { continue; }
                    catch (Exception exception) when (exception is IOException or DecoderFallbackException) { continue; }
                }
                if (command is null) continue;

                try
                {
                    var activation = JsonSerializer.Deserialize<ActivationCommand>(command);
                    if (activation?.Type == "activate" &&
                        (activation.Argument is null || activation.Argument.Length <= MaxArgumentCharacters))
                    {
                        ActivateRequested?.Invoke(this, new SingleInstanceActivationEventArgs(activation.Argument));
                    }
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

    private sealed record ActivationCommand(string Type, string? Argument);
}
