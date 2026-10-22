// =============================================================================
// ChatListVisibleReconcileResult
//
// Outcome of one budgeted VisibleChats reconcile pass. Lives in Core so
// ViewModels and IChatService can name it without importing WhatsAppClient.
// =============================================================================
namespace Unison.Core.Helpers
{
    public sealed class ChatListVisibleReconcileResult
    {
        public ChatListVisibleReconcileResult(int mutations, bool incomplete)
        {
            Mutations = mutations;
            Incomplete = incomplete;
        }

        public int Mutations { get; }

        public bool Incomplete { get; }
    }
}
