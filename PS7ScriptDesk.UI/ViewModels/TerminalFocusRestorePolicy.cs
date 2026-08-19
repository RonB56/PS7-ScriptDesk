using System;

namespace PS7ScriptDesk.UI.ViewModels
{
    /// <summary>
    /// Owns the one-time, generation-bound focus intent used when Reset Console replaces
    /// a terminal session. This is deliberately UI-framework independent so the ViewModel
    /// can be characterized without launching WebView2.
    /// </summary>
    public sealed class TerminalFocusRestorePolicy
    {
        private readonly object _syncRoot = new();
        private long _nextIntentId;
        private FocusIntent? _pendingIntent;

        public bool HasPendingIntent
        {
            get
            {
                lock (_syncRoot)
                {
                    return _pendingIntent is not null;
                }
            }
        }

        public TerminalFocusRestoreIntent Capture(bool terminalHadFocus, int previousGeneration)
        {
            lock (_syncRoot)
            {
                if (!terminalHadFocus)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreIntent.None;
                }

                var intent = new FocusIntent(++_nextIntentId, previousGeneration);
                _pendingIntent = intent;
                return new TerminalFocusRestoreIntent(intent.Id, intent.PreviousGeneration);
            }
        }

        public bool BindReplacementGeneration(int generation)
        {
            lock (_syncRoot)
            {
                if (_pendingIntent is null || generation <= _pendingIntent.PreviousGeneration)
                {
                    return false;
                }

                _pendingIntent.ReplacementGeneration = generation;
                return true;
            }
        }

        public bool Cancel()
        {
            lock (_syncRoot)
            {
                if (_pendingIntent is null)
                {
                    return false;
                }

                _pendingIntent = null;
                return true;
            }
        }

        public TerminalFocusRestoreDecision TryConsume(
            int observedGeneration,
            TerminalFocusRestoreReadiness readiness)
        {
            lock (_syncRoot)
            {
                if (_pendingIntent is null)
                {
                    return TerminalFocusRestoreDecision.NoPendingIntent;
                }

                if (_pendingIntent.ReplacementGeneration is null)
                {
                    return TerminalFocusRestoreDecision.WaitingForReplacementGeneration;
                }

                if (_pendingIntent.ReplacementGeneration != observedGeneration)
                {
                    return TerminalFocusRestoreDecision.StaleGeneration;
                }

                if (!readiness.RendererReady)
                {
                    return TerminalFocusRestoreDecision.RendererNotReady;
                }

                if (!readiness.ConsoleVisible)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ConsoleHidden;
                }

                if (!readiness.ApplicationActive)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ApplicationInactive;
                }

                if (readiness.ModalDialogOpen)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ModalDialogOpen;
                }

                _pendingIntent = null;
                return TerminalFocusRestoreDecision.Restore;
            }
        }

        /// <summary>
        /// Starts a verified focus attempt without consuming the intent. The caller must
        /// complete the attempt so a failed browser-side focus check can retry once for
        /// the same replacement generation only.
        /// </summary>
        public TerminalFocusRestoreDecision TryBeginFocusAttempt(
            int observedGeneration,
            TerminalFocusRestoreReadiness readiness)
        {
            lock (_syncRoot)
            {
                if (_pendingIntent is null)
                {
                    return TerminalFocusRestoreDecision.NoPendingIntent;
                }

                if (_pendingIntent.ReplacementGeneration is null)
                {
                    return TerminalFocusRestoreDecision.WaitingForReplacementGeneration;
                }

                if (_pendingIntent.ReplacementGeneration != observedGeneration)
                {
                    return TerminalFocusRestoreDecision.StaleGeneration;
                }

                if (_pendingIntent.FocusAttemptInProgress)
                {
                    return TerminalFocusRestoreDecision.FocusAttemptInProgress;
                }

                if (!readiness.RendererReady)
                {
                    return TerminalFocusRestoreDecision.RendererNotReady;
                }

                if (!readiness.ConsoleVisible)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ConsoleHidden;
                }

                if (!readiness.ApplicationActive)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ApplicationInactive;
                }

                if (readiness.ModalDialogOpen)
                {
                    _pendingIntent = null;
                    return TerminalFocusRestoreDecision.ModalDialogOpen;
                }

                _pendingIntent.FocusAttemptInProgress = true;
                return TerminalFocusRestoreDecision.Restore;
            }
        }

        public bool CompleteFocusAttempt(int observedGeneration, bool succeeded)
        {
            lock (_syncRoot)
            {
                if (_pendingIntent is null ||
                    _pendingIntent.ReplacementGeneration != observedGeneration ||
                    !_pendingIntent.FocusAttemptInProgress)
                {
                    return false;
                }

                _pendingIntent.FocusAttemptInProgress = false;
                if (succeeded)
                {
                    _pendingIntent = null;
                    return false;
                }

                _pendingIntent.FocusAttemptCount++;
                if (_pendingIntent.FocusAttemptCount < 2)
                {
                    return true;
                }

                _pendingIntent = null;
                return false;
            }
        }

        private sealed class FocusIntent
        {
            public FocusIntent(long id, int previousGeneration)
            {
                Id = id;
                PreviousGeneration = previousGeneration;
            }

            public long Id { get; }
            public int PreviousGeneration { get; }
            public int? ReplacementGeneration { get; set; }
            public bool FocusAttemptInProgress { get; set; }
            public int FocusAttemptCount { get; set; }
        }
    }

    public readonly record struct TerminalFocusRestoreIntent(long Id, int PreviousGeneration)
    {
        public static TerminalFocusRestoreIntent None { get; } = new(0, 0);
        public bool IsRequested => Id != 0;
    }

    public readonly record struct TerminalFocusRestoreReadiness(
        bool RendererReady,
        bool ConsoleVisible,
        bool ApplicationActive,
        bool ModalDialogOpen);

    public readonly record struct TerminalFocusRestoreResult(
        bool WpfHostFocused,
        bool WebViewFocused,
        bool BrowserFocusCommandExecuted,
        bool XtermInputActive,
        string? ActiveElement,
        string? FailureReason)
    {
        public bool Succeeded => WpfHostFocused && WebViewFocused && BrowserFocusCommandExecuted && XtermInputActive;
    }

    public enum TerminalFocusRestoreDecision
    {
        NoPendingIntent,
        WaitingForReplacementGeneration,
        StaleGeneration,
        RendererNotReady,
        FocusAttemptInProgress,
        ConsoleHidden,
        ApplicationInactive,
        ModalDialogOpen,
        Restore
    }
}
