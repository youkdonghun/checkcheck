using System.Threading;
using System.Windows.Threading;

namespace CheckCheck.App;

/// <summary>One running copy per Windows session; later launches restore its window.</summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle? _listener;
    private bool _disposed;

    internal SingleInstanceCoordinator(Dispatcher dispatcher, Action restore, string name = @"Local\CheckCheck.Desktop.v1")
    {
        _mutex = new Mutex(true, name + ".Instance", out var createdNew);
        IsPrimary = createdNew;
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        if (IsPrimary)
        {
            _listener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
            {
                if (_disposed || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(() => { if (!_disposed) restore(); });
            }, null, Timeout.Infinite, false);
        }
    }

    internal bool IsPrimary { get; }

    internal void RequestActivation() => _activation.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener?.Unregister(null);
        _activation.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* Cleanup must still release the OS handle. */ }
        }
        _mutex.Dispose();
    }
}
