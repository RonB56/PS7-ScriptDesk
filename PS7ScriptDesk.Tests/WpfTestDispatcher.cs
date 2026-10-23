using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace PS7ScriptDesk.Tests;

internal static class WpfTestDispatcher
{
    private static readonly object Gate = new();
    private static WpfDispatcherHost? _host;

    public static Dispatcher Dispatcher => GetHost().Dispatcher;

    public static void Start()
    {
        lock (Gate)
        {
            if (_host is not null) return;
            var host = new WpfDispatcherHost();
            host.Start();
            _host = host;
        }
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        GetHost().Run(action);
    }

    public static void Stop()
    {
        WpfDispatcherHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }
        host?.Dispose();
    }

    private static WpfDispatcherHost GetHost()
    {
        lock (Gate)
        {
            return _host ?? throw new InvalidOperationException("The WPF test collection fixture has not initialized its dispatcher.");
        }
    }

    private sealed class WpfDispatcherHost : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lifecycleGate = new();
        private Thread? _thread;
        private Dispatcher? _dispatcher;
        private PS7ScriptDesk.Shell.App? _application;
        private bool _disposed;

        public Dispatcher Dispatcher => _dispatcher ?? throw new InvalidOperationException("The WPF test dispatcher is not running.");

        public void Start()
        {
            lock (_lifecycleGate)
            {
                _thread = new Thread(RunDispatcher)
                {
                    IsBackground = true,
                    Name = "PS7 ScriptDesk WPF test collection dispatcher"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            var startupFailure = _startup.Task.GetAwaiter().GetResult();
            if (startupFailure is not null) ExceptionDispatchInfo.Capture(startupFailure).Throw();
            _ready.Task.GetAwaiter().GetResult();
        }

        public void Run(Action action)
        {
            lock (_lifecycleGate) ObjectDisposedException.ThrowIf(_disposed, this);
            Exception? failure = null;
            Dispatcher.Invoke(() =>
            {
                try { action(); }
                catch (Exception exception) { failure = exception; }
            });
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
            {
                _dispatcher.Invoke(() =>
                {
                    foreach (var window in System.Windows.Application.Current?.Windows.OfType<Window>().ToArray() ?? [])
                    {
                        if (window.IsVisible) window.Close();
                    }
                    _application?.Shutdown();
                    _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                });
            }

            _thread?.Join(TimeSpan.FromSeconds(10));
            _thread = null;
            _dispatcher = null;
            _application = null;
        }

        private void RunDispatcher()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _application = new PS7ScriptDesk.Shell.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                _application.InitializeComponent();
                _ready.TrySetResult(_dispatcher);
                _startup.TrySetResult(null);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                _startup.TrySetResult(exception);
                _ready.TrySetException(exception);
            }
        }
    }
}
