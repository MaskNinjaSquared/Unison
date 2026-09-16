using System;
using System.Threading.Tasks;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Chats / Archived shell pages (WhatsApp <c>UI/Views</c> or Unison <c>Shell/Unison/Views</c>).
    /// </summary>
    internal interface IConversationShellPage
    {
        event EventHandler MenuClicked;

        void NotifyLocalConversationsCleared();

        bool TryHandleBack();

        Task ResetForLoggedOutAsync();

        /// <summary>Toast/tile deep-link while Chats is showing; no-op on Archived.</summary>
        void RequestOpenPendingDeepLink();
    }
}
