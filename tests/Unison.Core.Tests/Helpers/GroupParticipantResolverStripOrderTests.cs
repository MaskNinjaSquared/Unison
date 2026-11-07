// =============================================================================
// Strip: roster / hint / 1:1 beat Contacts so BeginSweep skips the service hop.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupParticipantResolverStripOrderTests
    {
        private const string Pn = "5511988887777@s.whatsapp.net";

        [Fact]
        public void ResolveStripDisplayName_uses_roster_before_contacts()
        {
            var contacts = new CountingContacts("Ana Agenda");
            var context = new ParticipantResolutionContext(
                new PassThroughJids(),
                contacts,
                chatState: null,
                people: null);

            var member = new GroupMember { Jid = Pn, DisplayName = "Ana Roster" };
            var group = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                GroupMembers = new List<GroupMember> { member }
            };

            string name = GroupParticipantResolver.ResolveStripDisplayName(
                Pn,
                group,
                context,
                nameHint: null,
                rosterMember: member);

            Assert.Equal("Ana Roster", name);
            Assert.Equal(0, contacts.ResolveCalls);
        }

        [Fact]
        public void ResolveStripDisplayName_uses_hint_before_contacts()
        {
            var contacts = new CountingContacts("Ana Agenda");
            var context = new ParticipantResolutionContext(
                new PassThroughJids(),
                contacts,
                chatState: null,
                people: null);

            string name = GroupParticipantResolver.ResolveStripDisplayName(
                Pn,
                groupChat: null,
                context,
                nameHint: "Ana Hint");

            Assert.Equal("Ana Hint", name);
            Assert.Equal(0, contacts.ResolveCalls);
        }

        [Fact]
        public void ResolveStripDisplayName_falls_back_to_contacts_when_local_labels_missing()
        {
            var contacts = new CountingContacts("~Ana Agenda");
            var context = new ParticipantResolutionContext(
                new PassThroughJids(),
                contacts,
                chatState: null,
                people: null);

            string name = GroupParticipantResolver.ResolveStripDisplayName(
                Pn,
                groupChat: null,
                context);

            Assert.Equal("~Ana Agenda", name);
            Assert.True(contacts.ResolveCalls >= 1);
        }

        private sealed class PassThroughJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }

        private sealed class CountingContacts : IContactService
        {
            private readonly string _name;

            public CountingContacts(string name) => _name = name;

            public int ResolveCalls { get; private set; }

            public event EventHandler DisplayNamesUpdated
            {
                add { }
                remove { }
            }

            public void NotifyDisplayNamesUpdated()
            {
            }

            public void NotifyAvatarCached(string jid, string localAvatarUrl)
            {
            }

            public void FlushDeferredAliasFollowUp(string source) { }

            public void NotifyJidAliasResolved(string phoneJid, string lidJid, string source)
            {
            }

            public string ResolveDisplayName(string jid, string context)
            {
                ResolveCalls++;
                return _name;
            }

            public bool IsSelf(string jid) => false;

            public string ResolvePlaceholderChatName(string jid) => _name;

            public IReadOnlyDictionary<string, string> PhoneContactNamesByJid { get; } =
                new Dictionary<string, string>();

            public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
            {
            }

            public Task<Dictionary<string, string>> SyncLocalContactsAsync(
                IEnumerable<string> directChatJids,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<string, string>());

            public Task RetrieveContactPicturesAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task HydrateCachedAvatarUrisAsync(string reason) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsAsync(string groupJid) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsForJidsAsync(
                string groupJid,
                IReadOnlyList<string> memberJids) =>
                Task.CompletedTask;

            public bool IsHydratingGroupMemberAvatars => false;

            public Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl) => Task.CompletedTask;

            public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false) =>
                Task.CompletedTask;

            public Task RefreshPhoneContactOverlayAsync(bool force) => Task.CompletedTask;

            public Task RefreshChatListAddressBookNamesAsync() => Task.CompletedTask;

            public bool IsContactRefreshRunning => false;

            public bool IsContactRefreshOnCooldown => false;

            public Task ResolveMissingNamesAsync(bool force = false, bool includeGroupListing = true) =>
                Task.CompletedTask;

            public Task RunBackgroundResolutionAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task NotifyBackgroundResolutionDueAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public void TriggerBackgroundResolution()
            {
            }

            public void TriggerDeferredAvatarRetry()
            {
            }

            public void CancelBackgroundResolution()
            {
            }

            public Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false) =>
                Task.CompletedTask;

            public void RequestAvatarRefresh(ChatItem chat, bool force = false)
            {
            }

            public void CancelAliasFollowUp()
            {
            }

            public Task ApplyRemoteGroupSubjectsAsync(
                IEnumerable<KeyValuePair<string, string>> subjectsByJid) =>
                Task.CompletedTask;

            public Task ApplyRemoteContactNameAsync(string jid, string name) => Task.CompletedTask;

            public Task ApplyRemoteSelfPushNameAsync(string name) => Task.CompletedTask;

            public void CaptureSelfPushName(string pushName, string source = null)
            {
            }

            public void AcceptIncomingPushName(
                string pushName,
                bool isGroup,
                string participantJid,
                string fromJid,
                bool isOffline)
            {
            }

            public void NotifyInboundMessageNode(string fromJid, string notify, string participantJid)
            {
            }

            public void ApplyStanzaNotifyPushName(string fromJid, string notify, string participantJid)
            {
            }

            public void RegisterIncomingEnvelopeAliasHints(
                string senderLid,
                string fromJid,
                string peerRecipientPn,
                string peerRecipientLid,
                string recipientJid,
                string participant,
                string participantAlt)
            {
            }

            public void RememberPersonName(string jid, string displayName)
            {
            }

            public Task ApplyRemoteContactUpdatesAsync(
                IReadOnlyList<AccountContactUpdate> contacts) =>
                Task.CompletedTask;

            public Task NormalizePersistedChatNamesAsync() => Task.CompletedTask;

            public Task ApplyResolvedNamesToChatListAsync() => Task.CompletedTask;

            public Task ResolveGroupJoinParticipantsAsync(
                IEnumerable<KeyValuePair<string, string>> participantJidToPhone) =>
                Task.CompletedTask;

            public Task<string> SearchContactAsync(string phoneNumber) => Task.FromResult<string>(null);

            public string TryResolvePhone(string jid, string phoneDigits = null) => null;

            public bool CanAddToAddressBook(string jid, string phoneDigits = null) => false;

            public Task<bool> ShowAddToAddressBookAsync(
                string displayName,
                string phoneDigits,
                string photoUri = null,
                string jid = null) =>
                Task.FromResult(false);

            public Task SetPublishContactsToWindowsAsync(bool enabled) => Task.CompletedTask;

            public Task BlockContactAsync(string jid) => Task.CompletedTask;
        }
    }
}
