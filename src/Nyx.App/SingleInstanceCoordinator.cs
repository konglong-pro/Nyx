using System.IO.Pipes;
using System.IO;
using System.Text.Json;

namespace Nyx.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Nyx.FileShredder.v1";
    private const string PipeName = "Nyx.FileShredder.v1";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public void StartListening(Action<ForwardedLaunch> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        if (!IsPrimary || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = ListenAsync(receive, _listenerCancellation.Token);
    }

    public static bool ForwardToPrimary(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                client.Connect(200);
                JsonSerializer.Serialize(client, new ForwardedLaunch(paths));
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        _listenerCancellation.Dispose();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    private static async Task ListenAsync(
        Action<ForwardedLaunch> receive,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                var launch = await JsonSerializer.DeserializeAsync<ForwardedLaunch>(
                    server,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (launch?.Paths is not null)
                {
                    receive(launch);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client can disappear while forwarding. Keep serving future launches.
            }
            catch (JsonException)
            {
                // Ignore malformed messages from unrelated local processes.
            }
        }
    }
}

internal sealed record ForwardedLaunch(IReadOnlyList<string> Paths);
