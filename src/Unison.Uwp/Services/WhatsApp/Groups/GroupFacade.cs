// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// The roster restore runs here, through GroupRosterRestore (Core). The w:g2
// metadata reads and the high-quality picture fetch still forward into
// WhatsAppService.Groups: the apply that follows a metadata reply writes the name
// caches and schedules the chat-list persist, and those move with 3.6 / 3.9b.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private readonly IWhatsAppService _client;
        private readonly GroupRosterRestore _rosterRestore;

        internal GroupFacade(IWhatsAppService client, GroupRosterRestore rosterRestore)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _rosterRestore = rosterRestore ?? throw new ArgumentNullException(nameof(rosterRestore));
        }

        public Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            return string.IsNullOrWhiteSpace(groupJid)
                ? Task.CompletedTask
                : _client.RefreshGroupSendPermissionsAsync(groupJid);
        }

        public Task EnsureGroupRosterLoadedFromStoreAsync(string groupJid)
        {
            return _rosterRestore.EnsureLoadedAsync(groupJid);
        }

        public Task EnsureHighQualityGroupAvatarAsync(ChatItem chat)
        {
            return chat == null ? Task.CompletedTask : _client.EnsureHighQualityGroupAvatarAsync(chat);
        }
    }
}
