// =============================================================================
// GroupFacade
//
// The address the info pane and the composer use for group questions, so neither
// of them has to name the compatibility client.
//
// The send-permission refresh and the roster restore run here: the w:g2 query
// goes out through IGroupWireActions, GroupMetadataReader reads it, and the Core
// flows (GroupMetadataApply, GroupRosterRestore) put it on the row. The listing,
// the per-group fallback, the full-size group picture and member pictures run
// here too. One GroupQueryWindow, so the name refresh and the background pass
// cannot each start a listing of their own.
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
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Groups
{
    public sealed class GroupFacade : IGroupService
    {
        private const string GroupServer = "@g.us";

        private readonly IWhatsAppService _client;
        private readonly IGroupWireActions _wire;
        private readonly IGroupNaming _naming;
        private readonly ChatNameRefresh _nameRefresh;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IChatStateStore _chats;
        private readonly IJidResolver _jids;
        // Func: ContactFacade also takes IGroupService — avoid DI cycle at construction.
        private readonly Func<IContactService> _contacts;
        private readonly IPersonStore _people;
        private readonly GroupMetadataReader _reader;
        private readonly GroupMetadataApply _apply;
        private readonly GroupRosterRestore _rosterRestore;
        private readonly IGroupAvatarActions _avatars;
        private readonly IUsyncGate _usyncGate;
        private readonly SyncStatusChannel _syncStatus;
        private readonly SyncWorkGate _syncWork;
        private readonly SyncWorkloadStrategy _workload;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly GroupQueryWindow _queryWindow = new GroupQueryWindow();

        /// <summary>0 free / 1 busy — overlapping listings each burn a 60s IQ and starve chat open.</summary>
        private int _listingInFlight;

        /// <summary>
        /// Listing was skipped for catch-up / sync traffic; retry once the next due signal arrives.
        /// </summary>
        private int _listingRetryPending;

        /// <summary>
        /// Groups already asked once this session for <c>linked_parent</c> / <c>parent</c>.
        /// Stops re-querying ordinary groups that never carry those children.
        /// </summary>
        private readonly HashSet<string> _communityLinkProbed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GroupFacade(
            IWhatsAppService client,
            IGroupWireActions wire,
            IGroupNaming naming,
            IChatStateStore chats,
            IJidResolver jids,
            Func<IContactService> contacts,
            IPersonStore people,
            GroupMetadataApply apply,
            GroupRosterRestore rosterRestore,
            IGroupAvatarActions avatars,
            IUsyncGate usyncGate,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            SyncWorkloadStrategy workload,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            ChatNameRefresh nameRefresh,
            IRuntimeDiagnostics diagnostics = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
            _people = people;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _rosterRestore = rosterRestore ?? throw new ArgumentNullException(nameof(rosterRestore));
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            _usyncGate = usyncGate ?? throw new ArgumentNullException(nameof(usyncGate));
            _diagnostics = diagnostics;
            _reader = new GroupMetadataReader(jids);

            // Lives as long as the client, so there is nothing to unhook from.
            _client.OnGroupListingDue += (s, e) => { _ = RunBackgroundListingAsync(); };
        }

        private async Task RunBackgroundListingAsync()
        {
            _syncStatus.Raise(SyncPhaseStatus.Format(SyncPhaseStatus.Groups));
            _diagnostics?.Write("startup-phase", "background-groups:begin");
            var elapsed = Stopwatch.StartNew();
            try
            {
                await QueryAllGroupsAsync(force: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GroupFacade] Background group listing failed: {ex.Message}");
            }
            finally
            {
                elapsed.Stop();
                _diagnostics?.Write(
                    "startup-phase",
                    "background-groups:end",
                    "elapsedMs=" + elapsed.ElapsedMilliseconds);
                _syncStatus.Raise(null);
            }
        }

        public async Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            if (!_wire.IsHandshakeComplete)
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
                BinaryNode response = await _wire.QueryGroupMetadataAsync(canonical);
                GroupMetadataSnapshot metadata = _reader.ReadMetadata(response, canonical);
                _communityLinkProbed.Add(canonical);

                // ReadMetadata leaves Subject null by design — extract + cache so community hubs
                // and ordinary groups leave the "Community" / id placeholder after this refresh.
                string subject = _reader.ExtractSubject(response, canonical);
                string priorName = null;
                await _ui.RunAsync(() =>
                {
                    ChatItem row = _chats.Chats.FirstOrDefault(c =>
                        c != null &&
                        string.Equals(
                            _jids.GetCanonicalJid(c.JID),
                            canonical,
                            StringComparison.OrdinalIgnoreCase));
                    priorName = row?.Name;
                });
                bool cachedSubject = _apply.TryCacheSyncedSubject(canonical, subject, priorName);

                Debug.WriteLine(
                    "[GroupFacade] RefreshGroupSendPermissions " + canonical +
                    " linkedParent=" + (metadata?.LinkedParentJid ?? "-") +
                    " isCommunityParent=" + (metadata != null && metadata.IsCommunityParent) +
                    " isCommunityAnnounce=" + (metadata != null && metadata.IsCommunityAnnounce) +
                    " subject=" + (string.IsNullOrWhiteSpace(subject) ? "-" : "yes"));
                _diagnostics?.Write(
                    "group-meta",
                    "refresh-apply",
                    "chat=" + DiagnosticJidMask.Apply(canonical) +
                    "; linkedParent=" + DiagnosticJidMask.Apply(metadata?.LinkedParentJid) +
                    "; isCommunityParent=" + (metadata != null && metadata.IsCommunityParent) +
                    "; isCommunityAnnounce=" + (metadata != null && metadata.IsCommunityAnnounce) +
                    "; announceOnly=" + (metadata != null && metadata.AnnounceOnly) +
                    "; subjectCached=" + cachedSubject);
                await _apply.ApplyMetadataAsync(canonical, metadata);
                if (cachedSubject)
                {
                    await _nameRefresh.ApplyAsync();
                }
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

        /// <inheritdoc />
        public Task ApplyGroupMetadataAsync(string groupJid, GroupMetadataSnapshot metadata)
        {
            return _apply.ApplyMetadataAsync(groupJid, metadata);
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
            if (member == null || !_wire.IsHandshakeComplete)
            {
                return NotReady();
            }

            var attempt = new GroupMemberAvatarAttempt();
            foreach (string candidate in _reader.GetPictureCandidates(member))
            {
                token.ThrowIfCancellationRequested();

                GroupProfilePictureResult result = null;
                using (await _usyncGate.AcquireAsync(token))
                {
                    if (!_wire.IsHandshakeComplete)
                    {
                        return NotReady();
                    }

                    result = await _wire.GetPreviewPictureAsync(candidate);
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

        /// <inheritdoc />
        public bool MemberMatches(GroupMember member, string candidateJid)
        {
            return GroupRosterRules.MemberMatches(member, candidateJid, _jids);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> MembershipPersonKeys(GroupMember member)
        {
            return GroupRosterRules.MembershipPersonKeys(member, _jids);
        }

        /// <inheritdoc />
        public void EnrichMember(
            GroupMember member,
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null)
        {
            GroupParticipantResolver.EnrichMember(
                member,
                participantJid,
                groupChat,
                context,
                nameHint);
        }

        /// <inheritdoc />
        public string ResolveMemberAvatar(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            GroupMember rosterMember = null)
        {
            return GroupParticipantResolver.ResolveAvatar(
                participantJid,
                groupChat,
                context,
                rosterMember);
        }

        /// <inheritdoc />
        public string ResolveMemberDisplayName(
            string participantJid,
            ChatItem groupChat,
            ParticipantResolutionContext context,
            string nameHint = null,
            GroupMember rosterMember = null)
        {
            return GroupParticipantResolver.ResolveDisplayName(
                participantJid,
                groupChat,
                context,
                nameHint,
                rosterMember);
        }

        /// <inheritdoc />
        public void InvalidateParticipantDirectChatIndex()
        {
            GroupParticipantLookup.InvalidateDirectChatIndex();
        }

        /// <inheritdoc />
        public IGroupParticipantLookup CreateOpenChatParticipantLookup(Func<string> selfDisplayLabel)
        {
            var context = new ParticipantResolutionContext(_jids, _contacts(), _chats, _people);
            return new GroupParticipantLookup(context, selfDisplayLabel);
        }

        /// <inheritdoc />
        public void EnrichMember(
            GroupMember member,
            string participantJid,
            ChatItem groupChat,
            string nameHint = null)
        {
            EnrichMember(
                member,
                participantJid,
                groupChat,
                new ParticipantResolutionContext(_jids, _contacts(), _chats, _people),
                nameHint);
        }

        public Task QueryAllGroupsAsync() => QueryAllGroupsAsync(false);

        public async Task QueryAllGroupsAsync(bool force)
        {
            string heavyDeferReason;
            if (_syncWork.ShouldDeferHeavyLocalWork(out heavyDeferReason))
            {
                Interlocked.Exchange(ref _listingRetryPending, 1);
                Debug.WriteLine(
                    "[GroupFacade] QueryAllGroupsAsync skipped (" + heavyDeferReason +
                    "); retry armed");
                return;
            }

            string syncTrafficDeferReason;
            if (_syncWork.ShouldDeferAvatarFetch(out syncTrafficDeferReason))
            {
                Interlocked.Exchange(ref _listingRetryPending, 1);
                Debug.WriteLine(
                    "[GroupFacade] QueryAllGroupsAsync skipped (sync traffic active: " +
                    syncTrafficDeferReason + "); retry armed");
                return;
            }

            if (!_wire.IsHandshakeComplete)
            {
                Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync skipped (handshake not complete)");
                return;
            }

            bool retryForce = Interlocked.Exchange(ref _listingRetryPending, 0) == 1;
            bool effectiveForce = force || retryForce;

            DateTime nowUtc = DateTime.UtcNow;
            if (_queryWindow.ShouldSkip(effectiveForce, nowUtc))
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
                    BinaryNode response = await _wire.QueryParticipatingGroupsAsync();
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
            if (!_wire.IsHandshakeComplete)
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

                    string canonical = _jids.GetCanonicalJid(chat.JID);
                    bool missingCommunityLink =
                        string.IsNullOrWhiteSpace(chat.LinkedParentJid) &&
                        !chat.IsCommunityParent &&
                        !string.IsNullOrWhiteSpace(canonical) &&
                        !_communityLinkProbed.Contains(canonical);

                    if (GroupQuery.NeedsMetadata(
                            chat.IsGroup,
                            chat.JID,
                            _naming.IsMeaningfulGroupLabel(chat.Name, chat.JID),
                            missingCommunityLink))
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

                if (!_wire.IsHandshakeComplete)
                {
                    Debug.WriteLine("[GroupFacade] QueryUnresolvedGroupMetadataAsync aborted (socket gone)");
                    break;
                }

                attempts++;
                _syncStatus.Raise(SyncPhaseStatus.Format(SyncPhaseStatus.Groups, attempts, pending.Count));

                string canonical = _jids.GetCanonicalJid(chat.JID);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    _communityLinkProbed.Add(canonical);
                }

                try
                {
                    BinaryNode response = await _wire.QueryGroupMetadataAsync(chat.JID);
                    string subject = _reader.ExtractSubject(response, chat.JID);
                    if (_apply.TryCacheSyncedSubject(chat.JID, subject, chat.Name))
                    {
                        resolved++;
                    }

                    await _apply.ApplyMetadataAsync(chat.JID, _reader.ReadMetadata(response, chat.JID));
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
