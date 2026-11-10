using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class StatusComposerViewModelTests
    {
        [Fact]
        public void Reset_picks_a_palette_background_and_clears_draft()
        {
            var vm = new StatusComposerViewModel(new CountingDispatcher(), random: new Random(7));
            vm.Text = "draft";

            vm.Reset();

            Assert.Equal(string.Empty, vm.Text);
            Assert.Contains(vm.BackgroundColor, StatusComposerViewModel.BackgroundPalette);
            Assert.Equal(StatusComposerViewModel.FontPalette[0], vm.FontFamilyName);
            Assert.False(vm.IsEmojiTrayOpen);
        }

        [Fact]
        public void CycleBackground_always_changes_color()
        {
            var vm = new StatusComposerViewModel(new CountingDispatcher(), random: new Random(1));
            vm.Reset();

            for (int i = 0; i < 20; i++)
            {
                string before = vm.BackgroundColor;
                vm.CycleBackgroundCommand.Execute(null);
                Assert.NotEqual(before, vm.BackgroundColor);
                Assert.Contains(vm.BackgroundColor, StatusComposerViewModel.BackgroundPalette);
            }
        }

        [Fact]
        public void CycleFont_walks_the_font_list_and_wraps()
        {
            var vm = new StatusComposerViewModel(new CountingDispatcher());

            for (int i = 1; i <= StatusComposerViewModel.FontPalette.Count; i++)
            {
                vm.CycleFontCommand.Execute(null);
                Assert.Equal(
                    StatusComposerViewModel.FontPalette[i % StatusComposerViewModel.FontPalette.Count],
                    vm.FontFamilyName);
            }
        }

        [Fact]
        public void Send_needs_text_and_without_a_status_service_only_shows_unavailable_dialog()
        {
            var dialogs = new RecordingDialogs();
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            Assert.False(vm.SendCommand.CanExecute(null));
            vm.Text = "hello";
            Assert.True(vm.SendCommand.CanExecute(null));

            vm.SendCommand.Execute(null);

            Assert.Equal(1, dialogs.Shown);
            Assert.Equal("hello", vm.Text);
        }

        [Fact]
        public void Privacy_defaults_to_all_contacts()
        {
            var vm = new StatusComposerViewModel(new CountingDispatcher());

            Assert.Equal(StatusAudienceMode.Contacts, vm.AudienceMode);
            Assert.Equal("Status: Contacts", vm.PrivacyLabel);
            Assert.Equal("0 contacts excluded", vm.ExcludedCountLabel);
            Assert.Equal("0 contacts selected", vm.OnlyShareWithCountLabel);
        }

        [Fact]
        public void Privacy_except_opens_picker_then_reshows_dialog_and_commits_on_confirm()
        {
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickExcluded,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);

            Assert.True(vm.IsContactPickerOpen);
            Assert.Equal(StatusAudienceMode.ContactsExcept, vm.ContactPickerMode);
            Assert.True(vm.ContactPickerConfirmCommand.CanExecute(null));

            vm.SetPickerSelection(new[] { "1@s.whatsapp.net", "2@s.whatsapp.net" });
            Assert.Equal("2 selected", vm.ContactPickerSubtitle);
            vm.ContactPickerConfirmCommand.Execute(null);

            Assert.False(vm.IsContactPickerOpen);
            Assert.Equal(2, dialogs.PrivacyShown);
            Assert.Equal(StatusAudienceMode.ContactsExcept, vm.AudienceMode);
            Assert.Equal(new[] { "1@s.whatsapp.net", "2@s.whatsapp.net" }, vm.ExcludedContactJids);
            Assert.Equal("2 contacts excluded", vm.ExcludedCountLabel);
            Assert.Equal("Status: Contacts except 2", vm.PrivacyLabel);
        }

        [Fact]
        public void Privacy_picker_reopens_with_the_saved_selection()
        {
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickOnlyShareWith,
                StatusPrivacyDialogChoice.Confirm,
                StatusPrivacyDialogChoice.PickOnlyShareWith);
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);
            Assert.Equal("1 contacts selected", vm.OnlyShareWithCountLabel);

            vm.PrivacyCommand.Execute(null);

            Assert.True(vm.IsContactPickerOpen);
            Assert.Equal(StatusAudienceMode.OnlyShareWith, vm.ContactPickerMode);
            Assert.True(vm.IsPickerContactSelected("1@s.whatsapp.net"));
            Assert.False(vm.IsPickerContactSelected("2@s.whatsapp.net"));
        }

        [Fact]
        public void Privacy_only_share_with_needs_at_least_one_contact()
        {
            var dialogs = new RecordingDialogs(StatusPrivacyDialogChoice.PickOnlyShareWith);
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);

            Assert.False(vm.ContactPickerConfirmCommand.CanExecute(null));
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            Assert.True(vm.ContactPickerConfirmCommand.CanExecute(null));
        }

        [Fact]
        public void Privacy_cancelled_picker_keeps_the_previous_audience_and_list()
        {
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickExcluded,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            Assert.True(vm.TryHandleBack());

            Assert.False(vm.IsContactPickerOpen);
            Assert.Equal(StatusAudienceMode.Contacts, vm.AudienceMode);
            Assert.Empty(vm.ExcludedContactJids);
        }

        [Fact]
        public void Privacy_cancel_reverts_the_pending_choice()
        {
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickExcluded,
                StatusPrivacyDialogChoice.Cancel);
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);

            Assert.Equal(StatusAudienceMode.Contacts, vm.AudienceMode);
            Assert.Single(vm.ExcludedContactJids);
        }

        [Fact]
        public void Publish_sends_to_only_share_with_list_then_toasts_and_closes()
        {
            var status = new RecordingStatus();
            var notifications = new RecordingNotifications();
            var navigator = new RecordingNavigator();
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickOnlyShareWith,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(
                new CountingDispatcher(),
                dialogs,
                navigator: navigator,
                status: status,
                notifications: notifications);
            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);
            vm.Text = " hi 😀 ";

            vm.SendCommand.Execute(null);

            Assert.NotNull(status.LastPost);
            Assert.Equal("hi 😀", status.LastPost.Text);
            Assert.Equal(new[] { "1@s.whatsapp.net" }, status.LastPost.RecipientJids);
            Assert.Equal(0xFFFFFFFFu, status.LastPost.TextArgb);
            Assert.Equal(StatusComposerViewModel.ParseArgb(vm.BackgroundColor), status.LastPost.BackgroundArgb);
            Assert.Equal(StatusComposerViewModel.FontWireTypes[0], status.LastPost.Font);
            Assert.False(vm.IsPublishing);
            Assert.Equal("Status published", notifications.LastTitle);
            Assert.Equal(1, navigator.CloseFullScreenCalls);
        }

        [Fact]
        public void Publish_to_all_contacts_uses_people_from_the_chat_list()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            chats.Chats.Add(new ChatItem { JID = "1@s.whatsapp.net", Name = "Ana", Kind = ChatKind.Direct });
            chats.Chats.Add(new ChatItem { JID = "2@s.whatsapp.net", Name = "Bia", Kind = ChatKind.Direct });
            chats.Chats.Add(new ChatItem { JID = "9@g.us", Name = "Group", Kind = ChatKind.Group });
            var status = new RecordingStatus();
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickExcluded,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(dispatcher, dialogs, status: status, chatState: chats);
            vm.PrivacyCommand.Execute(null);
            Assert.Equal(2, CountRows(vm));
            vm.SetPickerSelection(new[] { "2@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);
            vm.Text = "hi";

            vm.SendCommand.Execute(null);

            Assert.Equal(new[] { "1@s.whatsapp.net" }, status.LastPost.RecipientJids);
        }

        [Fact]
        public void Publish_failure_drops_the_overlay_and_keeps_the_draft()
        {
            var status = new RecordingStatus { Fail = true };
            var navigator = new RecordingNavigator();
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickOnlyShareWith,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(
                new CountingDispatcher(),
                dialogs,
                navigator: navigator,
                status: status,
                notifications: new RecordingNotifications());
            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);
            vm.Text = "hi";

            vm.SendCommand.Execute(null);

            Assert.False(vm.IsPublishing);
            Assert.Equal("hi", vm.Text);
            Assert.Equal(1, dialogs.Shown);
            Assert.Equal(0, navigator.CloseFullScreenCalls);
        }

        [Fact]
        public void A_refused_publish_reports_the_server_code_and_does_not_toast_success()
        {
            var status = new RecordingStatus { RefuseWith = "400" };
            var navigator = new RecordingNavigator();
            var notifications = new RecordingNotifications();
            var dialogs = new RecordingDialogs(
                StatusPrivacyDialogChoice.PickOnlyShareWith,
                StatusPrivacyDialogChoice.Confirm);
            var vm = new StatusComposerViewModel(
                new CountingDispatcher(),
                dialogs,
                navigator: navigator,
                status: status,
                notifications: notifications);
            vm.PrivacyCommand.Execute(null);
            vm.SetPickerSelection(new[] { "1@s.whatsapp.net" });
            vm.ContactPickerConfirmCommand.Execute(null);
            vm.Text = "hi";

            vm.SendCommand.Execute(null);

            Assert.False(vm.IsPublishing);
            Assert.Equal("hi", vm.Text);
            Assert.Equal(1, dialogs.Shown);
            Assert.Contains("400", dialogs.LastContent);
            Assert.Equal(0, navigator.CloseFullScreenCalls);
            Assert.Null(notifications.LastTitle);
        }

        [Fact]
        public void Publish_without_recipients_does_not_send()
        {
            var status = new RecordingStatus();
            var dialogs = new RecordingDialogs();
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs, status: status);
            vm.Text = "hi";

            vm.SendCommand.Execute(null);

            Assert.Null(status.LastPost);
            Assert.Equal(1, dialogs.Shown);
        }

        [Fact]
        public void ParseArgb_reads_palette_hex()
        {
            Assert.Equal(0xFF128C7Eu, StatusComposerViewModel.ParseArgb("#FF128C7E"));
            Assert.Equal(0xFF5696FFu, StatusComposerViewModel.ParseArgb("5696FF"));
            Assert.Equal(StatusComposerViewModel.FontPalette.Count, StatusComposerViewModel.FontWireTypes.Count);
        }

        private static int CountRows(StatusComposerViewModel vm)
        {
            int count = 0;
            foreach (var group in vm.PickerGroupedContacts)
            {
                count += group.Count;
            }

            return count;
        }

        [Fact]
        public void Emoji_tray_toggles_glyph_and_insert_appends()
        {
            var vm = new StatusComposerViewModel(new CountingDispatcher());

            vm.ToggleEmojiTrayCommand.Execute(null);
            Assert.True(vm.IsEmojiTrayOpen);
            Assert.Equal(StatusComposerViewModel.GlyphKeyboard, vm.EmojiToggleGlyph);

            vm.Text = "Hi";
            vm.InsertEmojiCommand.Execute("😀");
            Assert.Equal("Hi😀", vm.Text);

            Assert.True(vm.TryHandleBack());
            Assert.False(vm.IsEmojiTrayOpen);
            Assert.Equal(StatusComposerViewModel.GlyphEmoji, vm.EmojiToggleGlyph);
        }

        [Fact]
        public void Text_tile_lowers_the_dock_and_opens_the_full_screen_composer()
        {
            var navigator = new RecordingNavigator();
            var vm = new StatusListViewModel(new EmptyStatus(), new CountingDispatcher(), navigator: navigator);

            vm.NewStatusCommand.Execute(null);
            Assert.True(vm.IsNewStatusPickerOpen);
            Assert.False(vm.StartMediaStatusCommand.CanExecute(null));

            vm.StartTextStatusCommand.Execute(null);

            Assert.False(vm.IsNewStatusPickerOpen);
            Assert.Equal(NavigationDestination.StatusComposer, navigator.LastFullScreen);
            Assert.False(vm.TryHandleNewStatusBack());
        }

        [Fact]
        public void Back_lowers_the_open_dock()
        {
            var vm = new StatusListViewModel(new EmptyStatus(), new CountingDispatcher());
            vm.NewStatusCommand.Execute(null);

            Assert.True(vm.TryHandleNewStatusBack());

            Assert.False(vm.IsNewStatusPickerOpen);
        }

        [Fact]
        public void Composer_back_closes_the_full_screen_page()
        {
            var navigator = new RecordingNavigator();
            var vm = new StatusComposerViewModel(new CountingDispatcher(), navigator: navigator);

            vm.BackCommand.Execute(null);

            Assert.Equal(1, navigator.CloseFullScreenCalls);
        }

        [Fact]
        public void Composer_back_with_tray_open_only_closes_the_tray()
        {
            var navigator = new RecordingNavigator();
            var vm = new StatusComposerViewModel(new CountingDispatcher(), navigator: navigator);
            vm.ToggleEmojiTrayCommand.Execute(null);

            Assert.True(vm.TryHandleBack());

            Assert.False(vm.IsEmojiTrayOpen);
            Assert.Equal(0, navigator.CloseFullScreenCalls);
        }

        private sealed class RecordingNavigator : INavigator
        {
            public NavigationDestination? LastFullScreen { get; private set; }

            public int CloseFullScreenCalls { get; private set; }

            public bool CanGoBack => false;

            public bool CanGoBackInShell => false;

            public string CurrentShellRoute => null;

            public bool IsFullScreenOpen => LastFullScreen != null;

            public event EventHandler<string> ShellNavigated { add { } remove { } }

            public event EventHandler FullScreenChanged { add { } remove { } }

            public event EventHandler LocalBackChanged { add { } remove { } }

            public bool HasLocalBack => false;

            public IDisposable PushLocalBack(Func<bool> handler) => new NoopDisposable();

            public bool TryHandleLocalBack() => false;

            public void Navigate(NavigationDestination destination, object parameter = null) { }

            public void NavigateAndClear(NavigationDestination destination, object parameter = null) { }

            public void GoBack() { }

            public void ClearBackStack() { }

            public void AttachShellFrame(object frame) { }

            public void NavigateInShell(NavigationDestination destination, object parameter = null) { }

            public void NavigateInShellAndClear(NavigationDestination destination, object parameter = null) { }

            public void OpenSettings(bool openProfile = false) { }

            public void GoBackInShell() { }

            public void AttachFullScreenFrame(object frame) { }

            public void NavigateFullScreen(NavigationDestination destination, object parameter = null) =>
                LastFullScreen = destination;

            public void CloseFullScreen()
            {
                CloseFullScreenCalls++;
                LastFullScreen = null;
            }

            public void PurgeShellNavigation() { }

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        private sealed class EmptyStatus : IStatusService
        {
            public event EventHandler StatusUpdated { add { } remove { } }

            public Task<IReadOnlyList<StatusAuthorItem>> GetActiveAuthorsAsync() =>
                Task.FromResult<IReadOnlyList<StatusAuthorItem>>(Array.Empty<StatusAuthorItem>());

            public Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid) =>
                Task.FromResult<IReadOnlyList<HistoryStatus>>(Array.Empty<HistoryStatus>());

            public Task<string> EnsureMediaAsync(HistoryStatus status) => Task.FromResult<string>(null);

            public Task MarkViewedAsync(string authorJid, string messageId) => Task.CompletedTask;

            public Task RecordViewersAsync(IEnumerable<string> messageIds, string viewerJid, DateTime viewedAtUtc) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<OwnStatusItem>> GetOwnStatusesAsync(string selfJid) =>
                Task.FromResult<IReadOnlyList<OwnStatusItem>>(Array.Empty<OwnStatusItem>());

            public string ResolveMediaDisplayUri(string downloadedUri, HistoryStatus item) => downloadedUri;

            public bool ShowStatusAsVideo(ChatPreviewKind kind, string displayUri) => false;

            public bool ShowStatusAsImage(ChatPreviewKind kind, string displayUri) => false;

            public string DescribeStatusItem(HistoryStatus item, int index = -1) => string.Empty;

            public Task IngestLiveFromDecryptedAsync(IncomingLiveStatusRequest request) => Task.CompletedTask;

            public Task<string> PublishTextStatusAsync(StatusTextPost post) => Task.FromResult("id");
        }

        private sealed class RecordingStatus : IStatusService
        {
            public bool Fail { get; set; }

            /// <summary>Ack error code the server answers the publish with, when set.</summary>
            public string RefuseWith { get; set; }

            public StatusTextPost LastPost { get; private set; }

            public event EventHandler StatusUpdated { add { } remove { } }

            public Task<string> PublishTextStatusAsync(StatusTextPost post)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("offline");
                }

                if (!string.IsNullOrEmpty(RefuseWith))
                {
                    throw new StatusPublishRefusedException(RefuseWith, "bad-request");
                }

                LastPost = post;
                return Task.FromResult("id");
            }

            public Task<IReadOnlyList<StatusAuthorItem>> GetActiveAuthorsAsync() =>
                Task.FromResult<IReadOnlyList<StatusAuthorItem>>(Array.Empty<StatusAuthorItem>());

            public Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid) =>
                Task.FromResult<IReadOnlyList<HistoryStatus>>(Array.Empty<HistoryStatus>());

            public Task<string> EnsureMediaAsync(HistoryStatus status) => Task.FromResult<string>(null);

            public Task MarkViewedAsync(string authorJid, string messageId) => Task.CompletedTask;

            public Task RecordViewersAsync(IEnumerable<string> messageIds, string viewerJid, DateTime viewedAtUtc) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<OwnStatusItem>> GetOwnStatusesAsync(string selfJid) =>
                Task.FromResult<IReadOnlyList<OwnStatusItem>>(Array.Empty<OwnStatusItem>());

            public string ResolveMediaDisplayUri(string downloadedUri, HistoryStatus item) => downloadedUri;

            public bool ShowStatusAsVideo(ChatPreviewKind kind, string displayUri) => false;

            public bool ShowStatusAsImage(ChatPreviewKind kind, string displayUri) => false;

            public string DescribeStatusItem(HistoryStatus item, int index = -1) => string.Empty;

            public Task IngestLiveFromDecryptedAsync(IncomingLiveStatusRequest request) => Task.CompletedTask;
        }

        private sealed class RecordingNotifications : INotificationService
        {
            public string LastTitle { get; private set; }

            public void ShowToast(string title, string body) => LastTitle = title;

            public void Initialize() { }

            public void NotifyIncomingMessage(
                string chatJid, string chatName, string senderName, string preview, bool isGroup, bool isMuted,
                bool suppressToast, int totalUnread, string avatarUrl = null, int chatUnread = -1,
                bool isCommunityAnnounce = false, bool isFavorite = false, bool isChannel = false) { }

            public void NotifyCallAlert(
                string title, string body, string chatJid = null, bool updateLiveTile = false, bool isFavorite = false) { }

            public void RefreshUnreadState(IEnumerable<ChatItem> chats) { }

            public void UpdateBadge(int totalUnread) { }

            public void ClearAll() { }

            public void OnNotificationsConfigChanged() { }
        }

        private sealed class RecordingDialogs : IDialogService
        {
            private readonly Queue<StatusPrivacyDialogChoice> _privacyChoices;

            public RecordingDialogs(params StatusPrivacyDialogChoice[] privacyChoices)
            {
                _privacyChoices = new Queue<StatusPrivacyDialogChoice>(privacyChoices);
            }

            public int PrivacyShown { get; private set; }

            public Task<StatusPrivacyDialogChoice> ShowStatusPrivacyAsync(StatusComposerViewModel composer)
            {
                PrivacyShown++;
                return Task.FromResult(
                    _privacyChoices.Count > 0 ? _privacyChoices.Dequeue() : StatusPrivacyDialogChoice.Cancel);
            }

            public int Shown { get; private set; }

            public string LastContent { get; private set; }

            public Task ShowMessageAsync(string title, string content, string closeButtonText)
            {
                Shown++;
                LastContent = content;
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

            public Task<LockScreenNotificationScope?> ShowNotificationFilterAsync(LockScreenNotificationScope current) =>
                Task.FromResult<LockScreenNotificationScope?>(null);

            public Task<bool> ShowChatExportOptionsAsync() => Task.FromResult(false);

            public Task ShowCallBackAsync(string phoneDigitsOrRaw) => Task.CompletedTask;

            public Task<GroupEphemeralDuration?> ShowGroupEphemeralAsync(GroupEphemeralDuration current) =>
                Task.FromResult<GroupEphemeralDuration?>(null);

            public Task<ChatExportReadyChoice> ShowChatExportReadyAsync(string fileName, string infoText) =>
                Task.FromResult(ChatExportReadyChoice.Dismissed);
        }
    }
}
