// =============================================================================
// IUserLogoutHost
//
// Ports for user-initiated logout: bounded server notice, then local wipe.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IUserLogoutHost
    {
        bool TryBeginLogout();

        void EndLogout();

        void JournalLogout(string eventName, string detail = null);

        Task NotifyServerLogoutAsync(string reason);

        Task ClearSessionAsync();

        Task ClearPublishedContactsAsync();

        Task AutoRestoreDefaultAsync();
    }
}
