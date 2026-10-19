using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>The two socket calls a placeholder resend makes. Either may throw when the socket is gone.</summary>
    public interface IPlaceholderResendSocket
    {
        string GenerateMessageId();

        /// <summary>Sends the resend request under <paramref name="stanzaId"/> and returns the id it went out with.</summary>
        Task<string> RequestPlaceholderResendAsync(Proto.MessageKey key, string stanzaId);
    }
}
