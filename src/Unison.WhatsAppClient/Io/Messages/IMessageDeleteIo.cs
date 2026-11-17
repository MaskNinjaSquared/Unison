using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned message delete / revoke surface.</summary>
    public interface IMessageDeleteIo
    {
        Task DeleteMessageForMeAsync(DeleteMessageForMeRequest request);

        Task DeleteMessageForEveryoneAsync(DeleteMessageForEveryoneRequest request);
    }
}
