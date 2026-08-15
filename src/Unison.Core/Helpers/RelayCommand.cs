using System;
using System.Windows.Input;

namespace Unison.Core.Helpers
{
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            if (!TryConvert(parameter, out T value))
            {
                return false;
            }

            return _canExecute == null || _canExecute(value);
        }

        public void Execute(object parameter)
        {
            if (TryConvert(parameter, out T value))
            {
                _execute(value);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        private static bool TryConvert(object parameter, out T value)
        {
            if (parameter is T matched)
            {
                value = matched;
                return true;
            }

            if (parameter == null)
            {
                value = default;
                return !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) != null;
            }

            try
            {
                value = (T)Convert.ChangeType(parameter, typeof(T));
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }
    }
}
