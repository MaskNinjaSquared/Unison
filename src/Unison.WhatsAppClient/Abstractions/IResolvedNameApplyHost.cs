// =============================================================================
// IResolvedNameApplyHost
//
// Auth / sidebar half of self-name capture. ContactFacade owns resolved-name
// batch apply (push map, group subjects, list walk).
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

        /// <summary>
        /// Echo / stanza / app-state self name into auth + sidebar. Rules in
        /// <see cref="Helpers.SelfPushNameCapture"/>; host still owns Me.Name / CurrentUserName / auth persist.
        /// </summary>
        void CaptureSelfPushName(string pushName, string source);
    }
}
