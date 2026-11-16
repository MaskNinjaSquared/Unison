using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Proto-shaped entry point of the status façade. Only the message façade calls it;
    /// Core sees <see cref="IStatusService"/>.
    /// </summary>
    public interface IStatusWireIngress : IStatusService
    {
        /// <summary>
        /// Live status@broadcast: build a <c>HistoryStatus</c> from the envelope and
        /// ingest (thumb + store).
        /// </summary>
        Task IngestLiveFromDecryptedAsync(IncomingLiveStatusRequest request);
    }
}
