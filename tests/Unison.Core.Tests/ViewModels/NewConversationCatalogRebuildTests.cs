using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    /// <summary>
    /// DisplayNamesChanged / Chats bursts must coalesce into one catalog rebuild,
    /// matching ChatDetailInfoViewModel's 400ms ScheduleRebuild debounce.
    /// </summary>
    public sealed class NewConversationCatalogRebuildTests
    {
        [Fact]
        public async Task DisplayNamesChanged_burst_coalesces_into_one_rebuild_after_debounce()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            var vm = CreateVm(chats, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NewConversationViewModel.GroupedContacts))
                {
                    rebuilds++;
                }
            };

            vm.Open();
            Assert.Equal(1, rebuilds);
            int passesAfterOpen = dispatcher.Passes;

            for (int i = 0; i < 20; i++)
            {
                chats.NotifyDisplayNamesChanged();
            }

            Assert.Equal(1, rebuilds);
            Assert.Equal(passesAfterOpen, dispatcher.Passes);

            Assert.True(
                SpinWait.SpinUntil(() => rebuilds >= 2, TimeSpan.FromSeconds(2)),
                "Expected one coalesced rebuild after the debounce window.");
            await Task.Delay(200).ConfigureAwait(true);

            Assert.Equal(2, rebuilds);
            Assert.Equal(passesAfterOpen + 1, dispatcher.Passes);
        }

        [Fact]
        public async Task Closing_during_debounce_skips_scheduled_rebuild()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            var vm = CreateVm(chats, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NewConversationViewModel.GroupedContacts))
                {
                    rebuilds++;
                }
            };

            vm.Open();
            Assert.Equal(1, rebuilds);

            chats.NotifyDisplayNamesChanged();
            vm.Close();

            await Task.Delay(700).ConfigureAwait(true);

            Assert.Equal(1, rebuilds);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public void Open_rebuilds_catalog_immediately()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            chats.Chats.Add(new ChatItem
            {
                JID = "alice@s.whatsapp.net",
                Name = "Alice",
                Kind = ChatKind.Direct
            });
            var vm = CreateVm(chats, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NewConversationViewModel.GroupedContacts))
                {
                    rebuilds++;
                }
            };

            vm.Open();

            Assert.Equal(1, rebuilds);
            Assert.True(vm.GroupedContacts.Count > 0);
            Assert.Equal(0, dispatcher.Passes);
        }

        [Fact]
        public void SearchQuery_rebuilds_catalog_immediately_while_open()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            chats.Chats.Add(new ChatItem
            {
                JID = "alice@s.whatsapp.net",
                Name = "Alice",
                Kind = ChatKind.Direct
            });
            chats.Chats.Add(new ChatItem
            {
                JID = "bob@s.whatsapp.net",
                Name = "Bob",
                Kind = ChatKind.Direct
            });
            var vm = CreateVm(chats, dispatcher);
            vm.Open();
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NewConversationViewModel.GroupedContacts))
                {
                    rebuilds++;
                }
            };

            vm.SearchQuery = "Alice";

            Assert.Equal(1, rebuilds);
            Assert.Equal(0, dispatcher.Passes);
        }

        private static NewConversationViewModel CreateVm(
            IChatStateStore chats,
            CountingDispatcher dispatcher)
        {
            return new NewConversationViewModel(
                chats,
                new StubContacts(),
                chats: null,
                profile: null,
                jids: new SelfJids(),
                dialogs: new FakeDialogs(),
                newChatFactory: new FakeNewChatFactory(),
                strings: null,
                dispatcher: dispatcher);
        }

        private sealed class FakeDialogs : IDialogService
        {
            public Task ShowMessageAsync(string title, string content, string closeButtonText) =>
                Task.CompletedTask;

            public Task<bool> ShowConfirmAsync(
                string title, string content, string primaryButtonText, string closeButtonText) =>
                Task.FromResult(false);

            public Task<MessageDeleteChoice> ShowMessageDeleteAsync(
                string content,
                bool offerDeleteForEveryone,
                string deleteForMeText,
                string deleteForEveryoneText,
                string cancelText) =>
                Task.FromResult(MessageDeleteChoice.Cancel);

            public Task<ConfirmPromptResult> ShowConfirmResultAsync(
                string title, string content, string primaryButtonText, string closeButtonText) =>
                Task.FromResult(ConfirmPromptResult.Cancel);

            public Task<string> ShowInputAsync(
                string title, string prompt, string placeholder, string primaryButtonText, string closeButtonText) =>
                Task.FromResult<string>(null);

            public Task ShowPairingCodeAsync(LoginViewModel loginVm, string code) => Task.CompletedTask;

            public Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm) =>
                Task.FromResult<string>(null);

            public Task<bool> ShowGroupPreviewAsync(GroupPreviewDialogViewModel previewVm) =>
                Task.FromResult(false);

            public Task<ImageSendPreviewResult> ShowImageSendPreviewAsync(
                IReadOnlyList<byte[]> imageBytes, string infoText, string initialCaption = null) =>
                Task.FromResult(new ImageSendPreviewResult());

            public Task<ImageSendPreviewResult> ShowDocumentSendPreviewAsync(
                string fileName, string infoText, string initialCaption = null) =>
                Task.FromResult(new ImageSendPreviewResult());

            public Task ShowQrFullscreenAsync(LoginViewModel loginVm) => Task.CompletedTask;

            public Task ShowQrFullscreenAsync(string qrData) => Task.CompletedTask;

            public Task ShowWhatsAppSettingsAsync(SettingsViewModel settingsVm, bool openProfile = false) =>
                Task.CompletedTask;

            public Task ShowReactionsDialogAsync(ChatMessageViewModel messageVm) => Task.CompletedTask;

            public Task ShowRuntimeDiagnosticsAsync() => Task.CompletedTask;

            public Task<StartTileSelection> ShowStartTileMarkAsync(StartTileMark current, bool transparent) =>
                Task.FromResult<StartTileSelection>(null);

            public Task<LockScreenNotificationScope?> ShowNotificationFilterAsync(
                LockScreenNotificationScope current) =>
                Task.FromResult<LockScreenNotificationScope?>(null);

            public Task<bool> ShowChatExportOptionsAsync() => Task.FromResult(false);

            public Task<ChatExportReadyChoice> ShowChatExportReadyAsync(string fileName, string infoText) =>
                Task.FromResult(ChatExportReadyChoice.Dismissed);

            public Task<StatusPrivacyDialogChoice> ShowStatusPrivacyAsync(StatusComposerViewModel composer) =>
                Task.FromResult(StatusPrivacyDialogChoice.Cancel);

            public Task ShowCallBackAsync(string phoneDigitsOrRaw) => Task.CompletedTask;

            public Task<GroupEphemeralDuration?> ShowGroupEphemeralAsync(GroupEphemeralDuration current) =>
                Task.FromResult<GroupEphemeralDuration?>(null);
        }

        private sealed class StubContacts : IContactService
        {
            public event EventHandler DisplayNamesUpdated { add { } remove { } }

            public void NotifyDisplayNamesUpdated() { }

            public void NotifyAvatarCached(string jid, string localAvatarUrl) { }

            public void NotifyJidAliasResolved(string phoneJid, string lidJid, string source) { }

            public void FlushDeferredAliasFollowUp(string source) { }

            public string ResolveDisplayName(string jid, string context) => jid;

            public bool IsSelf(string jid) => false;

            public string ResolvePlaceholderChatName(string jid) => jid;

            public IReadOnlyDictionary<string, string> PhoneContactNamesByJid { get; } =
                new Dictionary<string, string>();

            public void MarkAvatarImageLoadFailed(ChatItem chat, string reason) { }

            public Task<Dictionary<string, string>> SyncLocalContactsAsync(
                IEnumerable<string> directChatJids, CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<string, string>());

            public Task RetrieveContactPicturesAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task HydrateCachedAvatarUrisAsync(string reason) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsAsync(string groupJid) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsForJidsAsync(string groupJid, IReadOnlyList<string> memberJids) =>
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

            public void TriggerBackgroundResolution() { }

            public void TriggerDeferredAvatarRetry() { }

            public void CancelBackgroundResolution() { }

            public Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false) =>
                Task.CompletedTask;

            public void RequestAvatarRefresh(ChatItem chat, bool force = false) { }

            public void CancelAliasFollowUp() { }

            public Task ApplyRemoteGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid) =>
                Task.CompletedTask;

            public Task ApplyRemoteContactNameAsync(string jid, string name) => Task.CompletedTask;

            public Task ApplyRemoteSelfPushNameAsync(string name) => Task.CompletedTask;

            public void CaptureSelfPushName(string pushName, string source = null) { }

            public void RememberPersonName(string jid, string displayName) { }

            public void AcceptIncomingPushName(
                string rawName, bool isFromMe, string participantJid, string fromJid, bool isOffline) { }

            public void ApplyStanzaNotifyPushName(string fromJid, string notify, string participantJid) { }

            public void NotifyInboundMessageNode(string fromJid, string notify, string participantJid) { }

            public void RegisterIncomingEnvelopeAliasHints(
                string senderLid, string fromJid, string peerRecipientPn, string peerRecipientLid,
                string recipientJid, string participant, string participantAlt) { }

            public Task ApplyRemoteContactUpdatesAsync(IReadOnlyList<AccountContactUpdate> contacts) =>
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
                string displayName, string phoneDigits, string photoUri = null, string jid = null) =>
                Task.FromResult(false);

            public Task SetPublishContactsToWindowsAsync(bool enabled) => Task.CompletedTask;

            public Task BlockContactAsync(string jid) => Task.CompletedTask;
        }

        private sealed class SelfJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;

            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }

        private sealed class FakeNewChatFactory : INewChatDialogViewModelFactory
        {
            public NewChatDialogViewModel Create() => null;
        }
    }
}
