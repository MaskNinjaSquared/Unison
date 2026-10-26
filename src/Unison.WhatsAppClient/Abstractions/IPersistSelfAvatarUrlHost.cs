// =============================================================================
// IPersistSelfAvatarUrlHost
//
// Side effects for PersistSelfAvatarUrlDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistSelfAvatarUrlHost
    {
        bool HasSelfMe { get; }

        string SelfMeAvatarUrl { get; }

        void SetSelfMeAvatarUrl(string avatarUrl);

        bool CanPersistAuth { get; }

        Task SaveAuthStateAsync();

        void LogSelfAvatarPersistFailed(string message);
    }
}
