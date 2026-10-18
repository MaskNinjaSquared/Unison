// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// The send-permission refresh and the roster restore run here: the w:g2 query
// goes out through the session, GroupMetadataReader reads it, and the Core flows
// (GroupMetadataApply, GroupRosterRestore) put it on the row. The listing, the
// per-group fallback, the full-size group picture and member pictures run here too.
// One GroupQueryWindow, so the name refresh
// and the background pass cannot each start a listing of their own.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;
using Unison.Uwp.Services.WhatsApp.Contacts;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private const string GroupServer = "@g.us";

        private readonly IWhatsAppService _client;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IGroupNaming _naming;
        private readonly ChatNameRefresh _nameRefresh;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IChatStateStore _chats;
        private readonly IJidResolver _jids;
        private readonly GroupMetadataReader _reader;
        private readonly GroupMetadataApply _apply;
        private readonly GroupRosterRestore _rosterRestore;
        private readonly AvatarFetcher _avatars;
        private readonly IUsyncGate _usyncGate;
        private readonly SyncStatusChannel _syncStatus;
        private readonly SyncWorkGate _syncWork;
        private readonly SyncWorkloadStrategy _workload;
        private readonly GroupQueryWindow _queryWindow = new GroupQueryWindow();

        /// <summary>0 free / 1 busy — overlapping listings each burn a 60s IQ and starve chat open.</summary>
        private int _listingInFlight;

        internal GroupFacade(
            IWhatsAppService client,
            IWhatsAppSessionProvider sessions,
            IGroupNaming naming,
            IChatStateStore chats,
            IJidResolver jids,
            GroupMetadataApply apply,
            GroupRosterRestore rosterRestore,
            AvatarFetcher avatars,
            IUsyncGate usyncGate,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            SyncWorkloadStrategy workload,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            ChatNameRefresh nameRefresh)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _rosterRestore = rosterRestore ?? throw new ArgumentNullException(nameof(rosterRestore));
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            _usyncGate = usyncGate ?? throw new ArgumentNullException(nameof(usyncGate));
            _reader = new GroupMetadataReader(jids);

            // Lives as long as the client, so there is nothing to unhook from.
            _client.OnGroupListingDue += (s, e) => { _ = RunBackgroundListingAsync(); };
        }

        private async Task RunBackgroundListingAsync()
        {
            _syncStatus.Raise(SyncPhaseStatus.Format(SyncPhaseStatus.Groups));
            try
            {
                using (new StartupPhaseTrace("background-groups"))
                {
                    await QueryAllGroupsAsync(force: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GroupFacade] Background group listing failed: {ex.Message}");
            }
            finally
            {
                _syncStatus.Raise(null);
            }
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

        public async Task EnsureHighQualityGroupAvatarAsync(ChatItem chat)
        {
            if (chat == null || !GroupHighAvatar.ShouldFetch(chat.IsGroup, chat.JID, chat.AvatarHighUrl))
            {
                return;
            }

            string alias;
            _jids.TryGetAlias(chat.JID, out alias);
            string localUri = await _avatars.FetchHighResolutionAsync(
                chat.JID,
                ChatPinStoreKeys.For(chat.JID, _jids.GetCanonicalJid(chat.JID), alias),
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(localUri))
            {
                return;
            }

            await _ui.RunAsync(() => chat.AvatarHighUrl = localUri);
        }

        public async Task<GroupMemberAvatarFetchResult> FetchGroupMemberAvatarAsync(
            GroupMember member,
            CancellationToken token)
        {
            var socket = _sessions.Socket;
            if (member == null || socket == null || !socket.IsHandshakeComplete)
            {
                return NotReady();
            }

            var attempt = new GroupMemberAvatarAttempt();
            foreach (string candidate in _reader.GetPictureCandidates(member))
            {
                token.ThrowIfCancellationRequested();

                ProfilePictureResult result = null;
                using (await _usyncGate.AcquireAsync(token))
                {
                    socket = _sessions.Socket;
                    if (socket == null || !socket.IsHandshakeComplete)
                    {
                        return NotReady();
                    }

                    result = await socket.GetProfilePictureUrlResultAsync(candidate, "preview");
                }

                if (!string.IsNullOrWhiteSpace(result?.Url))
                {
                    string localUri = await _avatars.CachePreviewAsync(member.Jid, result.Url, token);
                    if (!string.IsNullOrWhiteSpace(localUri))
                    {
                        return attempt.Picture(localUri);
                    }

                    attempt.NoteEmptyDownload();
                    continue;
                }

                if (result != null && result.IsNotFound)
                {
                    attempt.NoteNotFound();
                    continue;
                }

                if (result != null && result.IsTimeout)
                {
                    attempt.NoteTimeout();
                }
                else
                {
                    attempt.NoteOtherFailure(result?.FailureReason);
                }
            }

            return attempt.Finish();
        }

        public void ApplyGroupMemberAvatarOutcome(string memberJid, GroupMemberAvatarFetchResult result)
        {
            if (string.IsNullOrWhiteSpace(memberJid) || result == null)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            string canonical = _jids.GetCanonicalJid(JidHelper.Normalize(memberJid));
            foreach (var chat in _chats.Chats)
            {
                if (chat?.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    continue;
                }

                foreach (var member in chat.GroupMembers)
                {
                    if (member == null || !GroupRosterRules.MemberMatches(member, canonical, _jids))
                    {
                        continue;
                    }

                    if (result.HasPicture)
                    {
                        ChatAvatarOutcome.RecordCached(member, result.LocalUri, nowUtc);
                    }
                    else if (result.IsNotFound)
                    {
                        ChatAvatarOutcome.RecordAbsent(member, "no-picture", nowUtc);
                    }
                    else
                    {
                        ChatAvatarOutcome.RecordFailure(member, result.FailureReason ?? "fetch-failed", nowUtc);
                    }
                }
            }
        }

        private static GroupMemberAvatarFetchResult NotReady()
        {
            return new GroupMemberAvatarFetchResult
            {
                IsTransientFailure = true,
                FailureReason = "socket-not-ready"
            };
        }

        public Task QueryAllGroupsAsync() => QueryAllGroupsAsync(false);

        public async Task QueryAllGroupsAsync(bool force)
        {
            if (_syncWork.IsReplayDrainActive)
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (replay drain active)");
                return;
            }

            string syncTrafficDeferReason;
            if (_syncWork.ShouldDeferAvatarFetch(out syncTrafficDeferReason))
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

            if (Interlocked.CompareExchange(ref _listingInFlight, 1, 0) != 0)
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (listing already in flight)");
                return;
            }

            // Armed on the way out: full reuse when the listing answered; short failure backoff
            // when it timed out, so callers cannot stack another 60s IQ on top of a dead one.
            bool listingAnswered = false;
            try
            {
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
                    await QueryUnresolvedGroupMetadataAsync(_workload.GroupMetadataBatchSize);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GroupFacade] Group metadata fallback failed: " + ex.Message);
                }

                if (listingAnswered)
                {
                    _queryWindow.Arm(DateTime.UtcNow);
                }
                else
                {
                    _queryWindow.ArmFailure(DateTime.UtcNow);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _listingInFlight, 0);
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
            await _ui.RunAsync(() =>
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
                            _naming.IsMeaningfulGroupLabel(chat.Name, chat.JID)))
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
                _syncStatus.Raise(SyncPhaseStatus.Format(SyncPhaseStatus.Groups, attempts, pending.Count));

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

                await Task.Delay(Math.Max(0, _workload.GroupMetadataPauseMs));
            }

            _syncStatus.Raise(null);

            if (resolved > 0)
            {
                await _nameRefresh.ApplyAsync();
                _catalog.Schedule();
            }

            Debug.WriteLine("[GroupFacade] Group metadata fallback: resolved " + resolved + "/" + attempts + " unresolved group names");
        }
    }
}
