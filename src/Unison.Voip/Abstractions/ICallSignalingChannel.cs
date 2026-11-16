using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Sends outbound call stanzas on the chat socket (Socket UseCases behind the host).
    /// </summary>
    public interface ICallSignalingChannel
    {
        Task RejectAsync(string callId, string toJid, string callCreatorJid, CancellationToken cancellationToken);

        Task TerminateAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason,
            CancellationToken cancellationToken);
    }
}
