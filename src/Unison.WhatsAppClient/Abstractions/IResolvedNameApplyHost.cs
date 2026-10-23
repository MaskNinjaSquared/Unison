// =============================================================================
// IResolvedNameApplyHost
//
// Auth / sidebar write for the account's own display name. ContactFacade owns
// capture policy (dash placeholder, PreferEstablished, RememberPersonName,
// list relabel); the host only mirrors Me.Name / CurrentUserName and persists.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IResolvedNameApplyHost
    {
        /// <summary>
        /// Self push-name from account app-state: capture, persist auth, then file as a contact name.
        /// </summary>
        Task ApplySelfPushNameFromAppStateAsync(string name);

        /// <summary>True when credentials are loaded and a self id is known.</summary>
        bool HasSelfCredentials { get; }

        /// <summary>Normalized PN (or primary) JID for the logged-in account.</summary>
        string SelfId { get; }

        /// <summary>Normalized LID for the logged-in account, or null.</summary>
        string SelfLid { get; }

        /// <summary>Name currently on the auth credentials.</summary>
        string AuthSelfName { get; }

        /// <summary>Name currently shown in the sidebar / profile shell.</summary>
        string SidebarSelfName { get; }

        /// <summary>True when the label is just our own phone digits.</summary>
        bool IsOwnPhoneEchoLabel(string label);

        /// <summary>
        /// Writes the accepted self name onto auth + sidebar and optionally persists credentials.
        /// </summary>
        void WriteSelfDisplayName(string sanitized, bool persistAuth);
    }
}
