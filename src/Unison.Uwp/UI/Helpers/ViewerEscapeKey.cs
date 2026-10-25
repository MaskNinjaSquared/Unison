using System;
using System.Windows.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// While a media viewer overlay is open, Escape runs <see cref="ICommand"/> close
    /// (same path as the chrome close button / system Back).
    /// </summary>
    internal sealed class ViewerEscapeKey
    {
        private readonly Func<bool> _isActive;
        private readonly Func<ICommand> _closeCommand;
        private bool _hooked;

        public ViewerEscapeKey(Func<bool> isActive, Func<ICommand> closeCommand)
        {
            _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
            _closeCommand = closeCommand ?? throw new ArgumentNullException(nameof(closeCommand));
        }

        public void Hook()
        {
            if (_hooked)
            {
                return;
            }

            Window window = Window.Current;
            if (window?.CoreWindow == null)
            {
                return;
            }

            window.CoreWindow.KeyDown += CoreWindow_KeyDown;
            _hooked = true;
        }

        public void Unhook()
        {
            if (!_hooked)
            {
                return;
            }

            Window window = Window.Current;
            if (window?.CoreWindow != null)
            {
                window.CoreWindow.KeyDown -= CoreWindow_KeyDown;
            }

            _hooked = false;
        }

        private void CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (args == null || args.VirtualKey != VirtualKey.Escape)
            {
                return;
            }

            if (_isActive == null || !_isActive())
            {
                return;
            }

            ICommand close = _closeCommand();
            if (close == null || !close.CanExecute(null))
            {
                return;
            }

            close.Execute(null);
            args.Handled = true;
        }
    }
}
