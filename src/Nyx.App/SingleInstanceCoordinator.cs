using System.IO.Pipes;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;

namespace Nyx.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly string EndpointSuffix = GetEndpointSuffix();
    private static readonly string MutexName = @"Local\Nyx.FileShredder.v1." + EndpointSuffix;
    private static readonly string PipeName = "Nyx.FileShredder.v1." + EndpointSuffix;
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

        byte[] payload;
        try
        {
            payload = LaunchMessageCodec.Encode(paths);
        }
        catch (InvalidDataException)
        {
            return false;
        }

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
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                client.WriteAsync(payload, timeout.Token).AsTask().GetAwaiter().GetResult();
                client.FlushAsync(timeout.Token).GetAwaiter().GetResult();
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
            catch (OperationCanceledException)
            {
                return false;
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var launch = await LaunchMessageCodec.ReadAsync(server, timeout.Token)
                    .ConfigureAwait(false);
                if (launch?.Paths is not null)
                {
                    receive(launch);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A connected client must finish its bounded message promptly.
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

    private static string GetEndpointSuffix()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"{identity.User?.Value ?? throw new InvalidOperationException("The current user identity is unavailable.")}.{process.SessionId}";
    }
}

internal sealed record ForwardedLaunch(IReadOnlyList<string> Paths);
