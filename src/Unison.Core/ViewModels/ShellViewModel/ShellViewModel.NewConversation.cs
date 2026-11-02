// =============================================================================
// ShellViewModel.NewConversation
//
// Hosts the New Conversation overlay ViewModel and opens chats it requests.
// =============================================================================
using System;

namespace Unison.Core.ViewModels
{
    public partial class ShellViewModel
    {
        private NewConversationViewModel _newConversation;
        private bool _newConversationHooked;

        /// <summary>Dedicated surface for the New Conversation overlay (list + action tiles).</summary>
        public NewConversationViewModel NewConversation => _newConversation;

        private void InitNewConversation(NewConversationViewModel newConversation)
        {
            _newConversation = newConversation;
            if (_newConversation == null || _newConversationHooked)
            {
                return;
            }

            _newConversation.ChatOpenRequested += OnNewConversationChatOpenRequested;
            _newConversationHooked = true;
        }

        private void OnNewConversationChatOpenRequested(object sender, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            QueueOpenChatFromActivation(
                "chat=" + Uri.EscapeDataString(jid),
                delayChatListUntilConversationReady: false);
        }
    }
}
