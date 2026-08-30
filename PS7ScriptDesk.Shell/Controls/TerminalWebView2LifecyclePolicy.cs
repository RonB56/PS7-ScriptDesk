using System;

namespace PS7ScriptDesk.Shell.Controls
{
    internal enum TerminalWebView2LifecycleState
    {
        Created,
        Initializing,
        Ready,
        Faulted,
        Disposing,
        Disposed
    }

    internal sealed class TerminalWebView2LifecyclePolicy
    {
        private readonly object _syncRoot = new();
        private TerminalWebView2LifecycleState _state = TerminalWebView2LifecycleState.Created;

        public TerminalWebView2LifecycleState State
        {
            get
            {
                lock (_syncRoot)
                {
                    return _state;
                }
            }
        }

        public bool CanUseRenderer
        {
            get
            {
                lock (_syncRoot)
                {
                    return _state == TerminalWebView2LifecycleState.Ready;
                }
            }
        }

        public bool CanAcceptRendererCallback
        {
            get
            {
                lock (_syncRoot)
                {
                    return _state == TerminalWebView2LifecycleState.Initializing ||
                           _state == TerminalWebView2LifecycleState.Ready;
                }
            }
        }

        public bool IsRetired
        {
            get
            {
                lock (_syncRoot)
                {
                    return _state == TerminalWebView2LifecycleState.Faulted ||
                           _state == TerminalWebView2LifecycleState.Disposing ||
                           _state == TerminalWebView2LifecycleState.Disposed;
                }
            }
        }

        public bool TryBeginInitialization()
        {
            lock (_syncRoot)
            {
                if (_state != TerminalWebView2LifecycleState.Created)
                {
                    return false;
                }

                _state = TerminalWebView2LifecycleState.Initializing;
                return true;
            }
        }

        public bool MarkReady()
        {
            lock (_syncRoot)
            {
                if (_state != TerminalWebView2LifecycleState.Initializing)
                {
                    return false;
                }

                _state = TerminalWebView2LifecycleState.Ready;
                return true;
            }
        }

        public bool MarkFaulted()
        {
            lock (_syncRoot)
            {
                if (_state == TerminalWebView2LifecycleState.Faulted ||
                    _state == TerminalWebView2LifecycleState.Disposed)
                {
                    return false;
                }

                _state = TerminalWebView2LifecycleState.Faulted;
                return true;
            }
        }

        public bool MarkDisposed()
        {
            lock (_syncRoot)
            {
                if (_state == TerminalWebView2LifecycleState.Disposed)
                {
                    return false;
                }

                _state = TerminalWebView2LifecycleState.Disposed;
                return true;
            }
        }

        public bool TryBeginDisposal()
        {
            lock (_syncRoot)
            {
                if (_state == TerminalWebView2LifecycleState.Disposing ||
                    _state == TerminalWebView2LifecycleState.Disposed)
                {
                    return false;
                }

                _state = TerminalWebView2LifecycleState.Disposing;
                return true;
            }
        }
    }
}
