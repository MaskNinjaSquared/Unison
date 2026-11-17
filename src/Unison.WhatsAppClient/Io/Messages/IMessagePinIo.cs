using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned message pin surface.</summary>
    public interface IMessagePinIo
    {
        Task SetMessagePinnedAsync(PinMessageRequest request);
    }
}
