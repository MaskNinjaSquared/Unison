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
    public sealed class NewConversationCreateFlowTests
    {
        [Fact]
        public void NewGroup_opens_group_flow_on_members_step()
        {
            var vm = CreateVm();

            vm.NewGroupCommand.Execute(null);

            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(0, vm.GroupStepIndex);
            Assert.Equal("New group", vm.HeaderTitle);
            Assert.Equal("Next", vm.PrimaryActionLabel);
            Assert.Equal("\uE72A", vm.PrimaryActionGlyph);
            Assert.Equal("\uE716", vm.CreateExplainGlyph);
        }

        [Fact]
        public void Group_next_moves_to_info_then_confirm_label()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);

            Assert.Equal(1, vm.GroupStepIndex);
            Assert.Equal("Confirm", vm.PrimaryActionLabel);
            Assert.Equal("\uE73E", vm.PrimaryActionGlyph);
            Assert.Equal("\uE8F1", vm.CreateExplainGlyph);
        }

        [Fact]
        public async Task Group_confirm_shows_not_implemented_and_stays_open()
        {
            var dialogs = new FakeDialogs();
            var vm = CreateVm(dialogs);
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.True(dialogs.MessageShown);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(1, vm.GroupStepIndex);
        }

        [Fact]
        public void Group_back_from_info_returns_to_members()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            Assert.True(vm.TryHandleCreateBack());
            Assert.Equal(0, vm.GroupStepIndex);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
        }

        [Fact]
        public void Group_back_from_members_returns_to_catalog()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);

            Assert.True(vm.TryHandleCreateBack());
            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
        }

        [Fact]
        public void Community_start_persists_across_layout_minimal_toggle()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewCommunityCommand.Execute(null);
            Assert.True(vm.ShowCommunityIntro);

            vm.StartCommunityCreateCommand.Execute(null);
            Assert.False(vm.ShowCommunityIntro);

            vm.ReportLayoutMinimal(false);
            vm.ReportLayoutMinimal(true);
            Assert.False(vm.ShowCommunityIntro);
            Assert.Equal(NewConversationCreateFlow.Community, vm.CreateFlow);
        }

        [Fact]
        public void Leaving_community_flow_resets_started_flag()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewCommunityCommand.Execute(null);
            vm.StartCommunityCreateCommand.Execute(null);

            vm.TryHandleCreateBack();

            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.False(vm.HasStartedCommunityCreate);
        }

        [Fact]
        public void Closing_overlay_clears_create_flow()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            var vm = new NewConversationViewModel(
                chats,
                new StubContacts(),
                chats: null,
                profile: null,
                jids: new SelfJids(),
                dialogs: new FakeDialogs(),
                newChatFactory: new FakeNewChatFactory(),
                strings: null,
                dispatcher: dispatcher);

            vm.Open();
            vm.NewGroupCommand.Execute(null);

            vm.Close();

            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task NewContact_opens_people_editor()
        {
            var local = new FakeLocalContacts();
            var vm = CreateVm(localContacts: local);

            vm.NewContactCommand.Execute(null);
            await local.WaitOpenedAsync().ConfigureAwait(true);

            Assert.Equal(1, local.OpenNewContactCount);
        }

        private static NewConversationViewModel CreateVm(
            FakeDialogs dialogs = null,
            ILocalContactsService localContacts = null)
        {
            var dispatcher = new CountingDispatcher();
            return new NewConversationViewModel(
                new ChatStateStore(dispatcher),
                new StubContacts(),
                chats: null,
                profile: null,
                jids: new SelfJids(),
                dialogs: dialogs ?? new FakeDialogs(),
                newChatFactory: new FakeNewChatFactory(),
                strings: null,
                dispatcher: dispatcher,
                localContacts: localContacts);
        }

        private sealed class FakeLocalContacts : ILocalContactsService
        {
            private readonly TaskCompletionSource<bool> _opened =
                new TaskCompletionSource<bool>();

            public int OpenNewContactCount { get; private set; }

            public Task WaitOpenedAsync() => _opened.Task;

            public Task<Dictionary<string, string>> LoadPhoneContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());

            public bool IsPhoneInAddressBook(string phoneDigits) => false;

            public Task<bool> ShowSystemContactCardAsync(
                string displayName, string phoneDigits, string photoUri) =>
                Task.FromResult(false);

            public Task<bool> OpenNewContactEditorAsync()
            {
                OpenNewContactCount++;
                _opened.TrySetResult(true);
                return Task.FromResult(true);
            }

            public Task PublishAppContactsAsync(IReadOnlyList<AppContactExport> contacts) =>
                Task.CompletedTask;

            public Task ClearPublishedAppContactsAsync() => Task.CompletedTask;
        }

        private sealed class FakeDialogs : IDialogService
        {
            private readonly TaskCompletionSource<bool> _shown =
                new TaskCompletionSource<bool>();

            public bool MessageShown { get; private set; }

            public Task WaitShownAsync() => _shown.Task;

            public Task ShowMessageAsync(string title, string content, string closeButtonText)
            {
                MessageShown = true;
                _shown.TrySetResult(true);
                return Task.CompletedTask;
            }

            public Task<bool> ShowConfirmAsync(string title, string content, string primaryButtonText, string closeButtonText) =>
                Task.FromResult(false);

            public Task<MessageDeleteChoice> ShowMessageDeleteAsync(
                string content, bool offerDeleteForEveryone, string deleteForMeText, string deleteForEveryoneText, string cancelText) =>
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
                byte[] imageBytes, string infoText, string initialCaption = null) =>
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

            public Task<LockScreenNotificationScope?> ShowNotificationFilterAsync(LockScreenNotificationScope current) =>
                Task.FromResult<LockScreenNotificationScope?>(null);

            public Task<bool> ShowChatExportOptionsAsync() => Task.FromResult(false);

            public Task<ChatExportReadyChoice> ShowChatExportReadyAsync(string fileName, string infoText) =>
                Task.FromResult(ChatExportReadyChoice.Dismissed);
        }

        private sealed class StubContacts : IContactService
        {
            public event EventHandler DisplayNamesUpdated { add { } remove { } }

            public void NotifyDisplayNamesUpdated() { }

            public void NotifyAvatarCached(string jid, string localAvatarUrl) { }

            public void NotifyJidAliasResolved(string phoneJid, string lidJid, string source) { }

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
