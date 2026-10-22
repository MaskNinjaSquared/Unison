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
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// On-demand profile / group-info surface shown beside (or over) the active chat.
    /// Created via <see cref="Factories.IChatDetailInfoViewModelFactory"/>.
    /// </summary>
    public sealed partial class ChatDetailInfoViewModel : Observable
    {
        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly IChatService _chatService;
        private readonly IMessageStore _messageStore;
        private readonly IMessageService _messages;
        private readonly IChatMessageVmFactory _messageVmFactory;

        /// <summary>Send permissions, roster and the group picture.</summary>
        private readonly IGroupService _groups;

        /// <summary>The chat list, for naming the groups this person shares with us.</summary>
        private readonly IChatStateStore _chatState;

        private readonly ParticipantResolutionContext _participants;
        private readonly IJidResolver _jids;
        private readonly IPersonStore _personStore;
        private readonly IContactService _contacts;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly IDialogService _dialogs;
        private readonly IUriLauncher _uriLauncher;
        private readonly ISystemInfoProvider _systemInfo;
        private readonly ChatItem _source;
        private readonly GroupMember _member;
        private readonly bool _isGroup;
        private readonly bool _isGroupMember;
        private int _rebuildBusy;
        private bool _rebuildAgain;
        private bool _rebuildScheduled;
        private bool _detached;
        private bool _mediaIndexRequested;
        private bool _isMediaIndexLoading;
        private bool _membersAvatarsRequested;
        private bool _isMembersAvatarsLoading;
        private bool _isMembersRosterLoading;
        private Task _membersRosterEnsureTask;

        /// <summary>Tiles materialized per page. Groups hold hundreds of media rows.</summary>
        private const int MediaPageSize = 30;

        /// <summary>Upper bound on the in-memory media index (models, not ViewModels).</summary>
        private const int MediaIndexLimit = 400;

        /// <summary>How long a burst of message events is allowed to settle before rebuilding.</summary>
        private static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(400);

        /// <summary>Media rows newest-first; only the first <see cref="_mediaWindow"/> become ViewModels.</summary>
        private readonly List<ChatMessage> _mediaIndex = new List<ChatMessage>();

        private readonly List<ChatMessage> _fileIndex = new List<ChatMessage>();

        private int _mediaWindow;
        private int _fileWindow;

        public ChatDetailInfoViewModel(
            ChatItem source,
            bool isGroup,
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings,
            IChatService chatService = null,
            IMessageStore messageStore = null,
            IChatMessageVmFactory messageVmFactory = null,
            IGroupService groups = null,
            GroupMember member = null,
            IPersonStore personStore = null,
            IMessageService messages = null,
            IContactService contacts = null,
            IDialogService dialogs = null,
            ParticipantResolutionContext participants = null,
            IUriLauncher uriLauncher = null,
            IChatStateStore chatState = null,
            ISystemInfoProvider systemInfo = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _participants = participants;
            _jids = participants?.Jids;
            _member = member;
            _isGroupMember = member != null;
            _isGroup = isGroup && !_isGroupMember;
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _chatService = chatService;
            _messageStore = messageStore;
            _messages = messages;
            _messageVmFactory = messageVmFactory;
            _groups = groups;
            _chatState = chatState;
            _personStore = personStore;
            _contacts = contacts;
            _dispatcher = dispatcher;
            _strings = strings;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _systemInfo = systemInfo;

            _source.PropertyChanged += Source_PropertyChanged;
            if (_member != null)
            {
                _member.PropertyChanged += Member_PropertyChanged;
            }

            // SQLite history chunks only raise the façade event, never the raw client one.
            if (_messages != null)
            {
                _messages.ChatMessagesChanged += MessageService_ChatMessagesChanged;
            }

            if (_contacts != null)
            {
                _contacts.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
            }

            if (_personStore != null)
            {
                _personStore.PersonChanged += PersonStore_PersonChanged;
            }

            PinToStartCommand = new RelayCommand(
                () => _ = ToggleWidgetPinAsync(),
                () => AreLiveTilesAvailable &&
                      !_isGroupMember &&
                      _source != null &&
                      !string.IsNullOrWhiteSpace(_source.JID) &&
                      _shortcutService != null &&
                      _chatStore != null);

            PinContactToStartCommand = new RelayCommand(
                () => _ = ToggleContactWidgetPinAsync(),
                () => AreLiveTilesAvailable &&
                      !_isGroup &&
                      !_isGroupMember &&
                      _source != null &&
                      !string.IsNullOrWhiteSpace(_source.JID) &&
                      _shortcutService != null);

            PinChatCommand = new RelayCommand(
                () => _ = ToggleChatPinAsync(),
                () => !_isGroupMember &&
                      _chatService != null &&
                      !string.IsNullOrWhiteSpace(_source?.JID));

            // Same surface as ChatDetail overflow: 1:1 and group. Hidden on a member's profile so
            // it cannot delete the group the member happens to be in.
            DeleteChatCommand = new RelayCommand(
                () => _ = DeleteChatAsync(),
                () => !_isGroupMember &&
                      _chatService != null &&
                      !string.IsNullOrWhiteSpace(_source?.JID));

            SetNotificationsCommand = new RelayCommand<bool>(
                enabled => _ = SetNotificationsEnabledAsync(enabled),
                _ => !_isGroupMember &&
                     _chatStore != null &&
                     _chatService != null &&
                     !string.IsNullOrWhiteSpace(_source?.JID));

            AddContactCommand = new RelayCommand(
                () => _ = AddContactAsync(),
                () => CanAddToAddressBook);

            CallPhoneCommand = new RelayCommand(
                () => _ = CallPhoneAsync(),
                () => CanCallPhone);

            RefreshDerived();
            if (_isGroupMember)
            {
                _ = LoadSharedGroupsAsync();
            }
            else if (_groups != null)
            {
                if (_isGroup)
                {
                    _ = _groups.RefreshGroupSendPermissionsAsync(_source.JID);
                }

                _ = _groups.EnsureHighQualityGroupAvatarAsync(_source);
            }

            if (!_isGroupMember)
            {
                _ = SyncContactWidgetPinFromSystemAsync();
            }
        }

        public ObservableCollection<ChatMessageViewModel> MediaItems { get; } =
            new ObservableCollection<ChatMessageViewModel>();

        public ObservableCollection<ChatMessageViewModel> FileItems { get; } =
            new ObservableCollection<ChatMessageViewModel>();

        public ObservableCollection<SharedGroupItem> SharedGroups { get; } =
            new ObservableCollection<SharedGroupItem>();

        public bool HasMedia => MediaItems.Count > 0;

        public bool HasFiles => FileItems.Count > 0;

        /// <summary>More media rows indexed than materialized — pane asks for the next page on scroll.</summary>
        public bool CanLoadMoreMedia => _mediaWindow < _mediaIndex.Count;

        public bool CanLoadMoreFiles => _fileWindow < _fileIndex.Count;

        /// <summary>
        /// SQLite media/files index is idle until the Media or Files pivot is opened.
        /// </summary>
        public bool IsMediaIndexLoading => _isMediaIndexLoading;

        /// <summary>True while the Members pivot is hydrating roster pictures.</summary>
        public bool IsMembersAvatarsLoading => _isMembersAvatarsLoading;

        /// <summary>True while the Members pivot is fetching the roster metadata base.</summary>
        public bool IsMembersRosterLoading => _isMembersRosterLoading;

        public bool HasSharedGroups => SharedGroups.Count > 0;

        public ChatItem Source => _source;

        public GroupMember Member => _member;

        public bool IsGroup => _isGroup;

        public bool IsGroupMember => _isGroupMember;

        public bool IsUser => !_isGroup && !_isGroupMember;

        public string AvatarUrl =>
            _isGroupMember
                ? (_groups?.ResolveMemberAvatar(
                    _member?.Jid ?? _member?.Lid ?? _member?.PhoneNumber,
                    _source,
                    _participants,
                    _member) ?? string.Empty)
                : _source.GetAvatarUrl(preferHigh: true);

        public string DisplayName
        {
            get
            {
                if (_isGroupMember)
                {
                    string jid = _member?.Jid ?? _member?.Lid ?? _member?.PhoneNumber;
                    return _groups?.ResolveMemberDisplayName(
                        jid,
                        _source,
                        _participants,
                        _member?.DisplayName,
                        _member) ?? string.Empty;
                }

                return _source.GetNameResolved(_strings) ?? string.Empty;
            }
        }

        public string PhoneValue
        {
            get
            {
                if (_isGroup)
                {
                    return string.Empty;
                }

                if (_contacts != null)
                {
                    string resolved = _contacts.TryResolvePhone(
                        LookupJid,
                        _isGroupMember ? _member?.PhoneNumber : null);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        return resolved;
                    }
                }

                if (_isGroupMember)
                {
                    string fromMember = PhoneNumberHelper.NormalizePhoneDigits(_member?.PhoneNumber);
                    if (!string.IsNullOrEmpty(fromMember))
                    {
                        return fromMember;
                    }

                    return JidHelper.TryPhoneFromJid(_member?.Jid) ?? string.Empty;
                }

                return JidHelper.TryPhoneFromJid(_source.JID) ?? string.Empty;
            }
        }

        public bool HasPhone => !string.IsNullOrWhiteSpace(PhoneValue);

        /// <summary>Non-group profiles with a phone — drives the dialer hyperlink.</summary>
        public bool CanCallPhone =>
            !_isGroup &&
            _uriLauncher != null &&
            HasPhone;

        public bool CanAddToAddressBook =>
            !_isGroup &&
            _contacts != null &&
            _contacts.CanAddToAddressBook(LookupJid, PhoneValue);

        /// <summary>Contact about / group description — local-only placeholder until synced.</summary>
        public string StatusOrDescription => string.Empty;

        public bool HasStatusOrDescription => !string.IsNullOrWhiteSpace(StatusOrDescription);

        /// <summary>True when the chat is not muted — notifications are delivered.</summary>
        public bool NotificationsEnabled => !_source.IsMutedLocally;

        /// <summary>Start-tile pin state (drives pin/unpin app-bar Visibility).</summary>
        public bool IsWidgetPinned => _source != null && _source.IsWidgetPinned;

        public bool IsContactWidgetPinned => _source != null && _source.IsContactWidgetPinned;

        /// <summary>
        /// Start Live Tile pin UX (false on Windows 11). Null provider → available.
        /// </summary>
        public bool AreLiveTilesAvailable =>
            _systemInfo == null || _systemInfo.LiveTilesCompatible();

        /// <summary>Account chat-list pin state (drives pin/unpin app-bar Visibility).</summary>
        public bool IsChatPinned => _source != null && _source.IsChatPinned;

        public bool IsMemberAdmin => _member != null && _member.IsAdmin;

        public bool HasMembersCount => _source.GroupMemberCount > 0;

        public string MembersCountText
        {
            get
            {
                int count = _source.GroupMemberCount;
                if (count <= 0)
                {
                    return "—";
                }

                if (count == 1)
                {
                    return Loc("ChatDetailInfo_MemberCountSingular", "1 member");
                }

                return string.Format(
                    Loc("ChatDetailInfo_MemberCount", "{0} members"),
                    count);
            }
        }

        public IList<GroupMember> Members => _source.GroupMembers;

        public bool HasMembers => _source.HasGroupMembers;

        /// <summary>Digit → name map from <see cref="Members"/> for bubble/strip parsers.</summary>
        public IReadOnlyDictionary<string, string> MentionLookup => _source.MentionLookup;

        /// <summary>Pins the conversation Start tile (<c>chat=</c>).</summary>
        public ICommand PinToStartCommand { get; }

        /// <summary>Pins the contact Profile Start tile (<c>profile=</c>).</summary>
        public ICommand PinContactToStartCommand { get; }

        public ICommand PinChatCommand { get; }

        /// <summary>Deletes the 1:1 conversation for the account after asking.</summary>
        public ICommand DeleteChatCommand { get; }

        /// <summary>
        /// Raised once the conversation is gone, so the host can leave the chat surface instead of
        /// keeping an info pane open on something that no longer exists.
        /// </summary>
        public event EventHandler ChatDeleted;

        /// <summary>True = unmute; false = mute forever (same local mute as the chat overflow).</summary>
        public ICommand SetNotificationsCommand { get; }

        public ICommand AddContactCommand { get; }

        /// <summary>Confirm + open dialer for this profile phone (1:1 or group member).</summary>
        public ICommand CallPhoneCommand { get; }

        public void Detach()
        {
            _detached = true;
            _source.PropertyChanged -= Source_PropertyChanged;
            if (_member != null)
            {
                _member.PropertyChanged -= Member_PropertyChanged;
            }

            if (_messages != null)
            {
                _messages.ChatMessagesChanged -= MessageService_ChatMessagesChanged;
            }

            if (_contacts != null)
            {
                _contacts.DisplayNamesUpdated -= Contacts_DisplayNamesUpdated;
            }

            if (_personStore != null)
            {
                _personStore.PersonChanged -= PersonStore_PersonChanged;
            }

            for (int i = 0; i < MediaItems.Count; i++)
            {
                MediaItems[i]?.Detach();
            }

            MediaItems.Clear();
            for (int i = 0; i < FileItems.Count; i++)
            {
                FileItems[i]?.Detach();
            }

            FileItems.Clear();
            _mediaIndex.Clear();
            _fileIndex.Clear();
            _mediaWindow = 0;
            _fileWindow = 0;
            _mediaIndexRequested = false;
            _isMediaIndexLoading = false;
            _membersAvatarsRequested = false;
            _isMembersAvatarsLoading = false;
            _isMembersRosterLoading = false;
            _membersRosterEnsureTask = null;
        }

        /// <summary>
        /// Starts the media/files SQLite index on first Media or Files pivot open.
        /// Live message events only rebuild after this has run once.
    }
}
