using Unison.Core.ViewModels;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Ctrl+V on an open chat: when the clipboard has media, intercept and paste-send;
    /// plain text is left to the TextBox.
    /// </summary>
    internal static class ChatDetailClipboardPaste
    {
        public static bool OnKeyDown(ChatDetailViewModel viewModel, KeyRoutedEventArgs e)
        {
            if (viewModel == null || e == null || e.Handled)
            {
                return false;
            }

            if (e.Key != VirtualKey.V)
            {
                return false;
            }

            CoreVirtualKeyStates ctrl = WindowKeyState(VirtualKey.Control);
            if ((ctrl & CoreVirtualKeyStates.Down) != CoreVirtualKeyStates.Down)
            {
                return false;
            }

            if (!viewModel.TryBeginPasteClipboardMedia())
            {
                return false;
            }

            e.Handled = true;
            return true;
        }

        private static CoreVirtualKeyStates WindowKeyState(VirtualKey key)
        {
            Windows.UI.Xaml.Window window = Windows.UI.Xaml.Window.Current;
            if (window?.CoreWindow == null)
            {
                return CoreVirtualKeyStates.None;
            }

            return window.CoreWindow.GetKeyState(key);
        }
    }
}
