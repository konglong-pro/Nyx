using System.Windows.Threading;

namespace Nyx.App;

internal sealed class CoalescingProgress<T> : IProgress<T>, IDisposable
{
    private readonly object _gate = new();
    private readonly DispatcherTimer _timer;
    private readonly Action<T> _report;
    private T? _latest;
    private bool _hasLatest;
    private bool _disposed;

    internal CoalescingProgress(Dispatcher dispatcher, Action<T> report)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(report);
        dispatcher.VerifyAccess();
        _report = report;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _latest = value;
            _hasLatest = true;
        }
    }

    internal void Flush()
    {
        _timer.Dispatcher.VerifyAccess();
        T value;
        lock (_gate)
        {
            if (!_hasLatest)
            {
                return;
            }

            value = _latest!;
            _latest = default;
            _hasLatest = false;
        }

        _report(value);
    }

    public void Dispose()
    {
        _timer.Dispatcher.VerifyAccess();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        Flush();
    }

    private void OnTick(object? sender, EventArgs e) => Flush();
}
