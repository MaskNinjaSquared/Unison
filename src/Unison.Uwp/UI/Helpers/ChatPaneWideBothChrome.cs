// =============================================================================
// ChatPaneWideBothChrome
//
// After ChatPaneLayoutApply: WideBoth refreshes splitter chrome; narrow panes
// reset drag/hover only when the pane string actually changed.
// =============================================================================
using System;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatPaneWideBothChrome
    {
        public static void AfterPaneApplied(
            bool wideBoth,
            bool paneChanged,
            Action applyWideChrome,
            Action resetNarrowSplitter)
        {
            if (wideBoth)
            {
                applyWideChrome?.Invoke();
                return;
            }

            if (paneChanged)
            {
                resetNarrowSplitter?.Invoke();
            }
        }
    }
}
