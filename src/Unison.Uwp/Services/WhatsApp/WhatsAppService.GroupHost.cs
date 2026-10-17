using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What the group flows in Core — <see cref="GroupMetadataApply"/> and
    /// <see cref="GroupRosterRestore"/> — still need from the client.
    /// </summary>
    /// <remarks>
    /// Implemented explicitly so it adds nothing to the client's own surface; the only ways in
    /// are the <see cref="IGroupHost"/> registration and the applier the client builds for its
    /// own query passes.
    /// </remarks>
    public partial class WhatsAppService : IGroupHost
    {
        string IGroupHost.ResolveMemberDisplayName(string jid) => ResolveGroupMemberDisplayName(jid);

        string IGroupHost.ResolveChatDisplayName(string jid) => ResolveDisplayName(jid, "chat");

        bool IGroupHost.IsMeaningfulGroupLabel(string label, string groupJid) =>
            IsMeaningfulChatLabel(label, groupJid, true);
    }
}
