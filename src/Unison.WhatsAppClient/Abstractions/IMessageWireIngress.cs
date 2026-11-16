using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Proto-shaped entry points of the message façade. Only the client and host call these;
    /// Core sees <see cref="IMessageService"/>.
    /// </summary>
    public interface IMessageWireIngress : IMessageService
    {
        /// <summary>
        /// Persists people from the payload (UpsertIfChanged), then applies history sync.
        /// </summary>
        Task SyncMessageHistoryAsync(HistorySync sync);

        /// <summary>
        /// Pump entry: assemble quiet/visibility/active-chat context and route the envelope.
        /// </summary>
        Task NotifyIncomingDecryptedAsync(IncomingDecryptedNotifyRequest request);
    }
}
