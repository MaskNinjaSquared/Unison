using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Key and caret handling the chat surfaces share for the @ menu (RichEdit composer).
    /// </summary>
    public static class MentionComposerInput
    {
        public static void OnSelectionChanged(ChatDetailViewModel viewModel, ChatComposerInput box)
        {
            if (viewModel == null || box == null)
            {
                return;
            }

            // BoundText / emoji font passes temporarily move selection; do not treat that as
            // a caret edit or clear MentionInsertCaret before the picker finishes placing it.
            if (box.IsDocumentSyncing)
            {
                return;
            }

            if (viewModel.MentionInsertCaret >= 0)
            {
                int caret = viewModel.MentionInsertCaret;
                box.CaretIndex = caret;
                viewModel.UpdateMentionQuery(box.GetPlainText(), caret);
                return;
            }

            viewModel.UpdateMentionQuery(box.GetPlainText(), box.CaretIndex);
        }

        /// <summary>True when the key was consumed by the mention menu.</summary>
        public static bool OnKeyDown(ChatDetailViewModel viewModel, ChatComposerInput box, KeyRoutedEventArgs e)
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
                box.CaretIndex = viewModel.MentionInsertCaret;
            }

            e.Handled = true;
            return true;
        }
    }
}
