// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// The send-permission refresh and the roster restore run here: the w:g2 query
// goes out through the session, GroupMetadataReader reads it, and the Core flows
// (GroupMetadataApply, GroupRosterRestore) put it on the row. The high-quality
// picture fetch still forwards into the client's avatar cluster. The listing and the
// per-group fallback run here too: one GroupQueryWindow, so the name refresh and the
// background pass cannot each start a pass of their own.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private const string GroupServer = "@g.us";
        private const int UnresolvedMetadataLimit = 25;
        private static readonly TimeSpan UnresolvedMetadataPause = TimeSpan.FromMilliseconds(120);

        private readonly IWhatsAppService _client;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IGroupHost _host;
        private readonly IChatStateStore _chats;
        private readonly IJidResolver _jids;
        private readonly GroupMetadataReader _reader;
        private readonly GroupMetadataApply _apply;
        private readonly GroupRosterRestore _rosterRestore;
        private readonly GroupQueryWindow _queryWindow = new GroupQueryWindow();

        internal GroupFacade(
            IWhatsAppService client,
            IWhatsAppSessionProvider sessions,
            IGroupHost host,
            IChatStateStore chats,
            IJidResolver jids,
            GroupMetadataApply apply,
            GroupRosterRestore rosterRestore)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
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

        public Task QueryAllGroupsAsync() => QueryAllGroupsAsync(false);

        public async Task QueryAllGroupsAsync(bool force)
        {
            if (_client.IsReplayDrainActive)
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (replay drain active)");
                return;
            }

            string syncTrafficDeferReason;
            if (_client.ShouldDeferAvatarFetch(out syncTrafficDeferReason))
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (sync traffic active: " + syncTrafficDeferReason + ")");
                return;
            }

            var socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (handshake not complete)");
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (_queryWindow.ShouldSkip(force, nowUtc))
            {
                Debug.WriteLine(
                    "[GroupFacade] QueryAllGroupsAsync skipped (last pass was " +
                    _queryWindow.Since(nowUtc).TotalSeconds.ToString("F0") +
                    "s ago)");
                return;
            }

            // Armed on the way out, and only when the listing answered. A timeout is exactly when
            // the groups are still nameless, and it must not buy itself the reuse window.
            bool listingAnswered = false;
            try
            {
                Debug.WriteLine("[GroupFacade] Fetching all participating groups...");
                var response = await socket.QueryParticipatingGroupsAsync();
                if (response != null)
                {
                    listingAnswered = true;
                    var groupNodes = response.FindAllDescendants("group");
                    Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync found " + groupNodes.Count + " group nodes.");
                    await _apply.ApplyListingAsync(_reader.ReadListing(groupNodes));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupFacade] Group query failed: " + ex.Message);
            }

            // Outside the try above. The per-group fallback names the groups the listing missed,
            // so a listing that failed is the case it exists for.
            try
            {
                await QueryUnresolvedGroupMetadataAsync(UnresolvedMetadataLimit);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupFacade] Group metadata fallback failed: " + ex.Message);
            }

            if (listingAnswered)
            {
                _queryWindow.Arm(DateTime.UtcNow);
            }
        }

        private async Task QueryUnresolvedGroupMetadataAsync(int limit)
        {
            var socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return;
            }

            var unresolved = new List<ChatItem>();
            await _host.RunOnUiThreadAsync(() =>
            {
                foreach (var chat in _chats.Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    if (GroupQuery.NeedsMetadata(
                            chat.IsGroup,
                            chat.JID,
                            _host.IsMeaningfulGroupLabel(chat.Name, chat.JID)))
                    {
                        unresolved.Add(chat);
                    }
                }
            });

            if (unresolved.Count == 0)
            {
                return;
            }

            int attempts = 0;
            int resolved = 0;
            var pending = unresolved.Take(Math.Max(1, limit)).ToList();
            foreach (var chat in pending)
            {
                if (string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                socket = _sessions.Socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[GroupFacade] QueryUnresolvedGroupMetadataAsync aborted (socket gone)");
                    break;
                }

                attempts++;
                _client.RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.Groups, attempts, pending.Count));

                try
                {
                    var response = await socket.QueryGroupMetadataAsync(chat.JID);
                    string subject = _reader.ExtractSubject(response, chat.JID);
                    if (_apply.TryCacheSyncedSubject(chat.JID, subject, chat.Name))
                    {
                        resolved++;
                    }

                    _ = _apply.ApplyMetadataAsync(chat.JID, _reader.ReadMetadata(response, chat.JID));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GroupFacade] QueryGroupMetadataAsync failed for " + chat.JID + ": " + ex.Message);
                }

                await Task.Delay(UnresolvedMetadataPause);
            }

            _client.RaiseSyncStatus(null);

            if (resolved > 0)
            {
                await _client.ApplyResolvedDisplayNamesToChatsAsync();
                _host.SchedulePersist();
            }

            Debug.WriteLine("[GroupFacade] Group metadata fallback: resolved " + resolved + "/" + attempts + " unresolved group names");
        }
    }
}
