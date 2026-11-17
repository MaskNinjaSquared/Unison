using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned reaction send surface.</summary>
    public interface IMessageReactIo
    {
        Task<bool> TryReactAsync(ReactToMessageRequest request);
    }
}
