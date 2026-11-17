using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Sends outbound call stanzas on the chat socket (Socket UseCases behind the host).
    /// </summary>
    public interface ICallSignalingChannel
    {
        /// <summary>Places a 1:1 audio offer (meowcaller placeCall).</summary>
        Task<CallOfferWireResult> OfferAudioAsync(
            string peerJid,
            CancellationToken cancellationToken);

        /// <summary>Eager prep when an inbound offer rings (meowcaller sendPreaccept).</summary>
        Task PreacceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken);

        /// <summary>Answer on the wire (meowcaller BuildAccept). No media yet.</summary>
        Task AcceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            bool isVideo,
            CancellationToken cancellationToken);

        Task RejectAsync(string callId, string toJid, string callCreatorJid, CancellationToken cancellationToken);

        Task TerminateAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason,
            CancellationToken cancellationToken);

        /// <summary>Callee echo of one caller relaylatency probe (meowcaller onRelayLatency).</summary>
        Task SendRelayLatencyAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            uint latencyMs,
            string relayName,
            byte[] addressBytes,
            CancellationToken cancellationToken);
    }
}
