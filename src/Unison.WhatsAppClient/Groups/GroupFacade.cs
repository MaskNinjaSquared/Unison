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
        private readonly IAppVisibility _visibility;
        private readonly GroupQueryWindow _queryWindow = new GroupQueryWindow();

        /// <summary>0 free / 1 busy — overlapping listings each burn a 60s IQ and starve chat open.</summary>
        private int _listingInFlight;

        /// <summary>
        /// Listing was skipped for catch-up / sync traffic; retry once the next due signal arrives.
        /// </summary>
        private int _listingRetryPending;

        /// <summary>
        /// Bumped when the window hides (or a newer resume settle arms) so a delayed
        /// post-keepalive listing retry does not fire after the user left again.
        /// </summary>
        private int _resumeSettleGeneration;

        /// <summary>
        /// Cancels in-flight listing IQs when the window hides (keepalive). Wire waiters
        /// drop via <see cref="CancellationToken"/>; the stanza may still arrive ignored.
        /// </summary>
        private readonly GroupListingCancellation _listingCancel = new GroupListingCancellation();

        /// <summary>
        /// Groups already asked once this session for <c>linked_parent</c> / <c>parent</c>.
        /// Stops re-querying ordinary groups that never carry those children.
        /// </summary>
        private readonly HashSet<string> _communityLinkProbed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GroupFacade(
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
            IRuntimeDiagnostics diagnostics = null,
            IAppVisibility visibility = null)
        {
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
            _visibility = visibility;
            _reader = new GroupMetadataReader(jids);
            if (_visibility != null)
            {
                _visibility.Changed += OnAppVisibilityChanged;
            }
        }

        /// <inheritdoc />
        public void NotifyGroupListingDue()
        {
            if (GroupListingVisibilityGate.ShouldDeferStart(IsWindowVisible))
            {
                Interlocked.Exchange(ref _listingRetryPending, 1);
                _diagnostics?.Write(
                    "startup-phase",
                    "background-groups:deferred",
                    "reason=window-hidden");
                return;
            }

            _ = RunBackgroundListingAsync();
        }

        private bool IsWindowVisible =>
            _visibility == null || _visibility.IsWindowVisible;

        private void OnAppVisibilityChanged(object sender, EventArgs e)
        {
            if (_visibility == null)
            {
                return;
            }

            if (!_visibility.IsWindowVisible)
            {
                Interlocked.Increment(ref _resumeSettleGeneration);
                _listingCancel.Cancel();
                return;
            }

            // Do not arm listing on the same turn as visible=True — resume paint +
            // fast-resume already saturate the dispatcher (Mobile hitch in 06f9cb95).
            if (Interlocked.CompareExchange(ref _listingRetryPending, 0, 0) == 1)
            {
                _ = RunResumeListingAfterSettleAsync();
            }
        }

        private async Task RunResumeListingAfterSettleAsync()
        {
            int armed = Interlocked.Increment(ref _resumeSettleGeneration);
            int delayMs = Math.Max(0, _workload.GroupListingResumeSettleMs);
            _diagnostics?.Write(
                "startup-phase",
                "background-groups:deferred",
                "reason=resume-settle; delayMs=" + delayMs);

            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (!GroupListingVisibilityGate.ShouldFireResumeRetry(
                    IsWindowVisible,
                    Interlocked.CompareExchange(ref _listingRetryPending, 0, 0) == 1,
                    armed,
                    Volatile.Read(ref _resumeSettleGeneration)))
            {
                return;
            }

            NotifyGroupListingDue();
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

        public async Task RefreshGroupSendPermissionsAsync(
            string groupJid,
            bool includeRoster = false,
            CancellationToken cancellationToken = default)
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
                cancellationToken.ThrowIfCancellationRequested();
                BinaryNode response = await _wire.QueryGroupMetadataAsync(canonical);
                cancellationToken.ThrowIfCancellationRequested();
                // Pass includeRoster into the reader too — otherwise we still allocate up to 512
                // drafts on every open refresh and discard them when ApplyMembers is gated off.
                GroupMetadataSnapshot metadata = _reader.ReadMetadata(response, canonical, includeRoster);
                _communityLinkProbed.Add(canonical);

                // ReadMetadata leaves Subject null by design — extract and hand to Apply so
                // subject cache runs AFTER WaitWhileHeld (never steal UI from LoadMessages).
                string subject = _reader.ExtractSubject(response, canonical);

                Debug.WriteLine(
                    "[GroupFacade] RefreshGroupSendPermissions " + canonical +
                    " linkedParent=" + (metadata?.LinkedParentJid ?? "-") +
                    " isCommunityParent=" + (metadata != null && metadata.IsCommunityParent) +
                    " isCommunityAnnounce=" + (metadata != null && metadata.IsCommunityAnnounce) +
                    " includeRoster=" + includeRoster +
                    " subject=" + (string.IsNullOrWhiteSpace(subject) ? "-" : "yes"));
                _diagnostics?.Write(
                    "group-meta",
                    "refresh-apply",
                    "chat=" + DiagnosticJidMask.Apply(canonical) +
                    "; linkedParent=" + DiagnosticJidMask.Apply(metadata?.LinkedParentJid) +
                    "; isCommunityParent=" + (metadata != null && metadata.IsCommunityParent) +
                    "; isCommunityAnnounce=" + (metadata != null && metadata.IsCommunityAnnounce) +
                    "; announceOnly=" + (metadata != null && metadata.AnnounceOnly) +
                    "; includeRoster=" + includeRoster +
                    "; subject=" + (string.IsNullOrWhiteSpace(subject) ? "-" : "yes"));
                // Default flags-only: open-path ApplyMembers(500+) froze Mobile after paint.
                // Members pivot passes includeRoster: true (chunked off UI).
                // ApplyMetadataAsync already ApplySyncedLabel on the single row — do not call
                // ChatNameRefresh.ApplyAsync() here (full catalogue walk → list-names-applied
                // storms that starve the timeline after a large-group open).
                await _apply.ApplyMetadataAsync(
                    canonical,
                    metadata,
                    includeRoster,
                    cancellationToken,
                    subject);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[GroupFacade] RefreshGroupSendPermissionsAsync cancelled for {canonical}");
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

        /// <inheritdoc />
        public Task ApplyGroupMetadataFromResponseAsync(string groupJid, BinaryNode response)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || response == null)
            {
                return Task.CompletedTask;
            }

            return _apply.ApplyMetadataAsync(groupJid, _reader.ReadMetadata(response, groupJid));
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
            if (GroupListingVisibilityGate.ShouldDeferStart(IsWindowVisible))
            {
                Interlocked.Exchange(ref _listingRetryPending, 1);
                Debug.WriteLine(
                    "[GroupFacade] QueryAllGroupsAsync deferred (window hidden); retry armed");
                return;
            }

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

            CancellationToken listingToken = _listingCancel.Begin();

            // Armed on the way out: full reuse when the listing answered; short failure backoff
            // when it timed out, so callers cannot stack another 60s IQ on top of a dead one.
            bool listingAnswered = false;
            bool abortedForVisibility = false;
            try
            {
                try
                {
                    Debug.WriteLine("[GroupFacade] Fetching all participating groups...");
                    BinaryNode response = await _wire
                        .QueryParticipatingGroupsAsync(listingToken)
                        .ConfigureAwait(false);
                    if (GroupListingVisibilityGate.ShouldAbortInFlight(
                            IsWindowVisible,
                            listingToken.IsCancellationRequested))
                    {
                        abortedForVisibility = true;
                        Interlocked.Exchange(ref _listingRetryPending, 1);
                        _diagnostics?.Write(
                            "startup-phase",
                            "background-groups:aborted",
                            "reason=window-hidden;stage=after-listing-iq");
                    }
                    else if (response != null)
                    {
                        listingAnswered = true;
                        var groupNodes = response.FindAllDescendants("group");
                        Debug.WriteLine("[GroupFacade] QueryAllGroupsAsync found " + groupNodes.Count + " group nodes.");
                        await _apply.ApplyListingAsync(_reader.ReadListing(groupNodes));
                    }
                }
                catch (OperationCanceledException)
                {
                    abortedForVisibility = true;
                    Interlocked.Exchange(ref _listingRetryPending, 1);
                    _diagnostics?.Write(
                        "startup-phase",
                        "background-groups:aborted",
                        "reason=cancelled;stage=listing-iq");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GroupFacade] Group query failed: " + ex.Message);
                }

                // Outside the try above. The per-group fallback names the groups the listing missed,
                // so a listing that failed is the case it exists for.
                if (!abortedForVisibility)
                {
                    try
                    {
                        abortedForVisibility = await QueryUnresolvedGroupMetadataAsync(
                            _workload.GroupMetadataBatchSize,
                            listingToken);
                        if (abortedForVisibility)
                        {
                            Interlocked.Exchange(ref _listingRetryPending, 1);
                            _diagnostics?.Write(
                                "startup-phase",
                                "background-groups:aborted",
                                "reason=window-hidden;stage=unresolved-metadata");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        abortedForVisibility = true;
                        Interlocked.Exchange(ref _listingRetryPending, 1);
                        _diagnostics?.Write(
                            "startup-phase",
                            "background-groups:aborted",
                            "reason=cancelled;stage=unresolved-metadata");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[GroupFacade] Group metadata fallback failed: " + ex.Message);
                    }
                }

                if (abortedForVisibility)
                {
                    _queryWindow.ArmFailure(DateTime.UtcNow);
                }
                else if (listingAnswered)
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

        /// <returns>True when aborted because the window hid mid-pass.</returns>
        private async Task<bool> QueryUnresolvedGroupMetadataAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            if (!_wire.IsHandshakeComplete)
            {
                return false;
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
                return false;
            }

            int attempts = 0;
            int resolved = 0;
            bool abortedForVisibility = false;
            var pending = unresolved.Take(Math.Max(1, limit)).ToList();
            foreach (var chat in pending)
            {
                if (GroupListingVisibilityGate.ShouldAbortInFlight(
                        IsWindowVisible,
                        cancellationToken.IsCancellationRequested))
                {
                    abortedForVisibility = true;
                    Debug.WriteLine(
                        "[GroupFacade] QueryUnresolvedGroupMetadataAsync aborted (window hidden)");
                    break;
                }

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
                    BinaryNode response = await _wire
                        .QueryGroupMetadataAsync(chat.JID, cancellationToken)
                        .ConfigureAwait(false);
                    string subject = _reader.ExtractSubject(response, chat.JID);
                    if (_apply.TryCacheSyncedSubject(chat.JID, subject, chat.Name))
                    {
                        resolved++;
                    }

                    // Flags-only — same as open-path. Default ApplyMetadata includeRoster=true
                    // would materialize drafts for every unresolved group and freeze the first
                    // open that then RebuildParticipantLookup'd a 500+ roster.
                    await _apply.ApplyMetadataAsync(
                        chat.JID,
                        _reader.ReadMetadata(response, chat.JID, includeRoster: false),
                        includeRoster: false,
                        cancellationToken,
                        subject);
                }
                catch (OperationCanceledException)
                {
                    abortedForVisibility = true;
                    break;
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
            return abortedForVisibility;
        }
    }
}
