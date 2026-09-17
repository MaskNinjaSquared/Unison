namespace Unison.Core.Helpers
{
    /// <summary>
    /// What an arriving message does to the unread badge and to the toast.
    /// </summary>
    /// <remarks>
    /// One decision with two outputs, because they answer the same question — is the user looking
    /// at this conversation right now — and they used to answer it differently. The badge asked
    /// only whether the chat was open; the toast asked whether the chat was open *and* the window
    /// was visible. Nothing closes the open conversation when the app goes to the background, so a
    /// message arriving then was announced by a toast and never counted as unread: dismiss the
    /// toast and the conversation looked like nothing had happened.
    /// </remarks>
    public struct IncomingAttention
    {
        private IncomingAttention(bool countsAsUnread, bool suppressToast)
        {
            CountsAsUnread = countsAsUnread;
            SuppressToast = suppressToast;
        }

        /// <summary>Whether the conversation should show one more waiting message.</summary>
        public bool CountsAsUnread { get; private set; }

        /// <summary>Whether the toast would be telling the user something already on their screen.</summary>
        public bool SuppressToast { get; private set; }

        /// <param name="isChatOpen">The conversation is the one the detail view has open.</param>
        /// <param name="isWindowVisible">The app's window is on screen.</param>
        public static IncomingAttention For(bool isFromMe, bool isChatOpen, bool isWindowVisible)
        {
            if (isFromMe)
            {
                return new IncomingAttention(false, true);
            }

            // Open behind a minimised app is not on screen. This is the whole point of the type.
            bool onScreen = isChatOpen && isWindowVisible;

            return new IncomingAttention(!onScreen, onScreen);
        }
    }
}
