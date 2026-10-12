// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// The send-permission refresh and the roster restore run here: the w:g2 query
// goes out through the session, GroupMetadataReader reads it, and the Core flows
// (GroupMetadataApply, GroupRosterRestore) put it on the row. The high-quality
// picture fetch still forwards into the client's avatar cluster.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private const string GroupServer = "@g.us";

        private readonly IWhatsAppService _client;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IJidResolver _jids;
        private readonly GroupMetadataReader _reader;
        private readonly GroupMetadataApply _apply;
        private readonly GroupRosterRestore _rosterRestore;

        internal GroupFacade(
            IWhatsAppService client,
            IWhatsAppSessionProvider sessions,
            IJidResolver jids,
            GroupMetadataApply apply,
            GroupRosterRestore rosterRestore)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _rosterRestore = rosterRestore ?? throw new ArgumentNullException(nameof(rosterRestore));
            _reader = new GroupMetadataReader(jids);
        }

        public async Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            var socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) ||
                !canonical.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var response = await socket.QueryGroupMetadataAsync(canonical);
                await _apply.ApplyMetadataAsync(canonical, _reader.ReadMetadata(response, canonical));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GroupFacade] RefreshGroupSendPermissionsAsync failed for {canonical}: {ex.Message}");
            }
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
