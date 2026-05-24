using System;
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
}