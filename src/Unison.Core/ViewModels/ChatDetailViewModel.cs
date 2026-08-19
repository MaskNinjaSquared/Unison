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
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Active conversation: composer, send text/media, mic session + overlay, load-more / history,
    /// presence watch, and group timeline run layout (author avatars resolved before the bubble binds).
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

        /// <summary>SQLite person cache (group bubble avatars when 1:1 / roster miss).</summary>
        private readonly IPersonStore _personStore;

        /// <summary>Device People card for contacts not yet in the address book.</summary>
        private readonly IContactService _contactService;

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
        private CancellationTokenSource _presenceCts;
        private bool _attached;
        private bool _presenceHandlerAttached;
        private bool _presenceReceived;
        private string _pendingPresenceText;
        private DateTime _presenceOpenedUtc;
        private bool _hasReachedStart;
        private int _emptyLoadAttempts;

        /// <summary>How many bubble VMs to materialize when opening a chat (service may hold more data).</summary>
        public const int InitialUiMessageWindow = 50;

        /// <summary>Hard cap on timeline VMs while the chat is open (trim after live sync / load-more).</summary>
        public const int MaxUiMessageWindow = 150;

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
            IPersonStore personStore = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            IChatService chatService = null,
            IContactService contactService = null)
        {
            _chatService = chatService;
            _whatsAppService = whatsAppService;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _audioRecording = audioRecording;
            _filePicker = filePicker;
            _dialogs = dialogs;
            _dispatcher = dispatcher;
            _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
            _infoFactory = infoFactory ?? throw new ArgumentNullException(nameof(infoFactory));
            _strings = strings;
            _personStore = personStore;
            _contactService = contactService;
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
            AddContactCommand = new RelayCommand(
                () => _ = AddContactAsync(),
                () => CanAddToAddressBook);
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
            ClearTimeline();
            if (_contactService != null)
            {
                _contactService.DisplayNamesUpdated -= Contacts_DisplayNamesUpdated;
            }

            _attached = false;
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            if (_contactService != null)
            {
                _contactService.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
            }

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
                vm.AttachMentionLookup(ActiveChat?.MentionLookup);
            }

            return vm;
        }

        /// <summary>Clears the timeline and detaches each bubble so models do not keep VMs alive.</summary>
        public void ClearTimeline()
        {
            for (int i = 0; i < Messages.Count; i++)
            {
                ReleaseMessageVm(Messages[i]);
            }

            Messages.Clear();
        }

        /// <summary>Removes one timeline row after <see cref="ChatMessageViewModel.Detach"/>.</summary>
        public void RemoveTimelineAt(int index)
        {
            if (index < 0 || index >= Messages.Count)
            {
                return;
            }

            ReleaseMessageVm(Messages[index]);
            Messages.RemoveAt(index);
        }

        /// <summary>Drops oldest rows until <paramref name="maxCount"/> remain (live append / stick-to-bottom).</summary>
        public void TrimTimelineToMax(int maxCount)
        {
            if (maxCount < 0)
            {
                maxCount = 0;
            }

            while (Messages.Count > maxCount)
            {
                RemoveTimelineAt(0);
            }
        }

        /// <summary>Drops newest rows until <paramref name="maxCount"/> remain (user scrolled up loading older).</summary>
        public void TrimTimelineNewestToMax(int maxCount)
        {
            if (maxCount < 0)
            {
                maxCount = 0;
            }

            while (Messages.Count > maxCount)
            {
                RemoveTimelineAt(Messages.Count - 1);
            }
        }

        /// <summary>Clears top-scroll paging flags when switching conversations.</summary>
        public void ResetTimelinePaging()
        {
            _hasReachedStart = false;
            _emptyLoadAttempts = 0;
            OnPropertyChanged(nameof(CanLoadMore));
        }

        /// <summary>
        /// Replaces the UI window with factory-made VMs for <paramref name="messages"/> (already sliced by the caller).
        /// </summary>
        public void ReplaceTimelineWindow(IEnumerable<ChatMessage> messages)
        {
            ClearTimeline();
            if (messages == null || _messageFactory == null)
            {
                return;
            }

            foreach (var msg in messages)
            {
                if (msg == null)
                {
                    continue;
                }

                var vm = CreateMessageVm(msg);
                if (vm != null)
                {
                    Messages.Add(vm);
                }
            }
        }

        private void ReleaseMessageVm(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            vm.PinnedChanged -= OnBubblePinnedChanged;
            vm.Detach();
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
        /// Storyboards remain in the view. For groups the view shows member names last, then loops.
        /// </summary>
        public event EventHandler<string> PresenceAnimationRequested;

        /// <summary>
        /// Comma-separated group member display names (A–Z). Null/empty when unknown.
        /// </summary>
        public string FormatGroupMemberNamesSummary(ChatItem group)
        {
            if (group?.GroupMembers == null || group.GroupMembers.Count == 0)
            {
                return null;
            }

            var names = group.GroupMembers
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.DisplayName))
                .Select(m => m.DisplayName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                return null;
            }

            string joined = string.Join(", ", names);
            const int maxLen = 140;
            if (joined.Length <= maxLen)
            {
                return joined;
            }

            return joined.Substring(0, maxLen - 1).TrimEnd(',', ' ') + "…";
        }

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
                OnPropertyChanged(nameof(CanLoadMore));
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
            private set
            {
                if (Set(ref _isLoadingMessages, value))
                {
                    OnPropertyChanged(nameof(CanLoadMore));
                }
            }
        }

        public bool IsLoadingMore
        {
            get => _isLoadingMore;
            private set
            {
                if (Set(ref _isLoadingMore, value))
                {
                    OnPropertyChanged(nameof(CanLoadMore));
                }
            }
        }

        /// <summary>
        /// Whether the view may request older messages (scroll near top).
        /// Service/SQLite keep data; this only gates materializing more bubble VMs.
        /// </summary>
        public bool CanLoadMore =>
            HasActiveChat &&
            !IsLoadingMore &&
            !IsLoadingMessages &&
            !_hasReachedStart;

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

        public ICommand AddContactCommand { get; }

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

        public bool CanAddToAddressBook =>
            _contactService != null &&
            ActiveChat != null &&
            !ActiveChat.IsGroup &&
            !ActiveChat.IsPersonal &&
            _contactService.CanAddToAddressBook(ActiveChat.JID);

        public string AddContactLabel =>
            _strings != null
                ? _strings.Get("ChatDetail_AddContact.Text", "Add contact")
                : "Add contact";

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
            RaiseCanAddToAddressBook();
        }

        private void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(RaiseCanAddToAddressBook);
                return;
            }

            RaiseCanAddToAddressBook();
        }

        private void RaiseCanAddToAddressBook()
        {
            OnPropertyChanged(nameof(CanAddToAddressBook));
            OnPropertyChanged(nameof(AddContactLabel));
            (AddContactCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task AddContactAsync()
        {
            ChatItem chat = ActiveChat;
            if (_contactService == null || chat == null || !CanAddToAddressBook)
            {
                return;
            }

            try
            {
                await _contactService.ShowAddToAddressBookAsync(
                    chat.GetNameResolved(_strings),
                    _contactService.TryResolvePhone(chat.JID),
                    chat.GetAvatarUrl(preferHigh: true),
                    chat.JID);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Add contact failed: " + ex.Message);
            }

            RaiseCanAddToAddressBook();
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

        /// <summary>Opens the group-member profile pane (media/files filtered to that author).</summary>
        public void OpenGroupMemberInfo(GroupMember member, string participantJid = null, string nameHint = null)
        {
            if (member == null || ActiveChat == null || _infoFactory == null)
            {
                return;
            }

            GroupParticipantResolver.EnrichMember(
                member,
                participantJid ?? member.Jid ?? member.Lid ?? member.PhoneNumber,
                ActiveChat,
                _whatsAppService,
                _personStore,
                nameHint);

            ChatDetailInfoViewModel previous = ChatDetailInfo;
            ChatDetailInfo = _infoFactory.CreateGroupMember(ActiveChat, member);
            IsChatDetailInfoOpen = true;
            previous?.Detach();
        }

        /// <summary>
        /// Resolves a timeline participant to a <see cref="GroupMember"/> (roster or ephemeral) and opens info.
        /// </summary>
        public void OpenGroupMemberInfoByJid(string participantJid, string nameHint = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid) || ActiveChat == null)
            {
                return;
            }

            GroupMember member = FindGroupMember(participantJid);
            if (member == null)
            {
                member = new GroupMember
                {
                    Jid = JidHelper.Normalize(participantJid)
                };
            }

            OpenGroupMemberInfo(member, participantJid, nameHint);
        }

        /// <summary>
        /// Opens a group member from a quote header when only the display name is known.
        /// </summary>
        public void OpenGroupMemberInfoByDisplayName(string displayName)
        {
            if (ActiveChat == null || !ActiveChat.IsGroup || string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            GroupMember member = FindGroupMemberByDisplayName(displayName.Trim());
            if (member == null)
            {
                return;
            }

            OpenGroupMemberInfo(member, member.Jid ?? member.Lid ?? member.PhoneNumber, displayName.Trim());
        }

        /// <summary>
        /// Quote author strip: JID first, else roster match on the visible name.
        /// </summary>
        public void OpenQuotedAuthor(string participantJid, string senderName)
        {
            if (ActiveChat == null || !ActiveChat.IsGroup)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(participantJid))
            {
                OpenGroupMemberInfoByJid(participantJid, senderName);
                return;
            }

            OpenGroupMemberInfoByDisplayName(senderName);
        }

        private GroupMember FindGroupMember(string participantJid)
        {
            if (ActiveChat?.GroupMembers == null || string.IsNullOrWhiteSpace(participantJid))
            {
                return null;
            }

            string canonical = _whatsAppService != null
                ? _whatsAppService.GetCanonicalJid(participantJid)
                : JidHelper.Normalize(participantJid);

            foreach (var member in ActiveChat.GroupMembers)
            {
                if (member == null)
                {
                    continue;
                }

                if (JidsMatchCanonical(member.Jid, canonical) ||
                    JidsMatchCanonical(member.PhoneNumber, canonical) ||
                    JidsMatchCanonical(member.Lid, canonical))
                {
                    return member;
                }
            }

            return null;
        }

        private GroupMember FindGroupMemberByDisplayName(string displayName)
        {
            if (ActiveChat?.GroupMembers == null || string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            string needle = displayName.Trim();
            GroupMember partial = null;
            for (int i = 0; i < ActiveChat.GroupMembers.Count; i++)
            {
                GroupMember member = ActiveChat.GroupMembers[i];
                if (member == null || string.IsNullOrWhiteSpace(member.DisplayName))
                {
                    continue;
                }

                if (string.Equals(member.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }

                if (partial == null &&
                    member.DisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = member;
                }
            }

            return partial;
        }

        private bool JidsMatchCanonical(string jid, string canonical)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(canonical))
            {
                return false;
            }

            string other = _whatsAppService != null
                ? _whatsAppService.GetCanonicalJid(jid)
                : JidHelper.Normalize(jid);
            if (string.IsNullOrWhiteSpace(other))
            {
                other = JidHelper.Normalize(jid);
            }

            return string.Equals(other, canonical, StringComparison.OrdinalIgnoreCase);
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
                ResetTimelinePaging();
            }

            RaisePinToStartCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
            (OpenChatDetailInfoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChatDetailInfoFromAvatarCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(LiveTilePinMenuLabel));
            OnPropertyChanged(nameof(ShowMuteDurationOptions));
            OnPropertyChanged(nameof(ShowUnmuteOption));
            OnPropertyChanged(nameof(IsGroupLockedForMessages));
            RaiseCanAddToAddressBook();
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

        /// <summary>
        /// Finds a loaded bubble VM by protocol message id.
        /// </summary>
        public ChatMessageViewModel FindMessageById(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessageViewModel candidate = Messages[i];
                if (candidate != null && string.Equals(candidate.Id, messageId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads older timeline pages until <paramref name="quotedMessageId"/> appears or history ends.
        /// </summary>
        public async Task<ChatMessageViewModel> NavigateToQuotedMessageAsync(string quotedMessageId, int maxLoadAttempts = 24)
        {
            ChatMessageViewModel target = FindMessageById(quotedMessageId);
            if (target != null)
            {
                return target;
            }

            if (_activeChat == null || string.IsNullOrWhiteSpace(quotedMessageId))
            {
                return null;
            }

            int attempts = 0;
            while (CanLoadMore && attempts < maxLoadAttempts)
            {
                attempts++;
                ChatTimelineLoadMoreResult result = await LoadMoreMessagesAsync().ConfigureAwait(false);
                if (_activeChat == null)
                {
                    return null;
                }

                target = FindMessageById(quotedMessageId);
                if (target != null)
                {
                    return target;
                }

                if (result == null ||
                    result.ReachedStart ||
                    (result.PrependedCount == 0 && !result.WaitingForOnDemand))
                {
                    break;
                }
            }

            return FindMessageById(quotedMessageId);
        }

        /// <summary>
        /// Prepends an older page into the UI window (factory VMs). View calls this when scroll is near top
        /// and <see cref="CanLoadMore"/> is true; scroll offset adjustment stays in code-behind.
        /// </summary>
        public async Task<ChatTimelineLoadMoreResult> LoadMoreMessagesAsync()
        {
            var result = new ChatTimelineLoadMoreResult();
            if (!CanLoadMore || _activeChat == null)
            {
                result.ReachedStart = _hasReachedStart;
                return result;
            }

            IsLoadingMore = true;
            try
            {
                string chatJid = _activeChat.JID;
                ChatMessageViewModel oldestVm = Messages.Count > 0 ? Messages[0] : null;
                var older = await _messageService.LoadMoreMessagesAsync(
                    chatJid,
                    oldestVm?.Timestamp,
                    oldestVm?.Id);

                if (_activeChat == null ||
                    !string.Equals(_activeChat.JID, chatJid, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }

                if (older != null && older.Count > 0)
                {
                    _emptyLoadAttempts = 0;
                    RemovePreviewFallbackMessages();
                    StampGroupRemoteJid(older, chatJid);

                    var existingIds = new HashSet<string>(
                        Messages.Where(m => m?.Id != null).Select(m => m.Id),
                        StringComparer.Ordinal);

                    int prepended = 0;
                    for (int i = 0; i < older.Count; i++)
                    {
                        var msg = older[i];
                        if (msg == null)
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(msg.Id) && existingIds.Contains(msg.Id))
                        {
                            continue;
                        }

                        InsertTimelineMessage(msg);
                        if (!string.IsNullOrEmpty(msg.Id))
                        {
                            existingIds.Add(msg.Id);
                        }

                        prepended++;
                    }

                    TrimTimelineNewestToMax(MaxUiMessageWindow);
                    result.PrependedCount = prepended;
                    OnPropertyChanged(nameof(CanLoadMore));
                    return result;
                }

                bool requestedOnDemand =
                    await _messageService.EnsureHistoryOnDemandAsync(chatJid, InitialUiMessageWindow);
                bool pendingOnDemand = _messageService.IsHistoryOnDemandPending(chatJid);

                if (requestedOnDemand || pendingOnDemand)
                {
                    _emptyLoadAttempts = 0;
                    _hasReachedStart = false;
                    result.WaitingForOnDemand = true;
                }
                else
                {
                    _emptyLoadAttempts++;
                    _hasReachedStart = _emptyLoadAttempts >= 2;
                    result.ReachedStart = _hasReachedStart;
                }

                OnPropertyChanged(nameof(CanLoadMore));
                return result;
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        /// <summary>Newest <paramref name="windowSize"/> rows of a loaded page, in order.</summary>
        public static List<ChatMessage> TakeLastWindow(IList<ChatMessage> source, int windowSize)
        {
            var list = new List<ChatMessage>();
            if (source == null || source.Count == 0 || windowSize <= 0)
            {
                return list;
            }

            int start = Math.Max(0, source.Count - windowSize);
            for (int i = start; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    list.Add(source[i]);
                }
            }

            return list;
        }

        /// <summary>
        /// Older persisted rows can arrive without the group JID, which is what decides whether a
        /// bubble shows its sender name. Stamps it so group runs render correctly.
        /// </summary>
        public void StampGroupRemoteJid(IList<ChatMessage> messages, string chatJid)
        {
            if (messages == null || string.IsNullOrEmpty(chatJid))
            {
                return;
            }

            bool isGroup = LooksLikeGroupChat(_activeChat) ||
                chatJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            if (!isGroup)
            {
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(msg.RemoteJid) ||
                    !msg.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    msg.RemoteJid = chatJid;
                }
            }
        }

        /// <summary>Drops the ephemeral preview bubbles once real rows are available.</summary>
        public void RemovePreviewFallbackMessages()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var model = Messages[i]?.Model;
                if (model == null)
                {
                    continue;
                }

                if (model.IsPreviewFallback || ChatPreviewMessageFactory.IsPreviewFallbackId(model.Id))
                {
                    RemoveTimelineAt(i);
                }
            }
        }

        /// <summary>
        /// Empty timeline while the chat list already shows a preview: surfaces that preview as an
        /// ephemeral bubble and asks the phone for history. Returns the model so the view can run
        /// its group-run layout over it, or null when the chat had nothing to show.
        /// </summary>
        public ChatMessage ApplyPreviewFallback(ChatItem chat, string requestedJid, bool isGroup)
        {
            if (chat == null)
            {
                return null;
            }

            string targetJid = string.IsNullOrWhiteSpace(requestedJid) ? chat.JID : requestedJid;
            string selfName = _strings?.Get("Chat_SelfFallbackName", "You") ?? "You";
            ChatMessage fallback = ChatPreviewMessageFactory.TryCreate(chat, selfName);
            if (fallback != null)
            {
                if (isGroup &&
                    (string.IsNullOrEmpty(fallback.RemoteJid) ||
                     !fallback.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)))
                {
                    fallback.RemoteJid = targetJid;
                }

                var vm = CreateMessageVm(fallback);
                if (vm != null)
                {
                    Messages.Add(vm);
                }
            }

            // Ask for history either way — an empty timeline is the reason we are here.
            _ = _messageService.EnsureHistoryOnDemandAsync(targetJid, InitialUiMessageWindow);
            return fallback;
        }

        /// <summary>
        /// Merges a freshly loaded page into the open timeline: strips preview bubbles, refreshes
        /// rows we already show, inserts what is new in order, then trims to the UI window.
        /// Returns true when the collection changed, so the view knows to redo runs / scroll.
        /// </summary>
        public bool MergeTimelineFromService(IList<ChatMessage> serviceMessages, string requestedJid)
        {
            if (serviceMessages == null || serviceMessages.Count == 0)
            {
                return false;
            }

            int countBeforeFallbackStrip = Messages.Count;
            RemovePreviewFallbackMessages();
            bool changed = Messages.Count != countBeforeFallbackStrip;

            var existingIds = new HashSet<string>(
                Messages.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id),
                StringComparer.Ordinal);

            for (int i = 0; i < serviceMessages.Count; i++)
            {
                ChatMessage msg = serviceMessages[i];
                if (msg == null)
                {
                    continue;
                }

                ChatMessageViewModel existing = FindTimelineRow(msg, existingIds);
                if (existing != null)
                {
                    ApplyLiveFieldsTo(existing.Model, msg);
                    continue;
                }

                StampGroupRemoteJid(new[] { msg }, requestedJid);
                InsertTimelineMessage(msg);
                if (!string.IsNullOrWhiteSpace(msg.Id))
                {
                    existingIds.Add(msg.Id);
                }

                changed = true;
            }

            TrimTimelineToMax(MaxUiMessageWindow);
            return changed;
        }

        /// <summary>
        /// Row already on screen for <paramref name="message"/>. Rows without an id (local echo)
        /// are matched on timestamp + direction + text, which is all they have.
        /// </summary>
        private ChatMessageViewModel FindTimelineRow(ChatMessage message, HashSet<string> existingIds)
        {
            if (!string.IsNullOrWhiteSpace(message.Id))
            {
                return existingIds.Contains(message.Id)
                    ? Messages.FirstOrDefault(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                    : null;
            }

            return Messages.FirstOrDefault(m =>
                m?.Model != null &&
                string.IsNullOrWhiteSpace(m.Id) &&
                m.Timestamp == message.Timestamp &&
                m.IsFromMe == message.IsFromMe &&
                string.Equals(m.Model.Content, message.Content, StringComparison.Ordinal));
        }

        /// <summary>Copies the fields a reload can carry forward onto a row already on screen.</summary>
        private static void ApplyLiveFieldsTo(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Status = source.Status;
            target.IsRevoked = source.IsRevoked;
            target.IsPinned = source.IsPinned;
            target.PinnedAtUtc = source.PinnedAtUtc;
            target.PinExpiresAtUtc = source.PinExpiresAtUtc;
            target.RemoteJid = source.RemoteJid;
            target.ParticipantJid = source.ParticipantJid;
            target.Reactions = source.Reactions;
            if (source.IsRevoked)
            {
                target.Content = source.Content;
                target.Kind = source.Kind;
            }

            if (!string.IsNullOrWhiteSpace(source.QuotedMessageId) ||
                !string.IsNullOrWhiteSpace(source.QuotedText) ||
                source.QuotedKind != ChatPreviewKind.Text)
            {
                target.QuotedMessageId = source.QuotedMessageId;
                target.QuotedText = source.QuotedText;
                target.QuotedKind = source.QuotedKind;
                target.QuotedSenderName = source.QuotedSenderName;
                target.QuotedParticipantJid = source.QuotedParticipantJid;
            }
            if (!string.IsNullOrWhiteSpace(source.ImageUri))
            {
                target.ImageUri = source.ImageUri;
            }

            if (!string.IsNullOrWhiteSpace(source.AudioUri))
            {
                target.AudioUri = source.AudioUri;
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

        /// <summary>Inserts one row at its chronological position (live append / load-more).</summary>
        public void InsertTimelineMessage(ChatMessage message)
        {
            if (message == null || _messageFactory == null)
            {
                return;
            }

            var vm = CreateMessageVm(message);
            if (vm == null)
            {
                return;
            }

            int index = ChatMessageOrder.FindInsertIndex(
                Messages.Count,
                i => Messages[i]?.Timestamp ?? DateTime.MinValue,
                i => Messages[i]?.Id,
                message.Timestamp,
                message.Id);
            Messages.Insert(index, vm);
        }

        /// <summary>
        /// One pass over the visible timeline: run chrome, date chips, sender labels, and group
        /// author avatars. The bubble only binds the resolved <see cref="ChatMessage.ContactUri"/>.
        /// </summary>
        public void ApplyMessageRunLayout(IList<ChatMessage> messages, bool isGroup, ChatItem groupChat)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            string todayLabel = _strings != null ? _strings.Get("Common_Today", "Today") : "Today";
            string yesterdayLabel = _strings != null
                ? _strings.Get("Common_Yesterday", "Yesterday")
                : "Yesterday";
            DateTime? previousLocalDate = null;

            for (int i = 0; i < messages.Count; i++)
            {
                var current = messages[i];
                if (current == null)
                {
                    continue;
                }

                DateTime localDate = WhatsAppMapper.ToLocalCalendarDate(current.Timestamp);
                bool isFirstOfDay = localDate != DateTime.MinValue &&
                    (!previousLocalDate.HasValue || localDate != previousLocalDate.Value);
                current.IsFirstOfDay = isFirstOfDay;
                current.DateSeparatorText = isFirstOfDay
                    ? WhatsAppMapper.FormatDaySeparator(current.Timestamp, todayLabel, yesterdayLabel)
                    : string.Empty;
                if (localDate != DateTime.MinValue)
                {
                    previousLocalDate = localDate;
                }

                if (isGroup)
                {
                    EnsureGroupSenderName(current, groupChat);
                    EnsureQuotedSenderName(current, groupChat);
                }

                bool isRunStart = i == 0;
                bool isRunEnd = i == messages.Count - 1;

                if (!isRunStart)
                {
                    isRunStart = !IsSameMessageRun(messages[i - 1], current);
                }

                if (!isRunEnd)
                {
                    isRunEnd = !IsSameMessageRun(current, messages[i + 1]);
                }

                current.IsRunStart = isRunStart;
                current.IsRunEnd = isRunEnd;
                current.ShowGroupSenderName =
                    isGroup &&
                    isRunStart &&
                    !current.IsFromMe &&
                    !string.IsNullOrWhiteSpace(current.SenderName) &&
                    !string.Equals(current.SenderName, "Me", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(current.SenderName, "You", StringComparison.OrdinalIgnoreCase);

                bool contactSlot = isGroup && !current.IsFromMe;
                current.ShowContactSlot = contactSlot;
                current.ShowContact = contactSlot && isRunStart;
                current.ContactUri = contactSlot
                    ? ResolveParticipantContactUri(current.ParticipantJid, groupChat)
                    : null;

                current.ShowQuotedAuthorLink =
                    isGroup &&
                    current.HasQuote &&
                    (!string.IsNullOrWhiteSpace(current.QuotedParticipantJid) ||
                     !string.IsNullOrWhiteSpace(current.QuotedSenderName));
            }

            System.Collections.Generic.IReadOnlyDictionary<string, string> lookup =
                isGroup ? groupChat?.MentionLookup : null;
            for (int i = 0; i < Messages.Count; i++)
            {
                Messages[i]?.RefreshMentions(lookup);
            }
        }

        /// <summary>
        /// Relabels date chips after local midnight (Hoje / Ontem / date). Call from the view timer.
        /// </summary>
        public void RefreshDateSeparators()
        {
            if (Messages.Count == 0)
            {
                return;
            }

            var models = new List<ChatMessage>(Messages.Count);
            for (int i = 0; i < Messages.Count; i++)
            {
                models.Add(Messages[i]?.Model);
            }

            ApplyMessageRunLayout(models, ActiveChat?.IsGroup ?? false, ActiveChat);
        }

        /// <summary>
        /// Group author photo: roster → 1:1 chat → Person (same chain as member info).
        /// </summary>
        public string ResolveParticipantContactUri(string participantJid, ChatItem groupChat)
        {
            GroupMember roster = FindGroupMember(participantJid);
            return GroupParticipantResolver.ResolveAvatar(
                participantJid,
                groupChat,
                _whatsAppService,
                _personStore,
                roster);
        }

        private void EnsureGroupSenderName(ChatMessage message, ChatItem groupChat)
        {
            if (message == null || message.IsFromMe)
            {
                return;
            }

            string participant = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return;
            }

            GroupMember roster = FindGroupMember(participant);
            string resolved = GroupParticipantResolver.ResolveDisplayName(
                participant,
                groupChat,
                _whatsAppService,
                _personStore,
                message.SenderName,
                roster);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                message.SenderName = resolved;
            }
        }

        private void EnsureQuotedSenderName(ChatMessage message, ChatItem groupChat)
        {
            if (message == null || !message.HasQuote)
            {
                return;
            }

            string participant = message.QuotedParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return;
            }

            GroupMember roster = FindGroupMember(participant);
            string resolved = GroupParticipantResolver.ResolveDisplayName(
                participant,
                groupChat,
                _whatsAppService,
                _personStore,
                message.QuotedSenderName,
                roster);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                message.QuotedSenderName = resolved;
            }
        }

        private static bool IsSameMessageRun(ChatMessage left, ChatMessage right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.IsFromMe != right.IsFromMe)
            {
                return false;
            }

            if (left.IsFromMe)
            {
                return true;
            }

            string leftParticipant = left.ParticipantJid ?? string.Empty;
            string rightParticipant = right.ParticipantJid ?? string.Empty;
            if (!string.IsNullOrEmpty(leftParticipant) && !string.IsNullOrEmpty(rightParticipant))
            {
                return string.Equals(leftParticipant, rightParticipant, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left.SenderName ?? string.Empty, right.SenderName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
