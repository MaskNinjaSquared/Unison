using Unison.Core.ViewModels;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Key and caret handling the three chat surfaces share for the @ menu.
    /// </summary>
    public static class MentionComposerInput
    {
        public static void OnSelectionChanged(ChatDetailViewModel viewModel, TextBox box)
        {
            if (viewModel == null || box == null)
            {
                return;
            }

            if (viewModel.MentionInsertCaret >= 0)
            {
                int caret = viewModel.MentionInsertCaret;
                box.SelectionStart = caret;
                viewModel.UpdateMentionQuery(box.Text, caret);
                return;
            }

            viewModel.UpdateMentionQuery(box.Text, box.SelectionStart);
        }

        /// <summary>True when the key was consumed by the mention menu.</summary>
        public static bool OnKeyDown(ChatDetailViewModel viewModel, TextBox box, KeyRoutedEventArgs e)
        {
            if (viewModel == null || !viewModel.IsMentionMenuOpen || e == null)
            {
                return false;
            }

            if (e.Key == VirtualKey.Up)
            {
                viewModel.MoveMentionSelection(-1);
                e.Handled = true;
                return true;
            }

            if (e.Key == VirtualKey.Down)
            {
                viewModel.MoveMentionSelection(1);
                e.Handled = true;
                return true;
            }

            if (e.Key != VirtualKey.Enter)
            {
                return false;
            }

            CoreVirtualKeyStates shift = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);
            if ((shift & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            {
                return false;
            }

            if (!viewModel.TryAcceptSelectedMention())
            {
                return false;
            }

            if (box != null && viewModel.MentionInsertCaret >= 0)
            {
                box.SelectionStart = viewModel.MentionInsertCaret;
            }

            e.Handled = true;
            return true;
        }
    }
}
