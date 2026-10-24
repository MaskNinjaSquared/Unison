// =============================================================================
// ChatOpenHostSequence
//
// List / archived tap → PreferFast + Complete. Keeps Unison / WhatsApp / UI
// ChatsView copies from drifting on the open finish path.
// =============================================================================
using System.Threading.Tasks;
using Unison.Uwp.UI.Views;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatOpenHostSequence
    {
        /// <summary>
        /// After Prepare + SelectChat + pane apply: skip Mobile 2s delay and load bubbles.
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
