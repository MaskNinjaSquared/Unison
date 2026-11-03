using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
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
        public void Send_needs_text_and_only_shows_unavailable_dialog()
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
        public void Privacy_opens_an_empty_dialog()
        {
            var dialogs = new RecordingDialogs();
            var vm = new StatusComposerViewModel(new CountingDispatcher(), dialogs);

            vm.PrivacyCommand.Execute(null);

            Assert.Equal(1, dialogs.Shown);
            Assert.Equal(string.Empty, dialogs.LastContent);
            Assert.Equal("Status: Contacts", vm.PrivacyLabel);
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
        }

        private sealed class RecordingDialogs : IDialogService
        {
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
    }
}
