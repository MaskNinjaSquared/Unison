using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Active conversation: composer, send text/media, mic session + overlay, load-more / history,
    /// and presence watch (Storyboards stay in the view).
    /// Per-bubble media / pin actions live on <see cref="ChatMessageViewModel"/>.
    /// Timeline items via <see cref="IChatMessageVmFactory"/>.
    /// </summary>
    public class ChatDetailViewModel : Observable
    {
        // ── DI ────────────────────────────────────────────────────────────────

        /// <summary>Load / live updates / presence for the active JID.</summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>
        /// The chat list. Message reads still go through the service, because finding a chat's
        /// messages needs the canonical-JID mapping that the store knows nothing about.
        /// </summary>
        private readonly IChatStateStore _chatState;

        /// <summary>Send text / image / audio via the message facade.</summary>
        private readonly IMessageService _messageService;

        /// <summary>Start shortcuts (SecondaryTile / future widgets).</summary>
        private readonly IShortcutService _shortcutService;

        /// <summary>SQLite local chat metadata (live-tile pin / mute).</summary>
        private readonly IChatStore _chatStore;

        /// <summary>Conversation-level actions the account shares: pinning, marking read.</summary>
        private readonly IChatService _chatService;

        /// <summary>Unitary microphone capture (returns session handles).</summary>
        private readonly IAudioRecordingService _audioRecording;

        /// <summary>File pickers — chat attach uses PickChatAttachmentAsync.</summary>
        private readonly IFilePicker _filePicker;

        /// <summary>Confirm/preview/error dialogs (no ContentDialog in Core).</summary>
        private readonly IDialogService _dialogs;

        /// <summary>UI-thread marshaling for live updates / elapsed ticks.</summary>
        private readonly IDispatcher _dispatcher;

        /// <summary>On-demand bubble VMs for the timeline.</summary>
        private readonly IChatMessageVmFactory _messageFactory;

        /// <summary>On-demand profile / group info pane VMs.</summary>
        private readonly IChatDetailInfoViewModelFactory _infoFactory;

        /// <summary>Localized presence / header subtitle copy.</summary>
        private readonly IStringResources _strings;

        /// <summary>Debug session log (visible on Debug screen).</summary>
        private readonly ISessionLogger _sessionLogger;

        /// <summary>Runtime diagnostics journal (Debug screen).</summary>
        private readonly IRuntimeDiagnostics _diagnostics;

        // ── State ─────────────────────────────────────────────────────────────

        private ChatItem _activeChat;
        private string _messageText;
        private bool _isSending;
        private bool _isRecording;
        private string _recordingElapsedText = "0:00";
        private bool _hasActiveChat;
        private bool _isLoadingMessages;
        private bool _isLoadingMore;
        private string _recordingChatJid;
        private bool _isChatDetailInfoOpen;
        private ChatDetailInfoViewModel _chatDetailInfo;

        private IAudioRecordingSession _recordingSession;
        private CancellationTokenSource _elapsedCts;
        private CancellationTokenSource _loadCts;
        private CancellationTokenSource _presenceCts;
        private bool _attached;
        private bool _presenceHandlerAttached;
        private bool _presenceReceived;
        private string _pendingPresenceText;
        private DateTime _presenceOpenedUtc;

        public ChatDetailViewModel(
            IWhatsAppService whatsAppService,
            IChatStateStore chatState,
            IMessageService messageService,
            IShortcutService shortcutService,
            IChatStore chatStore,
            IAudioRecordingService audioRecording,
            IFilePicker filePicker,
            IDialogService dialogs,
            IDispatcher dispatcher,
            IChatMessageVmFactory messageFactory,
            IChatDetailInfoViewModelFactory infoFactory,
            IStringResources strings,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            IChatService chatService = null)
        {
            _chatService = chatService;
            _whatsAppService = whatsAppService;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _messageService = messageService;
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _audioRecording = audioRecording;
            _filePicker = filePicker;
            _dialogs = dialogs;
            _dispatcher = dispatcher;
            _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
            _infoFactory = infoFactory ?? throw new ArgumentNullException(nameof(infoFactory));
            _strings = strings;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;

            Messages = new ObservableCollection<ChatMessageViewModel>();

            SendMessageCommand = new RelayCommand(
                () => _ = RunSafeAsync(SendMessageAsync, "send-text"),
                () => CanCompose && !string.IsNullOrWhiteSpace(MessageText));

            AttachMediaCommand = new RelayCommand(
                () => _ = RunSafeAsync(AttachMediaAsync, "attach"),
                () => CanCompose);

            AttachAudioCommand = new RelayCommand(
                () => _ = RunSafeAsync(AttachAudioAsync, "attach-audio"),
                () => CanCompose);

            // The four below are declared, wired and permanently unavailable. Each needs a send
            // route the app does not have yet - a document upload, a vCard, a location - and
            // saying so through CanExecute means the flyout item and the tile grey themselves
            // out from the same binding as the working ones. The day the route exists, only the
            // predicate changes; no markup moves.
            AttachCameraCommand = new RelayCommand(() => { }, () => false);
            AttachFileCommand = new RelayCommand(() => { }, () => false);
            AttachContactCommand = new RelayCommand(() => { }, () => false);
            AttachLocationCommand = new RelayCommand(() => { }, () => false);

            StartRecordingCommand = new RelayCommand(
                () => _ = RunSafeAsync(StartRecordingAsync, "record-start"),
                () => CanCompose);

            CancelRecordingCommand = new RelayCommand(
                () => _ = RunSafeAsync(CancelRecordingCoreAsync, "record-cancel"),
                () => _isRecording);

            SendRecordingCommand = new RelayCommand(
                () => _ = RunSafeAsync(StopAndSendRecordingAsync, "send-voice"),
                () => _isRecording && !_isSending);

            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
            PinToStartCommand = new RelayCommand(
                () => _ = ToggleWidgetPinAsync(),
                () => ActiveChat != null && !string.IsNullOrWhiteSpace(ActiveChat.JID) && _shortcutService != null && _chatStore != null);
            MuteFor8HoursCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(ChatMuteHelper.FromNow(ChatMuteHelper.EightHours)),
                () => CanMuteActiveChat());
            MuteFor1WeekCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(ChatMuteHelper.FromNow(ChatMuteHelper.OneWeek)),
                () => CanMuteActiveChat());
            MuteForeverCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(ChatMuteHelper.ForeverUnixSeconds),
                () => CanMuteActiveChat());
            UnmuteLocalCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(null),
                () => CanMuteActiveChat() && ActiveChat.IsMutedLocally);
            OpenChatDetailInfoCommand = new RelayCommand(
                OpenActiveChatDetailInfo,
                () => ActiveChat != null);
            OpenChatDetailInfoFromAvatarCommand = new RelayCommand(
                OpenActiveChatDetailInfo,
                () => ActiveChat != null);
            CloseChatDetailInfoCommand = new RelayCommand(CloseChatDetailInfo);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public Task InitializeAsync()
        {
            Attach();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Leave the detail surface: stop elapsed UI, cancel mic, stop presence watch.
        /// Idempotent — call from view Unloaded / navigate-away.
        /// </summary>
        public async Task UninitializeAsync()
        {
            StopPresenceWatch();
            await CancelRecordingCoreAsync();
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            // Live merge into Messages is owned by ChatDetailView.SyncMessagesFromServiceAsync
            // until that path is fully moved here (avoids double-append on the same collection).
        }

        /// <summary>Wrap a domain message for the timeline (view load/sync paths).</summary>
        public ChatMessageViewModel CreateMessageVm(ChatMessage message)
        {
            var vm = _messageFactory.Create(message);
            if (vm != null)
            {
                vm.PinnedChanged -= OnBubblePinnedChanged;
                vm.PinnedChanged += OnBubblePinnedChanged;
            }

            return vm;
        }

        private void OnBubblePinnedChanged(object sender, EventArgs e) =>
            MessagePinnedChanged?.Invoke(this, EventArgs.Empty);

        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler BackRequested;
        public event EventHandler MessageSent;

        /// <summary>Raised after pin/unpin succeeds (view refreshes pinned banner).</summary>
        public event EventHandler MessagePinnedChanged;

        /// <summary>
        /// Presence watch finished: non-null text → animate status; null → fallback-only sequence.
        /// Storyboards remain in the view.
        /// </summary>
        public event EventHandler<string> PresenceAnimationRequested;

        // ── Bindable state ────────────────────────────────────────────────────

        public ObservableCollection<ChatMessageViewModel> Messages { get; }

        public ChatItem ActiveChat
        {
            get => _activeChat;
            private set
            {
                if (_activeChat != null)
                {
                    _activeChat.PropertyChanged -= OnActiveChatPropertyChanged;
                }

                Set(ref _activeChat, value);
                HasActiveChat = value != null;
                if (_activeChat != null)
                {
                    _activeChat.PropertyChanged += OnActiveChatPropertyChanged;
                }

                RaiseComposerCommandsChanged();
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
            }
        }

        /// <summary>
        /// True when the active chat is a group in announce-only mode and the current user is not an admin.
        /// Bind the composer lock UI to this; values refresh when group metadata is applied.
        /// </summary>
        public bool IsGroupLockedForMessages => ActiveChat != null && ActiveChat.IsGroupLockedForMessages;

        public string MessageText
        {
            get => _messageText;
            set
            {
                Set(ref _messageText, value);
                RaiseSendMessageCanExecuteChanged();
            }
        }

        public bool IsSending
        {
            get => _isSending;
            private set
            {
                Set(ref _isSending, value);
                RaiseComposerCommandsChanged();
            }
        }

        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                Set(ref _isRecording, value);
                RaiseComposerCommandsChanged();
            }
        }

        public string RecordingElapsedText
        {
            get => _recordingElapsedText;
            private set => Set(ref _recordingElapsedText, value);
        }

        public bool HasActiveChat
        {
            get => _hasActiveChat;
            private set => Set(ref _hasActiveChat, value);
        }

        /// <summary>Shows the profile / group info pane beside (or over) the chat.</summary>
        public bool IsChatDetailInfoOpen
        {
            get => _isChatDetailInfoOpen;
            private set => Set(ref _isChatDetailInfoOpen, value);
        }

        /// <summary>Current info pane VM (null when closed). Created on demand by the factory.</summary>
        public ChatDetailInfoViewModel ChatDetailInfo
        {
            get => _chatDetailInfo;
            private set => Set(ref _chatDetailInfo, value);
        }

        public bool IsLoadingMessages
        {
            get => _isLoadingMessages;
            private set => Set(ref _isLoadingMessages, value);
        }

        public bool IsLoadingMore
        {
            get => _isLoadingMore;
            set => Set(ref _isLoadingMore, value);
        }

        /// <summary>
        /// Whether the composer accepts input at all. Public because the wide-layout clip button
        /// binds its enabled state to it: with no flyout item available there is nothing worth
        /// opening, and a flyout of six greyed rows is a worse answer than a greyed button.
        /// </summary>
        public bool CanCompose =>
            ActiveChat != null &&
            !_isSending &&
            !_isRecording &&
            !IsGroupLockedForMessages;

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>Sends the current composer text (or pending media) to the active chat.</summary>
        public ICommand SendMessageCommand { get; }

        /// <summary>Opens the picture picker and stages an image for send.</summary>
        public ICommand AttachMediaCommand { get; }

        /// <summary>Opens the music picker and sends the clip as a non-voice audio message.</summary>
        public ICommand AttachAudioCommand { get; }

        /// <summary>Take a photo. Unavailable: there is no capture surface yet.</summary>
        public ICommand AttachCameraCommand { get; }

        /// <summary>
        /// Send an arbitrary document. Unavailable: the socket can build and upload a
        /// DocumentMessage, but nothing in the app asks it to.
        /// </summary>
        public ICommand AttachFileCommand { get; }

        /// <summary>Share a contact card. Unavailable: no vCard send route.</summary>
        public ICommand AttachContactCommand { get; }

        /// <summary>Share a location. Unavailable: no location send route.</summary>
        public ICommand AttachLocationCommand { get; }

        /// <summary>Starts audio capture for a voice note.</summary>
        public ICommand StartRecordingCommand { get; }

        /// <summary>Aborts the in-progress voice recording without sending.</summary>
        public ICommand CancelRecordingCommand { get; }

        /// <summary>Stops recording and sends the captured voice note.</summary>
        public ICommand SendRecordingCommand { get; }

        /// <summary>Leaves the chat detail surface (raises <see cref="BackRequested"/>).</summary>
        public ICommand BackCommand { get; }

        /// <summary>Pins/unpins the active chat Start live tile (toggles SQLite + SecondaryTile).</summary>
        public ICommand PinToStartCommand { get; }

        public ICommand MuteFor8HoursCommand { get; }
        public ICommand MuteFor1WeekCommand { get; }
        public ICommand MuteForeverCommand { get; }
        public ICommand UnmuteLocalCommand { get; }

        /// <summary>Opens the info pane for the active chat (user or group) — header title/status.</summary>
        public ICommand OpenChatDetailInfoCommand { get; }

        /// <summary>Same pane as <see cref="OpenChatDetailInfoCommand"/>; wired from header avatar tap (not a Button).</summary>
        public ICommand OpenChatDetailInfoFromAvatarCommand { get; }

        /// <summary>Closes the info pane (bound to the panel close button).</summary>
        public ICommand CloseChatDetailInfoCommand { get; }

        /// <summary>Label for the live-tile pin menu (localized via string service when bound in code).</summary>
        public string LiveTilePinMenuLabel
        {
            get
            {
                bool pinned = ActiveChat != null && ActiveChat.IsWidgetPinned;
                if (pinned)
                {
                    return _strings != null
                        ? _strings.Get("ChatDetail_UnpinFromStart.Text", "Unpin from Start")
                        : "Unpin from Start";
                }

                return _strings != null
                    ? _strings.Get("ChatDetail_PinToStart.Text", "Pin to Start")
                    : "Pin to Start";
            }
        }

        /// <summary>True when mute submenu should show duration options (not unmuted→unmute-only).</summary>
        public bool ShowMuteDurationOptions => ActiveChat != null && !ActiveChat.IsMutedLocally;

        public bool ShowUnmuteOption => ActiveChat != null && ActiveChat.IsMutedLocally;

        /// <summary>
        /// Re-reads the local chat row before the overflow menu is built. Mute can be changed from
        /// the chat list, from a notification, or on another device, and the menu has to offer
        /// "mute" or "unmute" based on what is true now rather than on what was true when the
        /// conversation was opened.
        /// </summary>
        public void RefreshLocalChatState()
        {
            ChatItem chat = ActiveChat;
            if (chat == null || _chatStore == null)
            {
                return;
            }

            try
            {
                _chatStore.ApplyTo(chat);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] RefreshLocalChatState failed: " + ex.Message);
            }

            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(LiveTilePinMenuLabel));
            RaiseMuteCommandsCanExecuteChanged();
        }

        // ── Actions ───────────────────────────────────────────────────────────

        public void OpenActiveChatDetailInfo()
        {
            if (ActiveChat == null)
            {
                return;
            }

            OpenChatDetailInfo(ActiveChat);
        }

        /// <summary>
        /// Opens (or replaces) the info pane for <paramref name="chat"/>.
        /// Uses <see cref="IChatDetailInfoViewModelFactory.CreateGroup"/> / <c>CreateUser</c>.
        /// </summary>
        public void OpenChatDetailInfo(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            ChatDetailInfoViewModel previous = ChatDetailInfo;
            ChatDetailInfoViewModel next = chat.IsGroup
                ? _infoFactory.CreateGroup(chat)
                : _infoFactory.CreateUser(chat);

            ChatDetailInfo = next;
            IsChatDetailInfoOpen = true;
            previous?.Detach();
            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Opens a 1:1 profile in the same pane (e.g. future group-sender tap).</summary>
        public void OpenUserChatDetailInfo(ChatItem contact)
        {
            if (contact == null)
            {
                return;
            }

            ChatDetailInfoViewModel previous = ChatDetailInfo;
            ChatDetailInfo = _infoFactory.CreateUser(contact);
            IsChatDetailInfoOpen = true;
            previous?.Detach();
        }

        public void CloseChatDetailInfo()
        {
            if (!IsChatDetailInfoOpen && ChatDetailInfo == null)
            {
                return;
            }

            ChatDetailInfoViewModel previous = ChatDetailInfo;
            IsChatDetailInfoOpen = false;
            ChatDetailInfo = null;
            previous?.Detach();
        }

        /// <summary>
        /// The conversation is now on screen, so everything in it counts as seen: the badge is
        /// cleared here and on the phone, and whoever wrote the messages is told they were read.
        /// </summary>
        /// <remarks>
        /// Reading is a consequence of opening a chat, not a command the user issues, so it is
        /// driven from here rather than from a button. It is safe to call on every open - nothing
        /// goes out on the wire when there was nothing unread.
        /// </remarks>
        public async Task MarkChatOpenedAsync(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            try
            {
                if (_chatService != null)
                {
                    await _chatService.MarkReadAsync(chat);
                    return;
                }

                await _whatsAppService.ClearUnreadForChatAsync(chat.JID);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] MarkChatOpened failed: " + ex.Message);
            }
        }

        public void SyncActiveChat(ChatItem chat)
        {
            string previousJid = ActiveChat?.JID;
            string nextJid = chat?.JID;
            bool chatChanged = !string.Equals(previousJid, nextJid, StringComparison.OrdinalIgnoreCase);

            ActiveChat = chat;
            if (chat != null && _chatStore != null)
            {
                _chatStore.ApplyTo(chat);
                _ = SyncLocalStateAsync(chat);
            }

            if (chatChanged || chat == null)
            {
                CloseChatDetailInfo();
            }

            RaisePinToStartCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(LiveTilePinMenuLabel));
            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(IsGroupLockedForMessages));
            if (LooksLikeGroupChat(chat))
            {
                _ = RefreshGroupSendPermissionsSafeAsync(chat.JID);
            }

            if (chat == null)
            {
                MessageText = string.Empty;
                StopPresenceWatch();
                _ = CancelRecordingCoreAsync();
            }
        }

        private static bool LooksLikeGroupChat(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            if (chat.IsGroup)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(chat.JID) &&
                   chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnActiveChatPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(ChatItem.IsGroupLockedForMessages) ||
                e.PropertyName == nameof(ChatItem.IsAnnounceOnly) ||
                e.PropertyName == nameof(ChatItem.MyGroupRole) ||
                e.PropertyName == nameof(ChatItem.IsGroup) ||
                e.PropertyName == nameof(ChatItem.IsGroupAdmin))
            {
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
                RaiseComposerCommandsChanged();
            }
        }

        private async Task RefreshGroupSendPermissionsSafeAsync(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || _whatsAppService == null)
            {
                return;
            }

            try
            {
                await _whatsAppService.RefreshGroupSendPermissionsAsync(groupJid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] RefreshGroupSendPermissions failed: " + ex.Message);
            }
        }

        private async Task SyncLocalStateAsync(ChatItem chat)
        {
            if (chat == null || _chatStore == null)
            {
                return;
            }

            try
            {
                await _chatStore.ApplyToAsync(chat).ConfigureAwait(false);
                // Reconcile SecondaryTile existence once when opening the chat.
                if (_shortcutService != null)
                {
                    bool tileExists = await _shortcutService.IsChatPinnedAsync(chat.JID).ConfigureAwait(false);
                    if (tileExists != chat.IsWidgetPinned)
                    {
                        chat.IsWidgetPinned = tileExists;
                        await _chatStore.UpsertAsync(
                            chat.JID,
                            chat.LocalStatus,
                            chat.IsWidgetPinned,
                            chat.IsChatPinned,
                            chat.MutedUntil).ConfigureAwait(false);
                    }
                }

                await _dispatcher.RunAsync(() =>
                {
                    RaiseMuteCommandsCanExecuteChanged();
                    OnPropertyChanged(nameof(LiveTilePinMenuLabel));
                    OnPropertyChanged(nameof(ShowMuteDurationOptions));
                    OnPropertyChanged(nameof(ShowUnmuteOption));
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] SyncLocalState failed: " + ex.Message);
            }
        }

        private bool CanMuteActiveChat()
        {
            return ActiveChat != null &&
                   !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                   _chatStore != null;
        }

        private async Task ToggleWidgetPinAsync()
        {
            if (ActiveChat == null || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = ActiveChat;
            bool nextPinned = !chat.IsWidgetPinned;
            try
            {
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    // User cancelled the pin dialog — leave SQLite unchanged.
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);
                OnPropertyChanged(nameof(LiveTilePinMenuLabel));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] ToggleWidgetPin failed: " + ex.Message);
            }
        }

        private async Task SetLocalMuteAsync(long? mutedUntilUnixSeconds)
        {
            if (ActiveChat == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = ActiveChat;
            try
            {
                chat.MutedUntil = mutedUntilUnixSeconds;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);
                RaiseMuteCommandsCanExecuteChanged();
                OnPropertyChanged(nameof(ShowMuteDurationOptions));
                OnPropertyChanged(nameof(ShowUnmuteOption));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] SetLocalMute failed: " + ex.Message);
            }
        }

        private void RaiseMuteCommandsCanExecuteChanged()
        {
            (MuteFor8HoursCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MuteFor1WeekCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MuteForeverCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UnmuteLocalCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public async Task SetActiveChatAsync(ChatItem chat)
        {
            if (chat == null && _activeChat != null)
                return;

            StopPresenceWatch();
            await CancelRecordingCoreAsync().ConfigureAwait(false);

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            if (chat != null)
            {
                string canonical = _whatsAppService.GetCanonicalJid(chat.JID);
                if (!string.IsNullOrWhiteSpace(canonical) &&
                    !string.Equals(canonical, chat.JID, StringComparison.OrdinalIgnoreCase))
                {
                    var canonicalChat = _chatState.Chats.FirstOrDefault(c =>
                        string.Equals(_whatsAppService.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                    if (canonicalChat != null)
                        chat = canonicalChat;
                    else
                        chat.JID = canonical;
                }
            }

            ActiveChat = chat;
            if (chat != null && _chatStore != null)
            {
                _chatStore.ApplyTo(chat);
                await SyncLocalStateAsync(chat).ConfigureAwait(false);
            }

            RaisePinToStartCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
            OnPropertyChanged(nameof(LiveTilePinMenuLabel));
            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(IsGroupLockedForMessages));

            if (chat == null)
            {
                Messages.Clear();
                return;
            }

            if (LooksLikeGroupChat(chat))
            {
                _ = RefreshGroupSendPermissionsSafeAsync(chat.JID);
            }

            Messages.Clear();
            IsLoadingMessages = true;
            try
            {
                var loaded = await _whatsAppService.LoadMessagesForChatAsync(chat.JID);
                if (token.IsCancellationRequested) return;

                foreach (var msg in loaded ?? new List<ChatMessage>())
                {
                    if (msg != null)
                        Messages.Add(_messageFactory.Create(msg));
                }

                AppendLiveMessages();

                if (Messages.Count == 0)
                {
                    TryApplyPreviewFallback(chat);
                }

                _ = HydratePendingStickersAsync();
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    IsLoadingMessages = false;
            }
        }

        private void TryApplyPreviewFallback(ChatItem chat)
        {
            if (chat == null || _messageFactory == null || _whatsAppService == null)
            {
                return;
            }

            string selfName = _strings?.Get("Chat_SelfFallbackName", "You") ?? "You";
            ChatMessage fallback = ChatPreviewMessageFactory.TryCreate(chat, selfName);
            if (fallback != null)
            {
                Messages.Add(_messageFactory.Create(fallback));
            }

            _ = _whatsAppService.EnsureHistoryOnDemandAsync(chat.JID, 80);
        }

        public async Task<bool> LoadMoreMessagesAsync()
        {
            if (_activeChat == null) return false;

            IsLoadingMore = true;
            try
            {
                var older = await _whatsAppService.LoadMoreMessagesAsync(_activeChat.JID);
                if (older == null || older.Count == 0)
                    return await _whatsAppService.EnsureHistoryOnDemandAsync(_activeChat.JID, 80);

                for (int i = 0; i < older.Count; i++)
                {
                    if (older[i] != null)
                        Messages.Insert(i, _messageFactory.Create(older[i]));
                }

                _ = HydratePendingStickersAsync();
                return true;
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        public bool IsHistoryOnDemandPending()
        {
            return _activeChat != null && _whatsAppService.IsHistoryOnDemandPending(_activeChat.JID);
        }

        /// <summary>Download sticker media for rows that still only have protocol keys.</summary>
        private async Task HydratePendingStickersAsync()
        {
            var pendingVms = Messages
                .Where(m => m?.Model != null &&
                            m.Model.IsSticker &&
                            !m.Model.HasImage &&
                            !m.Model.IsStickerFailed)
                .ToList();

            foreach (var vm in pendingVms)
            {
                try
                {
                    await vm.EnsureImageReadyAsync(showErrorDialog: false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Sticker hydrate failed: " + ex.Message);
                    if (vm.Model != null)
                    {
                        vm.Model.IsStickerFailed = true;
                    }
                }
            }
        }

        /// <summary>
        /// Subscribe + wait for presence (or group hint). Raises <see cref="PresenceAnimationRequested"/>.
        /// View skips calling this on Windows Mobile (cosmetic cost).
        /// </summary>
        public void StartPresenceWatch(string jid)
        {
            StopPresenceWatch();
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            _presenceCts = new CancellationTokenSource();
            _presenceOpenedUtc = DateTime.UtcNow;
            _presenceReceived = false;
            _pendingPresenceText = null;

            EnsurePresenceHandlerAttached();

            if (jid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _presenceReceived = true;
                _pendingPresenceText = _strings.Get(
                    "ChatDetail_SelectForGroupInfo",
                    "select here for group info");
                _ = RunPresenceTimerAsync(_presenceCts.Token, null);
                return;
            }

            _ = RunPresenceTimerAsync(_presenceCts.Token, jid);
        }

        /// <summary>Cancel presence timer and detach handler.</summary>
        public void StopPresenceWatch()
        {
            try
            {
                _presenceCts?.Cancel();
            }
            catch
            {
            }

            _presenceCts?.Dispose();
            _presenceCts = null;
            _presenceReceived = false;
            _pendingPresenceText = null;
            DetachPresenceHandler();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void RaiseComposerCommandsChanged()
        {
            OnPropertyChanged(nameof(CanCompose));
            RaiseSendMessageCanExecuteChanged();
            (AttachMediaCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SendRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RaiseSendMessageCanExecuteChanged() =>
            (SendMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();

        private void RaisePinToStartCanExecuteChanged()
        {
            (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
        }

        private void EnsurePresenceHandlerAttached()
        {
            if (_presenceHandlerAttached)
            {
                return;
            }

            _messageService.PresenceUpdated += MessageService_PresenceUpdated;
            _presenceHandlerAttached = true;
        }

        private void DetachPresenceHandler()
        {
            if (!_presenceHandlerAttached)
            {
                return;
            }

            _messageService.PresenceUpdated -= MessageService_PresenceUpdated;
            _presenceHandlerAttached = false;
        }

        private void MessageService_PresenceUpdated(object sender, PresenceUpdateEventArgs e)
        {
            if (_presenceCts == null || _presenceCts.IsCancellationRequested)
            {
                return;
            }

            _presenceReceived = true;
            _pendingPresenceText = FormatPresenceText(e?.Presence, e?.LastSeen);
        }

        private async Task RunPresenceTimerAsync(CancellationToken ct, string subscribeJid)
        {
            try
            {
                bool subscribed = false;

                for (int i = 0; i < 30; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_presenceReceived)
                    {
                        break;
                    }

                    if (!subscribed && !string.IsNullOrEmpty(subscribeJid) && _whatsAppService.IsConnected)
                    {
                        await _messageService.SubscribeToPresenceAsync(subscribeJid);
                        subscribed = true;
                    }

                    await Task.Delay(100, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                string text = null;
                if (_presenceReceived && !string.IsNullOrEmpty(_pendingPresenceText))
                {
                    var elapsed = (DateTime.UtcNow - _presenceOpenedUtc).TotalMilliseconds;
                    if (elapsed < 3000)
                    {
                        await Task.Delay((int)(3000 - elapsed), ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    text = _pendingPresenceText;
                }

                await _dispatcher.RunAsync(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    PresenceAnimationRequested?.Invoke(this, text);
                });
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Presence timer: " + ex.Message);
            }
        }

        private string FormatPresenceText(string presence, long? lastSeenEpoch)
        {
            if (presence == "available" || presence == "composing")
            {
                return presence == "composing"
                    ? _strings.Get("ChatDetail_PresenceTyping", "typing...")
                    : _strings.Get("ChatDetail_PresenceOnline", "online");
            }

            if (lastSeenEpoch.HasValue && lastSeenEpoch.Value > 0)
            {
                // netstandard1.4: no DateTimeOffset.FromUnixTimeSeconds
                var lastSeenLocal = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(lastSeenEpoch.Value)
                    .ToLocalTime();
                var now = DateTime.Now;
                var timeStr = lastSeenLocal.ToString("HH:mm");

                if (lastSeenLocal.Date == now.Date)
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenToday", "last seen today at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date == now.Date.AddDays(-1))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenYesterday", "last seen yesterday at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date > now.Date.AddDays(-7))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenWeekday", "last seen on {0} at {1}"),
                        lastSeenLocal.ToString("dddd"),
                        timeStr);
                }

                return string.Format(
                    _strings.Get("ChatDetail_PresenceLastSeenDate", "last seen {0} at {1}"),
                    lastSeenLocal.ToString("dd/MM/yyyy"),
                    timeStr);
            }

            return _strings.Get("ChatDetail_PresenceLastSeenRecently", "last seen recently");
        }

        private async Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(MessageText) || _activeChat == null) return;

            string text = MessageText;
            string jid = _activeChat.JID;
            IsSending = true;
            MessageText = string.Empty;
            // Optimistic bubble is queued inside the service before transport completes —
            // ask the view to stick to bottom immediately.
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("text-start", "jid=" + jid + "; chars=" + text.Length);

            try
            {
                await _messageService.SendTextMessageAsync(jid, text);
                MessageSent?.Invoke(this, EventArgs.Empty);
                LogSend("text-ok", "jid=" + jid);
            }
            catch (UnisonUserException ex)
            {
                MessageText = text;
                await PresentUserExceptionAsync(ex, "text-fail");
            }
            catch (Exception ex)
            {
                MessageText = text;
                await PresentUserExceptionAsync(
                    new TextSendException("SendText failed: " + ex.Message, ex),
                    "text-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        private Task AttachMediaAsync() =>
            AttachAsync(() => _filePicker.PickChatImageAsync(), SendPickedImageAsync, "image");

        private Task AttachAudioAsync() =>
            AttachAsync(() => _filePicker.PickChatAudioAsync(), SendPickedAudioAsync, "audio");

        /// <summary>
        /// Picks a file of one kind and sends it, with the guards and the failure reporting that
        /// every attachment shares.
        /// </summary>
        /// <remarks>
        /// One picker per kind, rather than the single mixed one this used to open. The old picker
        /// accepted pictures and audio together and then read the extension back off the chosen
        /// file to decide which it had been handed - a guess it only had to make because the user
        /// had not been asked. Now the menu item is the answer.
        /// </remarks>
        private async Task AttachAsync(
            Func<Task<PickedChatMedia>> pick,
            Func<string, PickedChatMedia, Task> send,
            string kind)
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            string targetJid = _activeChat.JID;
            try
            {
                PickedChatMedia picked = await pick();
                if (picked == null || picked.Bytes == null || picked.Bytes.Length == 0)
                {
                    LogSend("attach-cancelled", "kind=" + kind);
                    return;
                }

                await send(targetJid, picked);
            }
            catch (UnisonUserException ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(ex, "attach-fail");
            }
            catch (Exception ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Attach failed: " + ex.Message, ex),
                    "attach-fail");
            }
        }

        private async Task SendPickedAudioAsync(string targetJid, PickedChatMedia picked)
        {
            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("audio-attach-start", "bytes=" + picked.Bytes.Length);
            try
            {
                await _messageService.SendAudioMessageAsync(
                    targetJid,
                    picked.Bytes,
                    picked.MimeType ?? "audio/mp4",
                    durationSeconds: 0,
                    isVoiceMessage: false);
                MessageSent?.Invoke(this, EventArgs.Empty);
                LogSend("audio-attach-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "audio-attach-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new AudioSendException("Attach audio failed: " + ex.Message, ex),
                    "audio-attach-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task SendPickedImageAsync(string targetJid, PickedChatMedia picked)
        {
            if (!picked.IsImage)
            {
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Unsupported attachment type."),
                    "attach-unsupported");
                return;
            }

            string info = string.Format("{0} ({1} KB)", picked.FileName ?? "image", picked.Bytes.Length / 1024);
            bool confirmed = await _dialogs.ShowImageSendPreviewAsync(picked.Bytes, info);
            if (!confirmed || _activeChat == null)
            {
                LogSend("image-attach-cancelled");
                return;
            }

            string caption = string.IsNullOrWhiteSpace(MessageText) ? null : MessageText.Trim();
            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("image-attach-start", "bytes=" + picked.Bytes.Length);
            try
            {
                await _messageService.SendImageAsync(targetJid, picked.Bytes, caption);
                if (caption != null)
                {
                    MessageText = string.Empty;
                }

                MessageSent?.Invoke(this, EventArgs.Empty);
                LogSend("image-attach-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "image-attach-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new ImageSendException("Attach image failed: " + ex.Message, ex),
                    "image-attach-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task StartRecordingAsync()
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            try
            {
                LogSend("record-start");
                _recordingSession = await _audioRecording.StartAsync();
                _recordingChatJid = _activeChat.JID;
                RecordingElapsedText = "0:00";
                IsRecording = true;
                StartElapsedLoop();
            }
            catch (Exception ex)
            {
                LogSendError("record-start-fail", ex);
                await CancelRecordingCoreAsync();
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordFailed",
                    "Could not record audio. Check microphone permission.");
            }
        }

        private async Task StopAndSendRecordingAsync()
        {
            var session = _recordingSession ?? _audioRecording.Current;
            string targetJid = _recordingChatJid ?? _activeChat?.JID;

            StopElapsedLoop();

            if (session == null || !session.IsActive)
            {
                await ClearRecordingUiAsync();
                return;
            }

            AudioRecordingResult recording;
            try
            {
                recording = await session.StopAsync();
            }
            catch (Exception ex)
            {
                LogSendError("record-stop-fail", ex);
                await CancelRecordingCoreAsync();
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordFailed",
                    "Could not record audio. Check microphone permission.");
                return;
            }

            _recordingSession = null;
            _recordingChatJid = null;
            IsRecording = false;
            RecordingElapsedText = "0:00";

            if (string.IsNullOrWhiteSpace(targetJid))
            {
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordingChatUnavailable",
                    "The conversation for this recording is no longer available.");
                return;
            }

            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("voice-send-start", "bytes=" + (recording.Bytes?.Length ?? 0));
            try
            {
                await _messageService.SendAudioMessageAsync(
                    targetJid,
                    recording.Bytes,
                    recording.MimeType ?? "audio/ogg; codecs=opus",
                    recording.DurationSeconds,
                    recording.IsVoiceNote);
                MessageSent?.Invoke(this, EventArgs.Empty);
                LogSend("voice-send-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "voice-send-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new AudioSendException("Voice send failed: " + ex.Message, ex),
                    "voice-send-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        /// <summary>Outer safety net so async command exceptions never tear down the app.</summary>
        private async Task RunSafeAsync(Func<Task> action, string op)
        {
            try
            {
                await action();
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, op + "-unhandled");
            }
            catch (Exception ex)
            {
                LogSendError(op + "-unhandled", ex);
                await ShowSimpleErrorAsync(
                    "ChatDetail_AttachSendFailed",
                    "Could not complete that action.");
            }
        }

        private async Task PresentUserExceptionAsync(UnisonUserException ex, string logEvent)
        {
            if (ex == null)
            {
                return;
            }

            LogSendError(logEvent, ex);
            await ShowSimpleErrorAsync(ex.ResourceKey, ex.FallbackMessage);
        }

        private async Task ShowSimpleErrorAsync(string resourceKey, string fallback)
        {
            if (_dialogs == null)
            {
                return;
            }

            try
            {
                await _dialogs.ShowMessageAsync(
                    _strings != null ? _strings.Get("Toast_AppName", "Unison") : "Unison",
                    _strings != null ? _strings.Get(resourceKey, fallback) : fallback,
                    _strings != null ? _strings.Get("Common_OK", "OK") : "OK");
            }
            catch (Exception dialogEx)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] dialog failed: " + dialogEx.Message);
            }
        }

        private void LogSend(string eventName, string details = null)
        {
            string jid = _activeChat?.JID ?? "?";
            string payload = string.Format(
                "jid={0}{1}",
                jid,
                string.IsNullOrWhiteSpace(details) ? string.Empty : "; " + details);

            try
            {
                _diagnostics?.Write("send", eventName, payload);
            }
            catch
            {
            }

            try
            {
                _sessionLogger?.WriteAlways("[send/" + eventName + "] " + payload);
            }
            catch
            {
            }

            System.Diagnostics.Debug.WriteLine("[send/" + eventName + "] " + payload);
        }

        private void LogSendError(string eventName, Exception ex)
        {
            string detail = ex == null ? null : (ex.GetType().Name + ": " + ex.Message);
            LogSend(eventName, detail);
            try
            {
                _sessionLogger?.WriteErrorAlways("[send/" + eventName + "]", ex);
            }
            catch
            {
            }

            try
            {
                if (ex != null)
                {
                    _diagnostics?.RecordException("send", eventName, ex, "jid=" + (_activeChat?.JID ?? "?"));
                }
            }
            catch
            {
            }
        }

        private async Task CancelRecordingCoreAsync()
        {
            StopElapsedLoop();

            var session = _recordingSession ?? _audioRecording?.Current;
            _recordingSession = null;
            _recordingChatJid = null;

            try
            {
                if (session != null && session.IsActive)
                {
                    await session.CancelAsync();
                }
            }
            catch (Exception ex)
            {
                LogSendError("record-cancel", ex);
            }
            finally
            {
                await ClearRecordingUiAsync();
            }
        }

        private Task ClearRecordingUiAsync()
        {
            return _dispatcher.RunAsync(() =>
            {
                if (_isRecording)
                {
                    IsRecording = false;
                }

                RecordingElapsedText = "0:00";
            });
        }

        private void StartElapsedLoop()
        {
            StopElapsedLoop();
            _elapsedCts = new CancellationTokenSource();
            var token = _elapsedCts.Token;
            var session = _recordingSession;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && session != null && session.IsActive)
                    {
                        string text = FormatElapsed(session.Elapsed);
                        await _dispatcher.RunAsync(() =>
                        {
                            if (_recordingSession == session && _isRecording)
                            {
                                RecordingElapsedText = text;
                            }
                        });
                        await Task.Delay(250, token);
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Elapsed loop: " + ex.Message);
                }
            }, token);
        }

        private void StopElapsedLoop()
        {
            try
            {
                _elapsedCts?.Cancel();
            }
            catch
            {
            }

            _elapsedCts?.Dispose();
            _elapsedCts = null;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
            {
                return string.Format(
                    "{0}:{1:D2}:{2:D2}",
                    (int)elapsed.TotalHours,
                    elapsed.Minutes,
                    elapsed.Seconds);
            }

            return string.Format("{0}:{1:D2}", (int)elapsed.TotalMinutes, elapsed.Seconds);
        }

        private void AppendLiveMessages()
        {
            if (_activeChat == null) return;

            var live = _whatsAppService.GetLiveMessages(_activeChat.JID);
            if (live == null || live.Count == 0) return;

            bool hasRealLive = live.Any(m =>
                m != null &&
                !string.IsNullOrEmpty(m.Id) &&
                !m.IsPreviewFallback &&
                !ChatPreviewMessageFactory.IsPreviewFallbackId(m.Id));
            if (!hasRealLive)
            {
                return;
            }

            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var existing = Messages[i];
                if (existing?.Model != null &&
                    (existing.Model.IsPreviewFallback ||
                     ChatPreviewMessageFactory.IsPreviewFallbackId(existing.Model.Id)))
                {
                    Messages.RemoveAt(i);
                }
            }

            var existingIds = new HashSet<string>(
                Messages.Where(m => m?.Id != null).Select(m => m.Id),
                StringComparer.Ordinal);

            foreach (var msg in live)
            {
                if (msg == null || string.IsNullOrEmpty(msg.Id)) continue;
                if (msg.IsPreviewFallback || ChatPreviewMessageFactory.IsPreviewFallbackId(msg.Id)) continue;
                if (existingIds.Contains(msg.Id)) continue;
                Messages.Add(_messageFactory.Create(msg));
                existingIds.Add(msg.Id);
            }
        }
    }
}
