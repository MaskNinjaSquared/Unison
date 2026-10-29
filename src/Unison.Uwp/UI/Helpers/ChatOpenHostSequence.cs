// =============================================================================
// ChatOpenHostSequence
//
// List / archived tap → PreferFast + Complete. Keeps Unison / WhatsApp / UI
// ChatsView copies from drifting on the open finish path.
//
// Host contract (Mobile hang avoidance):
//   1) PrepareActiveChatAsync — chrome only (BindActiveChatForOpen; no roster/avatar IQ)
//   2) SelectChat + ApplyChatPaneState — leave the list immediately
//   3) CompleteListTapOpenAsync — ProgressBar → yield paint → enrich → LoadMessages
// =============================================================================
using System.Threading.Tasks;
using Unison.Uwp.UI.Views;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatOpenHostSequence
    {
        /// <summary>
        /// After Prepare + SelectChat + pane apply: ProgressBar, paint yield, enrich, bubbles.
        /// PreferFast skips the Mobile 2s materialize delay on list taps.
        /// </summary>
        public static Task CompleteListTapOpenAsync(IChatDetailSurface detail)
        {
            if (detail == null)
            {
                return Task.CompletedTask;
            }

            detail.PreferFastTimelineLoad = true;
            return detail.CompleteActiveChatLoadAsync();
        }
    }
}
