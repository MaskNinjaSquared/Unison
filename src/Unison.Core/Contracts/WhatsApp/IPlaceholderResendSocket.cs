using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>The socket half of a placeholder resend. Wire send may throw when the socket is gone.</summary>
    public interface IPlaceholderResendSocket
    {
        /// <summary>False when there is no live handshake yet — callers skip scheduling.</summary>
        bool IsHandshakeComplete { get; }

        string GenerateMessageId();

        /// <summary>Sends the resend request under <paramref name="stanzaId"/> and returns the id it went out with.</summary>
        Task<string> RequestPlaceholderResendAsync(Proto.MessageKey key, string stanzaId);
    }
}
