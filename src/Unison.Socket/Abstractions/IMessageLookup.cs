// =============================================================================
// IMessageLookup
//
// Inversion of control for message retries. When a peer fails to decrypt one of
// our messages it asks for a resend, and we must produce the original content.
// Rather than let the socket keep a message store, it asks the host through this
// interface - the reason the socket layer owns no chat history at all.
//
// Ports: rc14 getMessage in src/Types/Socket.ts
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    /// <summary>
    /// Lets the socket ask the host for a previously sent message when answering a retry receipt.
    /// Inverting this is what keeps message history out of the socket layer.
    /// </summary>
    public interface IMessageLookup
    {
        /// <summary>Returns the message for the key, or null when the host no longer has it.</summary>
        Task<global::Proto.Message> GetMessageAsync(global::Proto.MessageKey key);
    }
}
