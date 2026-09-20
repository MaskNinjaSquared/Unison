// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// Everything here forwards. The w:g2 metadata reads, the roster hydrate and the
// high-quality picture fetch all still run inside WhatsAppService.Groups, and
// moving those bodies is phase 3.2. What this buys now is that the callers stop
// depending on where the work happens to live today.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private readonly IWhatsAppService _client;

        internal GroupFacade(IWhatsAppService client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            return string.IsNullOrWhiteSpace(groupJid)
                ? Task.CompletedTask
                : _client.RefreshGroupSendPermissionsAsync(groupJid);
        }

        public Task EnsureGroupRosterLoadedFromStoreAsync(string groupJid)
        {
            return string.IsNullOrWhiteSpace(groupJid)
                ? Task.CompletedTask
                : _client.EnsureGroupRosterLoadedFromStoreAsync(groupJid);
        }

        public Task EnsureHighQualityGroupAvatarAsync(ChatItem chat)
        {
            return chat == null ? Task.CompletedTask : _client.EnsureHighQualityGroupAvatarAsync(chat);
        }
    }
}
