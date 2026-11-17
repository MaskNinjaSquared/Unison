using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Client-owned text-send surface. Core <c>IMessageService</c> adapts to this.
    /// </summary>
    public interface IMessageTextIo
    {
        Task<ChatMessageDto> SendTextAsync(SendTextRequest request);
    }
}
