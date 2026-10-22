using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public partial class ChatDetailViewModel : Observable
    {
        // ── DI ────────────────────────────────────────────────────────────────

        /// <summary>Whether presence may be subscribed yet.</summary>
        private readonly IConnectionService _connectionService;

        /// <summary>Who may write in this group, and who is in it.</summary>
        private readonly IGroupService _groupService;

        /// <summary>
        /// The chat list. Message reads still go through the message facade, because finding a
        /// chat's messages needs the canonical-JID mapping the store knows nothing about.
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
        private readonly IUiSoundService _uiSounds;

        /// <summary>File pickers — chat attach uses PickChatAttachmentAsync.</summary>
        private readonly IFilePicker _filePicker;

        /// <summary>Confirm dialogs (delete chat, call phone, …).</summary>
        private readonly IDialogService _dialogs;

        /// <summary>Opens system URIs (tel:, http, …).</summary>
        private readonly IUriLauncher _uriLauncher;

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

        /// <summary>Device info (kept for other Mobile/desktop branching in this VM).</summary>
        private readonly ISystemInfoProvider _systemInfo;

        /// <summary>Debug session log (visible on Debug screen).</summary>
        private readonly ISessionLogger _sessionLogger;

        /// <summary>Runtime diagnostics journal (Debug screen).</summary>
        private readonly IRuntimeDiagnostics _diagnostics;

        /// <summary>Keeps the chat-list author strip in step with the names resolved here.</summary>
        private readonly IChatAuthorProjection _authorProjection;

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

        /// <summary>
        /// Per open-chat lookup of group participant labels/avatars (plus 1:1 indexes).
        /// Owned for the VM lifetime; roster hooks detach on uninitialize without Dispose.
        /// </summary>
        private readonly IGroupParticipantLookup _participants;

        /// <summary>Which JIDs are the same person (PN vs LID).</summary>
        private readonly IJidResolver _jids;

        /// <summary>How many bubble VMs to materialize when opening a chat (service may hold more data).</summary>
        public int InitialUiMessageWindow { get; }

        /// <summary>Hard cap on timeline VMs while the chat is open (trim after live sync / load-more).</summary>
        public int MaxUiMessageWindow { get; }

        public ChatDetailViewModel(
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
            IJidResolver jids,
            IPersonStore personStore = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            IChatService chatService = null,
            IContactService contactService = null,
            ISystemInfoProvider systemInfo = null,
            IUriLauncher uriLauncher = null,
            IConnectionService connectionService = null,
            IGroupService groupService = null,
            IChatAuthorProjection authorProjection = null,
            IUiSoundService uiSounds = null)
        {
            _chatService = chatService;
            _connectionService = connectionService;
            _groupService = groupService;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _audioRecording = audioRecording;
            _uiSounds = uiSounds;
            _filePicker = filePicker;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _dispatcher = dispatcher;
            _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
            _infoFactory = infoFactory ?? throw new ArgumentNullException(nameof(infoFactory));
            _strings = strings;
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _personStore = personStore;
            _contactService = contactService;
            _systemInfo = systemInfo;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;
            _authorProjection = authorProjection;

            // Open-chat bubble budget follows memory tier (not DeviceFamily): Unsupported/Low keep
            // the old Mobile 30/80 window; High keeps the desktop 50/150.
            SyncWorkloadStrategy uiWindow = SyncWorkloadStrategy.For(
                _systemInfo != null ? _systemInfo.GetDeviceCategory() : DeviceCategory.High);
            InitialUiMessageWindow = uiWindow.InitialUiMessageWindow;
            MaxUiMessageWindow = uiWindow.MaxUiMessageWindow;

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

            // Contact / location stay unavailable until each has a send route.
            // Camera opens the in-chat CameraPickView; document attach is live.
            AttachCameraCommand = new RelayCommand(
                () => CameraPickRequested?.Invoke(this, EventArgs.Empty),
                () => CanCompose);
            AttachFileCommand = new RelayCommand(
                () => _ = RunSafeAsync(AttachDocumentAsync, "attach-file"),
                () => CanCompose);
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
                () => AreLiveTilesAvailable &&
                      ActiveChat != null &&
                      !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                      _shortcutService != null &&
                      _chatStore != null);
            DeleteChatCommand = new RelayCommand(
                () => _ = DeleteActiveChatAsync(),
                () => CanDeleteActiveChat());
            ArchiveChatCommand = new RelayCommand(
                () => { },
                () => ActiveChat != null &&
                      !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                      ActiveChat.Status == ChatStatus.Active);
            UnarchiveChatCommand = new RelayCommand(
                () => { },
                () => ActiveChat != null &&
                      !string.IsNullOrWhiteSpace(ActiveChat.JID) &&
                      ActiveChat.Status == ChatStatus.Archived);
            _messageActions.PropertyChanged += OnMessageActionsChanged;
            CopySelectedMessageCommand = new RelayCommand(
                () => SelectedMessage?.CopyCommand.Execute(null),
                () => _messageActions.CanCopy);
            ReplySelectedMessageCommand = new RelayCommand(
                () => BeginReply(SelectedMessage),
                () => _messageActions.CanReply);
            DismissReplyCommand = new RelayCommand(DismissReply);
            PinSelectedFor24HoursCommand = new RelayCommand(
                () => _ = PinSelectedAsync(true, MessageActionSelection.PinFor24HoursSeconds),
                () => _messageActions.CanPin);
            PinSelectedFor7DaysCommand = new RelayCommand(
                () => _ = PinSelectedAsync(true, MessageActionSelection.PinFor7DaysSeconds),
                () => _messageActions.CanPin);
            PinSelectedFor30DaysCommand = new RelayCommand(
                () => _ = PinSelectedAsync(true, MessageActionSelection.PinFor30DaysSeconds),
                () => _messageActions.CanPin);
            UnpinSelectedMessageCommand = new RelayCommand(
                () => _ = PinSelectedAsync(false, 0),
                () => _messageActions.CanUnpin);
            MuteFor8HoursCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(_chatService.MuteUntilEightHoursFromNow()),
                () => CanMuteActiveChat());
            MuteFor1WeekCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(_chatService.MuteUntilOneWeekFromNow()),
                () => CanMuteActiveChat());
            MuteForeverCommand = new RelayCommand(
                () => _ = SetLocalMuteAsync(_chatService.MuteForeverUnixSeconds),
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

            CallPhoneCommand = new RelayCommand(
                () => _ = CallPhoneAsync(),
                () => CanCallPhone);

            _participants = _groupService != null
                ? _groupService.CreateOpenChatParticipantLookup(SelfListDisplayName)
                : new GroupParticipantLookup(
                    new ParticipantResolutionContext(jids, contactService, chatState, personStore),
                    SelfListDisplayName);
            _participants.ParticipantAvatarChanged += OnParticipantAvatarChanged;
        }


        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler BackRequested;
        public event EventHandler MessageSent;

        /// <summary>Chat detail should show <c>CameraPickView</c> over the conversation.</summary>
        public event EventHandler CameraPickRequested;

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
                CloseMentionMenu();
                DismissMessageActions();
                DismissReply();
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
                if (_composerHeld)
                {
                    Set(ref _messageText, value);
                    RaiseSendMessageCanExecuteChanged();
                    return;
                }

                string shown = _mentions.SetText(value);
                Set(ref _messageText, shown);
                if (!string.Equals(shown, value, StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(MessageText));
                }

                RaiseSendMessageCanExecuteChanged();
            }
        }

        private readonly ComposerMentionDraft _mentions = new ComposerMentionDraft();
        private bool _composerHeld;
        private int _mentionTokenStart;
        private int _mentionTokenLength;
        private int _mentionTokenCaret;

        /// <summary>Rows for the @ menu. Everyone stays first. Empty when the menu is closed.</summary>
        public ObservableCollection<MentionChoice> MentionChoices { get; } = new ObservableCollection<MentionChoice>();

        public bool IsMentionMenuOpen
        {
            get => _mentionMenuOpen;
            private set => Set(ref _mentionMenuOpen, value);
        }

        private readonly MessageActionSelection _messageActions = new MessageActionSelection();

        /// <summary>The bubble the Legacy command bar is acting on. Null when the bar is closed.</summary>
        public ChatMessageViewModel SelectedMessage => _messageActions.Message;

        public bool IsMessageActionsOpen => _messageActions.IsOpen;

        public bool CanPinSelectedMessage => _messageActions.CanPin;

        public bool CanUnpinSelectedMessage => _messageActions.CanUnpin;

        public ICommand CopySelectedMessageCommand { get; }

        public ICommand ReplySelectedMessageCommand { get; }

        public ICommand PinSelectedFor24HoursCommand { get; }

        public ICommand PinSelectedFor7DaysCommand { get; }

        public ICommand PinSelectedFor30DaysCommand { get; }

        public ICommand UnpinSelectedMessageCommand { get; }

        /// <summary>
        /// Opens the Legacy action bar on this bubble. The same bubble again closes it.
        /// </summary>
        public void SelectMessage(ChatMessageViewModel message)
        {
            _messageActions.Select(message);
        }

        public void DismissMessageActions()
        {
            _messageActions.Dismiss();
        }

        private void OnMessageActionsChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SelectedMessage));
            OnPropertyChanged(nameof(IsMessageActionsOpen));
            OnPropertyChanged(nameof(CanPinSelectedMessage));
            OnPropertyChanged(nameof(CanUnpinSelectedMessage));
            (CopySelectedMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ReplySelectedMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PinSelectedFor24HoursCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PinSelectedFor7DaysCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PinSelectedFor30DaysCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UnpinSelectedMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task PinSelectedAsync(bool pin, uint durationSeconds)
        {
            ChatMessageViewModel message = SelectedMessage;
            if (message == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(message.Model.RemoteJid) && ActiveChat != null)
            {
                message.Model.RemoteJid = ActiveChat.JID;
            }

            await message.SetPinnedAsync(pin, durationSeconds);
        }

        private bool _mentionMenuOpen;

        public int MentionSelectedIndex
        {
            get => _mentionSelectedIndex;
            private set => Set(ref _mentionSelectedIndex, value);
        }

        private int _mentionSelectedIndex;

        /// <summary>Where the caret belongs after a mention is inserted. -1 when nothing was inserted.</summary>
        public int MentionInsertCaret { get; private set; } = -1;

        /// <summary>
        /// Opens or refreshes the @ menu from the field text and caret.
        /// Groups only. A direct chat never offers members.
        /// </summary>
        public void UpdateMentionQuery(string text, int caret)
        {
            MentionInsertCaret = -1;
            if (ActiveChat == null || !ActiveChat.IsGroup || ActiveChat.IsGroupLockedForMessages)
            {
                CloseMentionMenu();
                return;
            }

            int at;
            string query;
            int length;
            if (_messageService == null ||
                !_messageService.TryReadMentionToken(text, caret, out at, out query, out length))
            {
                CloseMentionMenu();
                return;
            }

            _mentionTokenStart = at;
            _mentionTokenLength = length;
            _mentionTokenCaret = caret;
            string everyone = _strings != null
                ? _strings.Get("ChatDetail_MentionEveryone", "Everyone")
                : "Everyone";
            string detail = _strings != null
                ? _strings.Get("ChatDetail_MentionEveryoneDetail", "Mention all group members")
                : "Mention all group members";
            IReadOnlyList<MentionChoice> rows = _messageService.SearchMentionChoices(
                ActiveChat.GroupMembers,
                query,
                everyone,
                detail,
                ActiveChat.AvatarUrl,
                IsCurrentMember);

            MentionChoices.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                MentionChoices.Add(rows[i]);
            }

            MentionSelectedIndex = -1;
            MentionSelectedIndex = 0;
            IsMentionMenuOpen = MentionChoices.Count > 0;
        }

        private bool IsCurrentMember(GroupMember member)
        {
            if (member == null || _contactService == null)
            {
                return false;
            }

            if (_contactService.IsSelf(member.Jid) || _contactService.IsSelf(member.Lid))
            {
                return true;
            }

            string phone = member.PhoneNumber;
            if (string.IsNullOrWhiteSpace(phone))
            {
                return false;
            }

            if (phone.IndexOf('@') < 0)
            {
                phone = phone + "@s.whatsapp.net";
            }

            return _contactService.IsSelf(phone);
        }

        public void MoveMentionSelection(int delta)
        {
            if (!IsMentionMenuOpen || MentionChoices.Count == 0)
            {
                return;
            }

            int next = _mentionSelectedIndex + delta;
            if (next < 0)
            {
                next = MentionChoices.Count - 1;
            }
            else if (next >= MentionChoices.Count)
            {
                next = 0;
            }

            MentionSelectedIndex = next;
        }

        /// <summary>Inserts the highlighted row. Returns false when the menu is closed.</summary>
        public bool TryAcceptSelectedMention()
        {
            if (!IsMentionMenuOpen || MentionChoices.Count == 0)
            {
                return false;
            }

            int index = _mentionSelectedIndex;
            if (index < 0 || index >= MentionChoices.Count)
            {
                index = 0;
            }

            AcceptMention(MentionChoices[index]);
            return true;
        }

        public void AcceptMention(MentionChoice choice)
        {
            if (choice == null)
            {
                return;
            }

            string shown = _mentions.Insert(choice, _mentionTokenCaret, _mentionTokenLength);
            MentionInsertCaret = _mentions.CaretAfterInsert(_mentionTokenStart, choice.Name);
            _messageText = shown;
            OnPropertyChanged(nameof(MessageText));
            RaiseSendMessageCanExecuteChanged();
            CloseMentionMenu();
        }

        private void CloseMentionMenu()
        {
            if (MentionChoices.Count > 0)
            {
                MentionChoices.Clear();
            }

            MentionSelectedIndex = 0;
            IsMentionMenuOpen = false;
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
                    OnPropertyChanged(nameof(IsTimelineBusy));
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
                    OnPropertyChanged(nameof(IsTimelineBusy));
                }
            }
        }

        /// <summary>
        /// First open or load-more in flight — drives the header progress and scroll lock.
        /// </summary>
        public bool IsTimelineBusy => IsLoadingMessages || IsLoadingMore;

        /// <summary>
        /// Whether the view may request older messages (scroll near top).
        /// Service/SQLite keep data; this only gates materializing more bubble VMs.
        /// <see cref="IsLoadingMore"/> is the re-entrancy lock for that path.
        /// </summary>
        public bool CanLoadMore =>
            HasActiveChat &&
            !IsLoadingMore &&
            !IsLoadingMessages &&
            !_hasReachedStart;

        /// <summary>Marks the initial timeline fetch for the open chat (progress + scroll lock).</summary>
        public void BeginLoadingMessages()
        {
            IsLoadingMessages = true;
        }

        /// <summary>Clears the initial timeline fetch flag.</summary>
        public void EndLoadingMessages()
        {
            IsLoadingMessages = false;
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

        /// <summary>Opens the in-chat camera overlay (<see cref="CameraPickViewModel"/>).</summary>
        public ICommand AttachCameraCommand { get; }

        /// <summary>Opens the document picker and sends a DocumentMessage.</summary>
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

        /// <summary>
        /// Deletes the open conversation for the account after asking, then leaves the surface -
        /// there is nothing behind it once the messages are gone.
        /// </summary>
        public ICommand DeleteChatCommand { get; }

        /// <summary>Placeholder until archive outbound is wired.</summary>
        public ICommand ArchiveChatCommand { get; }

        /// <summary>Placeholder until unarchive outbound is wired.</summary>
        public ICommand UnarchiveChatCommand { get; }

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

        /// <summary>1:1 only: confirm, then open the system dialer for the contact phone.</summary>
        public ICommand CallPhoneCommand { get; }

        /// <summary>Whether the Start tile for this chat is pinned (drives pin/unpin menu Visibility).</summary>
        public bool IsWidgetPinned => ActiveChat != null && ActiveChat.IsWidgetPinned;

        /// <summary>
        /// Start Live Tile pin UX (false on Windows 11). Null provider → available (tests / hosts without SKU).
        /// </summary>
        public bool AreLiveTilesAvailable =>
            _systemInfo == null || _systemInfo.LiveTilesCompatible();

        /// <summary>True when mute submenu should show duration options (not unmuted→unmute-only).</summary>
        public bool ShowMuteDurationOptions => ActiveChat != null && !ActiveChat.IsMutedLocally;

        public bool ShowUnmuteOption => ActiveChat != null && ActiveChat.IsMutedLocally;

        public bool CanAddToAddressBook =>
            _contactService != null &&
            ActiveChat != null &&
            !ActiveChat.IsGroup &&
            !ActiveChat.IsPersonal &&
            _contactService.CanAddToAddressBook(ActiveChat.JID);

        /// <summary>1:1 chats with a resolvable phone number.</summary>
        public bool CanCallPhone =>
            ActiveChat != null &&
            !ActiveChat.IsGroup &&
            _uriLauncher != null &&
            !string.IsNullOrEmpty(ResolveActiveChatPhone());

        private string ResolveActiveChatPhone()
        {
            ChatItem chat = ActiveChat;
            if (chat == null || chat.IsGroup)
            {
                return null;
            }

            if (_contactService != null)
            {
                string resolved = _contactService.TryResolvePhone(chat.JID);
                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }
            }

            return JidHelper.TryPhoneFromJid(chat.JID);
        }

        private async Task CallPhoneAsync()
        {
            if (!CanCallPhone)
            {
                return;
            }

            ChatItem chat = ActiveChat;
            string phone = ResolveActiveChatPhone();
            string label = chat.GetNameResolved(_strings);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = phone;
            }

            await PhoneCallPrompt.ConfirmAndCallAsync(
                phone,
                label,
                _dialogs,
                _uriLauncher,
                _strings).ConfigureAwait(false);
        }

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
            OnPropertyChanged(nameof(IsWidgetPinned));
            RaiseMuteCommandsCanExecuteChanged();
            RaiseCanAddToAddressBook();
        }

        private void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            _groupService?.InvalidateParticipantDirectChatIndex();

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(RefreshGroupAuthorLabelsAfterNamesChanged);
                return;
            }

            RefreshGroupAuthorLabelsAfterNamesChanged();
        }

        /// <summary>
        /// Roster / Person / push names often arrive after the first run layout. Rebuild the
        /// participant index and re-label visible bubbles so short JIDs do not stick until leave/reenter.
        /// </summary>
        private void RefreshGroupAuthorLabelsAfterNamesChanged()
        {
            RaiseCanAddToAddressBook();

            ChatItem chat = ActiveChat;
            if (chat == null || !LooksLikeGroupChat(chat) || Messages.Count == 0)
            {
                return;
            }

            RebuildParticipantLookup();

            var models = new List<ChatMessage>(Messages.Count);
            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessage model = Messages[i]?.Model;
                if (model != null)
                {
                    models.Add(model);
                }
            }

            if (models.Count == 0)
            {
                return;
            }

            ApplyMessageRunLayout(models, isGroup: true, chat);
        }

        private void RaiseCanAddToAddressBook()
        {
            OnPropertyChanged(nameof(CanAddToAddressBook));
            OnPropertyChanged(nameof(CanCallPhone));
            (AddContactCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CallPhoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    }
}
