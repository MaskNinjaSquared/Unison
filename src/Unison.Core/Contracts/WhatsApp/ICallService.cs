using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Outbound call signaling the UI can invoke. Media accept/offer stay gated until Voip engine.
    /// </summary>
    public interface ICallService
    {
        /// <summary>Reject an inbound ringing offer.</summary>
        Task RejectAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Terminate / hang up.</summary>
        Task HangupAsync(
            string callId,
            string peerJid,
            string callCreatorJid,
            string reason = null,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>True when the host can place/answer with media (engine ready).</summary>
        bool IsMediaAvailable { get; }
    }
}
