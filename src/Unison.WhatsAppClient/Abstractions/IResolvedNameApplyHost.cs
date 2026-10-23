// =============================================================================
// IResolvedNameApplyHost
//
// UI-thread name / subject apply that still lives on the client (ContactNames,
// GroupSubjects, list walk). ContactFacade owns the app-state entry points and
// hands prepared entries here.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IResolvedNameApplyHost
    {
        Task ApplyResolvedNameBatchAsync(IList<ResolvedNameEntry> entries);

        /// <summary>
        /// Self push-name from account app-state: capture, persist auth, then file as a contact name.
        /// </summary>
        Task ApplySelfPushNameFromAppStateAsync(string name);

        /// <summary>
        /// Echo / stanza / app-state self name into auth + sidebar. Rules in
        /// <see cref="SelfPushNameCapture"/>; host still owns Me.Name / CurrentUserName / auth persist.
        /// </summary>
        void CaptureSelfPushName(string pushName, string source);
    }
}
