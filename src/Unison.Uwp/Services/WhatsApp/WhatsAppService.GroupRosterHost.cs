using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What <see cref="GroupRosterRestore"/> on <c>GroupFacade</c> still needs from the client.
    /// </summary>
    /// <remarks>
    /// Implemented explicitly so it adds nothing to the client's own surface; the only way in is
    /// the <see cref="IGroupRosterHost"/> registration.
    /// </remarks>
    public partial class WhatsAppService : IGroupRosterHost
    {
        Task IGroupRosterHost.RunOnUiThreadAsync(Action action) => RunOnUiThreadAsync(action);

        string IGroupRosterHost.ResolveMemberDisplayName(string jid) => ResolveGroupMemberDisplayName(jid);
    }
}
