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

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// On-demand profile / group-info surface shown beside (or over) the active chat.
    /// Created via <see cref="Factories.IChatDetailInfoViewModelFactory"/>.
    /// </summary>
    public sealed class ChatDetailInfoViewModel : Observable
    {
        private readonly IShortcutService _shortcutService;
        private readonly IChatStore _chatStore;
        private readonly IChatService _chatService;
        private readonly IMessageStore _messageStore;
        private readonly IMessageService _messages;
        private readonly IChatMessageVmFactory _messageVmFactory;
        private readonly IWhatsAppService _whatsApp;
        private readonly IPersonStore _personStore;
        private readonly IContactService _contacts;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
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

        /// <summary>Tiles materialized per page. Groups hold hundreds of media rows.</summary>
        private const int MediaPageSize = 30;

        /// <summary>Upper bound on the in-memory media index (models, not ViewModels).</summary>
        private const int MediaIndexLimit = 400;

        /// <summary>Media rows newest-first; only the first <see cref="_mediaWindow"/> become ViewModels.</summary>
        private readonly List<ChatMessage> _mediaIndex = new List<ChatMessage>();

        private readonly List<ChatMessage> _fileIndex = new List<ChatMessage>();

        private int _mediaWindow;
        private int _fileWindow;

        private string _notificationsValue;
        private string _pinMenuLabel;
        private string _chatPinLabel;

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
            IWhatsAppService whatsApp = null,
            GroupMember member = null,
            IPersonStore personStore = null,
            IMessageService messages = null,
            IContactService contacts = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _member = member;
            _isGroupMember = member != null;
            _isGroup = isGroup && !_isGroupMember;
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _chatService = chatService;
            _messageStore = messageStore;
            _messages = messages;
            _messageVmFactory = messageVmFactory;
            _whatsApp = whatsApp;
            _personStore = personStore;
            _contacts = contacts;
            _dispatcher = dispatcher;
            _strings = strings;

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
                () => !_isGroupMember &&
                      _source != null &&
                      !string.IsNullOrWhiteSpace(_source.JID) &&
                      _shortcutService != null &&
                      _chatStore != null);

            PinChatCommand = new RelayCommand(
                () => _ = ToggleChatPinAsync(),
                () => !_isGroupMember &&
                      _chatService != null &&
                      !string.IsNullOrWhiteSpace(_source?.JID));

            SetNotificationsCommand = new RelayCommand<bool>(
                enabled => _ = SetNotificationsEnabledAsync(enabled),
                _ => !_isGroupMember &&
                     _chatStore != null &&
                     !string.IsNullOrWhiteSpace(_source?.JID));

            AddContactCommand = new RelayCommand(
                () => _ = AddContactAsync(),
                () => CanAddToAddressBook);

            RefreshDerived();
            if (_isGroupMember)
            {
                _ = LoadSharedGroupsAsync();
            }
            else if (_whatsApp != null)
            {
                if (_isGroup)
                {
                    _ = _whatsApp.RefreshGroupSendPermissionsAsync(_source.JID);
                }

                _ = _whatsApp.EnsureHighQualityGroupAvatarAsync(_source);
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

        public bool HasSharedGroups => SharedGroups.Count > 0;

        public ChatItem Source => _source;

        public GroupMember Member => _member;

        public bool IsGroup => _isGroup;

        public bool IsGroupMember => _isGroupMember;

        public bool IsUser => !_isGroup && !_isGroupMember;

        public string AvatarUrl =>
            _isGroupMember
                ? (_member?.AvatarUrl ?? string.Empty)
                : _source.GetAvatarUrl(preferHigh: true);

        public string DisplayName
        {
            get
            {
                if (_isGroupMember)
                {
                    if (!string.IsNullOrWhiteSpace(_member?.DisplayName))
                    {
                        return _member.DisplayName;
                    }

                    string jid = _member?.Jid;
                    if (!string.IsNullOrWhiteSpace(jid) && _whatsApp != null)
                    {
                        return _whatsApp.ResolveDisplayName(jid, "sender") ?? jid;
                    }

                    return jid ?? string.Empty;
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

        public bool CanAddToAddressBook =>
            !_isGroup &&
            _contacts != null &&
            _contacts.CanAddToAddressBook(LookupJid, PhoneValue);

        public string AddContactLabel => Loc("ChatDetail_AddContact.Text", "Add contact");

        public string AddContactAppBarLabel =>
            Loc("ChatDetailInfo_AddContactAppBarLabel", "Add\ncontact");

        /// <summary>Contact about / group description — local-only placeholder until synced.</summary>
        public string StatusOrDescription => string.Empty;

        public bool HasStatusOrDescription => !string.IsNullOrWhiteSpace(StatusOrDescription);

        public string NotificationsValue
        {
            get => _notificationsValue;
            private set => Set(ref _notificationsValue, value);
        }

        /// <summary>True when the chat is not muted — notifications are delivered.</summary>
        public bool NotificationsEnabled => !_source.IsMutedLocally;

        public string PinMenuLabel
        {
            get => _pinMenuLabel;
            private set => Set(ref _pinMenuLabel, value);
        }

        public string ProfilePivotHeader =>
            _isGroupMember
                ? Loc("ChatDetailInfo_Profile", "Profile")
                : _isGroup
                    ? Loc("ChatDetailInfo_GroupInfo", "Group info")
                    : Loc("ChatDetailInfo_Profile", "Profile");

        public string NameSectionLabel =>
            _isGroup
                ? Loc("ChatDetailInfo_GroupName", "Group name")
                : Loc("ChatDetailInfo_Name", "Name");

        public string PhoneSectionLabel => Loc("ChatDetailInfo_Phone", "Phone");

        public string StatusSectionLabel =>
            _isGroup
                ? Loc("ChatDetailInfo_Description", "Description")
                : Loc("ChatDetailInfo_Status", "Status");

        public string NotificationsSectionLabel =>
            Loc("ChatDetailInfo_Notifications", "Notifications");

        public string SharedGroupsSectionLabel =>
            Loc("ChatDetailInfo_GroupsInCommon", "Groups in common");

        public string SharedGroupsEmptyText =>
            Loc("ChatDetailInfo_GroupsInCommonEmpty", "No groups in common.");

        public string AdminRoleText =>
            _member != null && _member.IsAdmin
                ? Loc("ChatDetailInfo_Admin", "Admin")
                : string.Empty;

        public bool IsMemberAdmin => _member != null && _member.IsAdmin;

        public string NotificationsOnText => Loc("ChatDetailInfo_NotificationsOn", "On");

        public string NotificationsOffText => Loc("ChatDetailInfo_NotificationsOff", "Off");

        public string MembersSectionLabel => Loc("ChatDetailInfo_Members", "Members");

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

        public string MembersPivotHeader => Loc("ChatDetailInfo_Members", "Members");

        public IList<GroupMember> Members => _source.GroupMembers;

        public bool HasMembers => _source.HasGroupMembers;

        /// <summary>Digit → name map from <see cref="Members"/> for bubble/strip parsers.</summary>
        public IReadOnlyDictionary<string, string> MentionLookup => _source.MentionLookup;

        public string MediaPivotHeader => Loc("ChatDetailInfo_Media", "Media");

        public string FilesPivotHeader => Loc("ChatDetailInfo_Files", "Files");

        public string CallsPivotHeader => Loc("ChatDetailInfo_Calls", "Calls");

        public string CallsEmptyText =>
            Loc("ChatDetailInfo_CallsEmpty", "Calls you make and receive will appear here.");

        public string MembersEmptyText =>
            Loc("ChatDetailInfo_MembersEmpty", "Group members will appear here.");

        public string MediaEmptyText =>
            Loc("ChatDetailInfo_MediaEmpty", "Photos, videos and audio will appear here.");

        public string FilesEmptyText =>
            Loc("ChatDetailInfo_FilesEmpty", "Documents will appear here.");

        /// <summary>
        /// Label for the WhatsApp chat-list pin, which is a different thing from the Start tile
        /// beside it: this one follows the account to every device.
        /// </summary>
        public string ChatPinLabel
        {
            get => _chatPinLabel;
            private set => Set(ref _chatPinLabel, value);
        }

        public ICommand PinToStartCommand { get; }

        public ICommand PinChatCommand { get; }

        /// <summary>True = unmute; false = mute forever (same local mute as the chat overflow).</summary>
        public ICommand SetNotificationsCommand { get; }

        public ICommand AddContactCommand { get; }

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
        }

        /// <summary>
        /// Starts the media/files SQLite index on first Media or Files pivot open.
        /// Live message events only rebuild after this has run once.
        /// </summary>
        public void EnsureMediaIndex()
        {
            if (_detached || _mediaIndexRequested)
            {
                return;
            }

            _mediaIndexRequested = true;
            if (!_isMediaIndexLoading)
            {
                _isMediaIndexLoading = true;
                OnPropertyChanged(nameof(IsMediaIndexLoading));
            }

            _ = RebuildFilteredAsync();
        }

        /// <summary>Materializes one more page of media tiles from the index.</summary>
        public void LoadMoreMedia()
        {
            if (!CanLoadMoreMedia)
            {
                return;
            }

            _mediaWindow = Math.Min(_mediaWindow + MediaPageSize, _mediaIndex.Count);
            SyncWindow(MediaItems, _mediaIndex, _mediaWindow, nameof(HasMedia), nameof(MediaItems), nameof(CanLoadMoreMedia));
        }

        public void LoadMoreFiles()
        {
            if (!CanLoadMoreFiles)
            {
                return;
            }

            _fileWindow = Math.Min(_fileWindow + MediaPageSize, _fileIndex.Count);
            SyncWindow(FileItems, _fileIndex, _fileWindow, nameof(HasFiles), nameof(FileItems), nameof(CanLoadMoreFiles));
        }

        private void Member_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(IsMemberAdmin));
            OnPropertyChanged(nameof(AdminRoleText));
            RaiseCanAddToAddressBook();
        }

        private void MessageService_ChatMessagesChanged(object sender, string chatJid)
        {
            if (!_mediaIndexRequested || string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            if (!string.Equals(
                    JidHelper.Normalize(chatJid),
                    JidHelper.Normalize(_source.JID),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ScheduleRebuild();
        }

        /// <summary>
        /// Coalesces the bursts of message events a history chunk produces: rebuilding the panes
        /// once per burst instead of once per chat keeps the grids off the UI thread hot path.
        /// </summary>
        private async void ScheduleRebuild()
        {
            if (_rebuildScheduled || _detached)
            {
                return;
            }

            _rebuildScheduled = true;
            try
            {
                await Task.Delay(400).ConfigureAwait(false);
            }
            finally
            {
                _rebuildScheduled = false;
            }

            if (_detached)
            {
                return;
            }

            await RebuildFilteredAsync().ConfigureAwait(false);
        }

        private async Task RebuildFilteredAsync()
        {
            if (Interlocked.Exchange(ref _rebuildBusy, 1) == 1)
            {
                _rebuildAgain = true;
                return;
            }

            try
            {
                do
                {
                    _rebuildAgain = false;
                    List<ChatMessage> loaded = await LoadMediaCandidatesAsync().ConfigureAwait(false);

                    var media = new List<ChatMessage>();
                    var files = new List<ChatMessage>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var message in loaded
                        .Where(m => m != null)
                        .OrderByDescending(m => m.Timestamp))
                    {
                        message.EnsureKindFromLegacyFlags();
                        if (!string.IsNullOrWhiteSpace(message.Id) && !seen.Add(message.Id))
                        {
                            continue;
                        }

                        if (_isGroupMember && !MessageMatchesMember(message))
                        {
                            continue;
                        }

                        if (ChatMediaFilter.IsDocument(message))
                        {
                            files.Add(message);
                        }
                        else if (ChatMediaFilter.IsMedia(message))
                        {
                            media.Add(message);
                        }
                    }

                    Action apply = () => ApplyIndexes(media, files);
                    if (_dispatcher != null)
                    {
                        await _dispatcher.RunAsync(apply);
                    }
                    else
                    {
                        apply();
                    }
                }
                while (_rebuildAgain && !_detached);
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildBusy, 0);
                SetMediaIndexLoading(false);
                if (_rebuildAgain && !_detached)
                {
                    _ = RebuildFilteredAsync();
                }
            }
        }

        private void SetMediaIndexLoading(bool value)
        {
            Action apply = () =>
            {
                if (_isMediaIndexLoading == value)
                {
                    return;
                }

                _isMediaIndexLoading = value;
                OnPropertyChanged(nameof(IsMediaIndexLoading));
            };

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(apply);
            }
            else
            {
                apply();
            }
        }

        private async Task<List<ChatMessage>> LoadMediaCandidatesAsync()
        {
            if (string.IsNullOrWhiteSpace(_source.JID))
            {
                return new List<ChatMessage>();
            }

            if (_messages != null)
            {
                try
                {
                    return await _messages.LoadChatMediaIndexAsync(_source.JID, MediaIndexLimit).ConfigureAwait(false)
                           ?? new List<ChatMessage>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatDetailInfoViewModel] Media index load failed: " + ex.Message);
                }
            }

            if (_messageStore != null)
            {
                try
                {
                    return await _messageStore.LoadMessagesAsync(_source.JID).ConfigureAwait(false)
                           ?? new List<ChatMessage>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatDetailInfoViewModel] Load messages failed: " + ex.Message);
                }
            }

            return new List<ChatMessage>();
        }

        private void ApplyIndexes(List<ChatMessage> media, List<ChatMessage> files)
        {
            // The pane can close while a rebuild is in flight; re-filling here would leave
            // bubble VMs attached to their models with nothing to detach them.
            if (_detached)
            {
                return;
            }

            _mediaIndex.Clear();
            _mediaIndex.AddRange(media);
            _fileIndex.Clear();
            _fileIndex.AddRange(files);

            _mediaWindow = ClampWindow(_mediaWindow, _mediaIndex.Count);
            _fileWindow = ClampWindow(_fileWindow, _fileIndex.Count);

            SyncWindow(MediaItems, _mediaIndex, _mediaWindow, nameof(HasMedia), nameof(MediaItems), nameof(CanLoadMoreMedia));
            SyncWindow(FileItems, _fileIndex, _fileWindow, nameof(HasFiles), nameof(FileItems), nameof(CanLoadMoreFiles));
        }

        /// <summary>Keeps what the user already scrolled into view, but never less than one page.</summary>
        private static int ClampWindow(int current, int available)
        {
            int wanted = Math.Max(current, MediaPageSize);
            return Math.Min(wanted, available);
        }

        /// <summary>
        /// Walks the desired window against the bound collection, inserting only what is missing and
        /// trimming the tail. A full clear-and-refill on a live grid is what made big groups fall over.
        /// </summary>
        private void SyncWindow(
            ObservableCollection<ChatMessageViewModel> target,
            List<ChatMessage> index,
            int windowSize,
            string hasPropertyName,
            string itemsPropertyName,
            string canLoadMorePropertyName)
        {
            int desired = Math.Min(windowSize, index.Count);
            int position = 0;
            for (int i = 0; i < desired; i++)
            {
                ChatMessage model = index[i];
                if (model == null)
                {
                    continue;
                }

                if (position < target.Count && Matches(target[position], model))
                {
                    position++;
                    continue;
                }

                ChatMessageViewModel vm = _messageVmFactory?.Create(model);
                if (vm == null)
                {
                    continue;
                }

                target.Insert(position, vm);
                position++;
            }

            while (target.Count > position)
            {
                int last = target.Count - 1;
                target[last]?.Detach();
                target.RemoveAt(last);
            }

            OnPropertyChanged(hasPropertyName);
            OnPropertyChanged(itemsPropertyName);
            OnPropertyChanged(canLoadMorePropertyName);
        }

        private static bool Matches(ChatMessageViewModel vm, ChatMessage model)
        {
            if (vm?.Model == null || model == null)
            {
                return false;
            }

            if (ReferenceEquals(vm.Model, model))
            {
                return true;
            }

            return !string.IsNullOrEmpty(model.Id) &&
                   string.Equals(vm.Model.Id, model.Id, StringComparison.Ordinal);
        }

        private bool MessageMatchesMember(ChatMessage message)
        {
            if (_member == null || message == null)
            {
                return false;
            }

            string participant = message.ParticipantJid;
            if (string.IsNullOrWhiteSpace(participant))
            {
                return false;
            }

            string canonical = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(participant)
                : JidHelper.Normalize(participant);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(participant);
            }

            return JidsMatchCanonical(_member.Jid, canonical) ||
                   JidsMatchCanonical(_member.PhoneNumber, canonical) ||
                   JidsMatchCanonical(_member.Lid, canonical);
        }

        private bool JidsMatchCanonical(string jid, string canonical)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(canonical))
            {
                return false;
            }

            string other = _whatsApp != null
                ? _whatsApp.GetCanonicalJid(jid)
                : JidHelper.Normalize(jid);
            if (string.IsNullOrWhiteSpace(other))
            {
                other = JidHelper.Normalize(jid);
            }

            return string.Equals(other, canonical, StringComparison.OrdinalIgnoreCase);
        }

        private async Task LoadSharedGroupsAsync()
        {
            if (_personStore == null || _member == null || string.IsNullOrWhiteSpace(_member.Jid))
            {
                return;
            }

            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);

                var membershipsByGroup = new Dictionary<string, PersonGroupMembership>(StringComparer.OrdinalIgnoreCase);
                foreach (string personKey in EnumeratePersonLookupKeys(_member))
                {
                    var found = await _personStore.ListGroupsForPersonAsync(personKey).ConfigureAwait(false);
                    if (found == null)
                    {
                        continue;
                    }

                    foreach (var membership in found)
                    {
                        if (membership == null || string.IsNullOrWhiteSpace(membership.GroupJid))
                        {
                            continue;
                        }

                        string groupJid = JidHelper.Normalize(membership.GroupJid);
                        if (string.IsNullOrEmpty(groupJid) || membershipsByGroup.ContainsKey(groupJid))
                        {
                            continue;
                        }

                        membershipsByGroup[groupJid] = membership;
                    }
                }

                var items = new List<SharedGroupItem>();
                foreach (var pair in membershipsByGroup)
                {
                    string groupJid = pair.Key;
                    string name = groupJid;
                    string avatar = null;
                    if (_whatsApp?.Chats != null)
                    {
                        var chat = _whatsApp.Chats.FirstOrDefault(c =>
                            c != null &&
                            string.Equals(
                                JidHelper.Normalize(c.JID),
                                groupJid,
                                StringComparison.OrdinalIgnoreCase));
                        if (chat != null)
                        {
                            name = chat.GetNameResolved(_strings) ?? chat.Name ?? groupJid;
                            avatar = chat.GetAvatarUrl(preferHigh: false);
                        }
                        else if (_whatsApp != null)
                        {
                            name = _whatsApp.ResolveDisplayName(groupJid, "chat") ?? groupJid;
                        }
                    }

                    items.Add(new SharedGroupItem
                    {
                        Jid = groupJid,
                        Name = name,
                        AvatarUrl = avatar
                    });
                }

                items = items
                    .OrderBy(i => i.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                Action apply = () =>
                {
                    SharedGroups.Clear();
                    foreach (var item in items)
                    {
                        SharedGroups.Add(item);
                    }

                    OnPropertyChanged(nameof(HasSharedGroups));
                    OnPropertyChanged(nameof(SharedGroups));
                };

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(apply);
                }
                else
                {
                    apply();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Shared groups load failed: " + ex.Message);
            }
        }

        /// <summary>
        /// PersonGroup rows may be keyed by LID, PN, or phone digits — try all aliases for the member.
        /// </summary>
        private IEnumerable<string> EnumeratePersonLookupKeys(GroupMember member)
        {
            if (member == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                string key = JidHelper.Normalize(raw);
                if (string.IsNullOrEmpty(key) || !seen.Add(key))
                {
                    return;
                }
            }

            Add(member.Jid);
            if (_whatsApp != null && !string.IsNullOrWhiteSpace(member.Jid))
            {
                Add(_whatsApp.GetCanonicalJid(member.Jid));
            }

            Add(member.Lid);
            if (_whatsApp != null && !string.IsNullOrWhiteSpace(member.Lid))
            {
                Add(_whatsApp.GetCanonicalJid(member.Lid));
            }

            string phone = member.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(phone))
            {
                if (phone.Contains("@"))
                {
                    Add(phone);
                }
                else
                {
                    string digits = PhoneNumberHelper.NormalizePhoneDigits(phone);
                    if (!string.IsNullOrEmpty(digits))
                    {
                        Add(digits + "@s.whatsapp.net");
                    }
                }
            }

            foreach (string key in seen)
            {
                yield return key;
            }
        }

        private void Source_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == null)
            {
                RefreshDerived();
                RaiseDisplayProperties();
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.Kind) ||
                e.PropertyName == nameof(ChatItem.JID))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(PhoneValue));
                OnPropertyChanged(nameof(HasPhone));
                RaiseCanAddToAddressBook();
            }
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl) ||
                     e.PropertyName == nameof(ChatItem.AvatarHighUrl))
            {
                OnPropertyChanged(nameof(AvatarUrl));
            }
            else if (e.PropertyName == nameof(ChatItem.GroupMemberCount) ||
                     e.PropertyName == nameof(ChatItem.GroupMembers) ||
                     e.PropertyName == nameof(ChatItem.HasGroupMembers))
            {
                OnPropertyChanged(nameof(HasMembersCount));
                OnPropertyChanged(nameof(MembersCountText));
                OnPropertyChanged(nameof(Members));
                OnPropertyChanged(nameof(HasMembers));
                OnPropertyChanged(nameof(MentionLookup));
            }
            else if (e.PropertyName == nameof(ChatItem.MutedUntil) ||
                     e.PropertyName == nameof(ChatItem.IsMutedLocally))
            {
                RefreshNotifications();
            }
            else if (e.PropertyName == nameof(ChatItem.IsWidgetPinned))
            {
                RefreshPinLabel();
                (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(ChatItem.IsChatPinned))
            {
                RefreshChatPinLabel();
            }
        }

        private void RefreshDerived()
        {
            RefreshNotifications();
            RefreshPinLabel();
            RefreshChatPinLabel();
            RaiseCanAddToAddressBook();
        }

        private string LookupJid =>
            _isGroupMember ? _member?.Jid : _source?.JID;

        private void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            RaiseCanAddToAddressBookOnUi();
        }

        private void PersonStore_PersonChanged(object sender, string jid)
        {
            RaiseCanAddToAddressBookOnUi();
        }

        private void RaiseCanAddToAddressBookOnUi()
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
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(CanAddToAddressBook));
            (AddContactCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task AddContactAsync()
        {
            if (_contacts == null || !CanAddToAddressBook)
            {
                return;
            }

            try
            {
                await _contacts.ShowAddToAddressBookAsync(
                    DisplayName,
                    PhoneValue,
                    AvatarUrl,
                    LookupJid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Add contact failed: " + ex.Message);
            }

            RaiseCanAddToAddressBook();
        }

        private void RefreshNotifications()
        {
            NotificationsValue = _source.IsMutedLocally
                ? Loc("ChatDetailInfo_NotificationsOff", "Off")
                : Loc("ChatDetailInfo_NotificationsOn", "On");
            OnPropertyChanged(nameof(NotificationsEnabled));
            (SetNotificationsCommand as RelayCommand<bool>)?.RaiseCanExecuteChanged();
        }

        private async Task SetNotificationsEnabledAsync(bool enabled)
        {
            if (_chatStore == null || _source == null || string.IsNullOrWhiteSpace(_source.JID))
            {
                return;
            }

            if (enabled == NotificationsEnabled)
            {
                return;
            }

            long? mutedUntil = enabled ? (long?)null : ChatMuteHelper.ForeverUnixSeconds;
            ChatItem chat = _source;
            try
            {
                chat.MutedUntil = mutedUntil;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailInfoViewModel] Set notifications failed: " + ex.Message);
            }
        }

        private void RefreshPinLabel()
        {
            PinMenuLabel = _source.IsWidgetPinned
                ? Loc("ChatDetailInfo_UnpinAppBarLabel", "Unpin from\nStart")
                : Loc("ChatDetailInfo_PinAppBarLabel", "Pin to\nStart");
        }

        private void RefreshChatPinLabel()
        {
            ChatPinLabel = _source.IsChatPinned
                ? Loc("ChatDetailInfo_UnpinChatAppBarLabel", "Unpin\nchat")
                : Loc("ChatDetailInfo_PinChatAppBarLabel", "Pin\nchat");
        }

        /// <summary>
        /// The pin the account shares. The label follows the chat rather than the outcome of this
        /// call, because the row is updated the moment the change is accepted locally and reverted
        /// if the server refuses it.
        /// </summary>
        private async Task ToggleChatPinAsync()
        {
            if (_chatService == null || _source == null)
            {
                return;
            }

            try
            {
                await _chatService.SetPinnedAsync(_source, !_source.IsChatPinned);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Chat pin failed: " + ex.Message);
            }
        }

        private void RaiseDisplayProperties()
        {
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(CanAddToAddressBook));
            OnPropertyChanged(nameof(StatusOrDescription));
            OnPropertyChanged(nameof(HasStatusOrDescription));
            OnPropertyChanged(nameof(HasMembersCount));
            OnPropertyChanged(nameof(MembersCountText));
            OnPropertyChanged(nameof(Members));
            OnPropertyChanged(nameof(HasMembers));
            OnPropertyChanged(nameof(MentionLookup));
        }

        private async Task ToggleWidgetPinAsync()
        {
            if (_source == null || _shortcutService == null || _chatStore == null)
            {
                return;
            }

            ChatItem chat = _source;
            bool nextPinned = !chat.IsWidgetPinned;
            try
            {
                bool ok = nextPinned
                    ? await _shortcutService.PinChatAsync(chat)
                    : await _shortcutService.UnpinChatAsync(chat.JID);

                if (!ok && nextPinned)
                {
                    return;
                }

                chat.IsWidgetPinned = nextPinned;
                await _chatStore.UpsertAsync(
                    chat.JID,
                    chat.LocalStatus,
                    chat.IsWidgetPinned,
                    chat.IsChatPinned,
                    chat.MutedUntil);

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(() =>
                    {
                        RefreshPinLabel();
                        (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    });
                }
                else
                {
                    RefreshPinLabel();
                    (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailInfoViewModel] Pin failed: " + ex.Message);
            }
        }

        private string Loc(string key, string fallback) =>
            _strings != null ? _strings.Get(key, fallback) : fallback;
    }
}
