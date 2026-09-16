using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PS7ScriptDesk.UI.Commands
{
    public class RelayCommand : ICommand
    {
        private readonly Action? _executeWithoutParameter;
        private readonly Func<bool>? _canExecuteWithoutParameter;

        private readonly Action<object?>? _executeWithParameter;
        private readonly Func<object?, bool>? _canExecuteWithParameter;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _executeWithoutParameter = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecuteWithoutParameter = canExecute;
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _executeWithParameter = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecuteWithParameter = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            if (_canExecuteWithParameter is not null)
            {
                return _canExecuteWithParameter(parameter);
            }

            if (_canExecuteWithoutParameter is not null)
            {
                return _canExecuteWithoutParameter();
            }

            return true;
        }

        public void Execute(object? parameter)
        {
            if (_executeWithParameter is not null)
            {
                _executeWithParameter(parameter);
                return;
            }

            _executeWithoutParameter?.Invoke();
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Owns an asynchronous ICommand execution and observes its task.</summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private readonly SynchronizationContext? _notificationContext = SynchronizationContext.Current;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public Task? ExecutionTask { get; private set; }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            try
            {
                ExecutionTask = _execute();
                _ = ObserveAsync(ExecutionTask);
            }
            catch (Exception ex)
            {
                ExecutionTask = Task.FromException(ex);
                _ = ObserveAsync(ExecutionTask);
            }
        }

        public void RaiseCanExecuteChanged()
        {
            if (_notificationContext is not null && SynchronizationContext.Current != _notificationContext)
            {
                _notificationContext.Post(_ => CanExecuteChanged?.Invoke(this, EventArgs.Empty), null);
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The observer owns the fault so it cannot surface later as an
                // unobserved task exception. The workflow owns its UI error state.
            }
        }
    }
}
