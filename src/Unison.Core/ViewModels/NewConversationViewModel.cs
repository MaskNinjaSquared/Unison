// =============================================================================
// NewConversationViewModel
//
// Full-bleed "New conversation" overlay above the chat list: action tiles,
// message-yourself row, and A–Z contacts (searchable).
// =============================================================================
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Owns the New Conversation surface. Hosts bind <see cref="IsOpen"/> and
    /// subscribe to <see cref="ChatOpenRequested"/> to navigate into a chat.
    /// </summary>
    public sealed partial class NewConversationViewModel : Observable
    {
        private readonly IChatStateStore _chatState;
        private readonly IContactService _contacts;
        private readonly IChatService _chats;
        private readonly IProfileService _profile;
        private readonly IJidResolver _jids;
        private readonly IDialogService _dialogs;
        private readonly INewChatDialogViewModelFactory _newChatFactory;
        private readonly IStringResources _strings;
        private readonly IDispatcher _dispatcher;
        private readonly IUrgentDispatcher _urgentUi;
        private readonly ILocalContactsService _localContacts;
        private readonly IFilePicker _filePicker;
        private readonly ICameraPickService _cameraPick;
        private readonly IGroupService _groups;
        private readonly INotificationService _notifications;
        private readonly IChannelService _channels;

        private static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// First open only: leave chrome + spinner on screen for at least one paint before
        /// <c>x:Load</c> of the A–Z host. Same-turn materialize finished in ~15ms and hid the ring.
        /// </summary>
        private static readonly TimeSpan FirstOpenPaintBudget = TimeSpan.FromMilliseconds(50);

        private bool _isOpen;
        private bool _catalogHooked;
        private bool _rebuildScheduled;
        private bool _isCatalogLoading;
        private bool _isContactsSurfaceReady;
        private string _searchQuery = string.Empty;
        private string _selfDisplayName = string.Empty;
        private string _selfAvatarUrl;
        private string _selfSubtitle = string.Empty;
        private ObservableCollection<AlphabetChatGroup> _groupedContacts =
            new ObservableCollection<AlphabetChatGroup>();
        private ObservableCollection<AlphabetJumpTile> _jumpTiles =
            new ObservableCollection<AlphabetJumpTile>();

        public NewConversationViewModel(
            IChatStateStore chatState,
            IContactService contacts,
            IChatService chats,
            IProfileService profile,
            IJidResolver jids,
            IDialogService dialogs,
            INewChatDialogViewModelFactory newChatFactory,
            IStringResources strings,
            IDispatcher dispatcher = null,
            ILocalContactsService localContacts = null,
            IFilePicker filePicker = null,
            IGroupService groups = null,
            INotificationService notifications = null,
            IChannelService channels = null,
            IUrgentDispatcher urgentDispatcher = null,
            ICameraPickService cameraPick = null)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
            _chats = chats;
            _profile = profile;
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _newChatFactory = newChatFactory ?? throw new ArgumentNullException(nameof(newChatFactory));
            _strings = strings;
            _dispatcher = dispatcher;
            // Create dismiss must beat Normal name-batch hops; AttachedUiDispatcher is both.
            _urgentUi = urgentDispatcher ?? (dispatcher as IUrgentDispatcher);
            _localContacts = localContacts;
            _filePicker = filePicker;
            _cameraPick = cameraPick;
            _groups = groups;
            _notifications = notifications;
            _channels = channels;

            CloseCommand = new RelayCommand(Close);
            NewGroupCommand = new RelayCommand(OpenCreateGroup);
            NewContactCommand = new RelayCommand(() => _ = OpenNewContactAsync());
            NewCommunityCommand = new RelayCommand(OpenCreateCommunity);
            NewChannelCommand = new RelayCommand(OpenCreateChannel);
            PhoneNumberCommand = new RelayCommand(() => _ = OpenPhoneDialogAsync());
            NavigateToPrivateChatCommand = new RelayCommand(() => _ = NavigateToPrivateChatAsync());
            SelectContactCommand = new RelayCommand<ChatItem>(chat => _ = OpenContactAsync(chat));
            InitCreateFlowCommands();
        }

        /// <summary>Raised after a contact / phone / self row should open a conversation.</summary>
        public event EventHandler<string> ChatOpenRequested;

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen == value)
                {
                    return;
                }

                _isOpen = value;
                OnPropertyChanged(nameof(IsOpen));
                if (value)
                {
                    RefreshSelfTile();
                    HookCatalog();
                    RefreshCreateChrome();
                    // First open: chrome paints with loading; contacts SemanticZoom materializes
                    // after yield. Later opens keep the surface warm and only refresh catalog.
                    if (!_isContactsSurfaceReady)
                    {
                        IsCatalogLoading = true;
                    }

                    ScheduleOpenRebuild();
                }
                else
                {
                    UnhookCatalog();
                    SearchQuery = string.Empty;
                    ClearCreateFlow();
                    IsCatalogLoading = false;
                }
            }
        }

        /// <summary>
        /// True while the first contacts surface is still deferred (spinner over the list area).
        /// Stays false on later opens once <see cref="IsContactsSurfaceReady"/> is sticky.
        /// </summary>
        public bool IsCatalogLoading
        {
            get => _isCatalogLoading;
            private set
            {
                if (_isCatalogLoading == value)
                {
                    return;
                }

                _isCatalogLoading = value;
                OnPropertyChanged(nameof(IsCatalogLoading));
            }
        }

        /// <summary>
        /// Sticky after the first deferred open: hosts x:Load the A–Z SemanticZoom once, then
        /// subsequent <see cref="IsOpen"/> toggles only Visibility.
        /// </summary>
        public bool IsContactsSurfaceReady
        {
            get => _isContactsSurfaceReady;
            private set
            {
                if (_isContactsSurfaceReady == value)
                {
                    return;
                }

                _isContactsSurfaceReady = value;
                OnPropertyChanged(nameof(IsContactsSurfaceReady));
            }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_searchQuery, next, StringComparison.Ordinal))
                {
                    return;
                }

                _searchQuery = next;
                OnPropertyChanged(nameof(SearchQuery));
                if (_isOpen)
                {
                    RebuildCatalog();
                }
            }
        }

        public string SelfDisplayName
        {
            get => _selfDisplayName;
            private set => Set(ref _selfDisplayName, value);
        }

        public string SelfAvatarUrl
        {
            get => _selfAvatarUrl;
            private set => Set(ref _selfAvatarUrl, value);
        }

        public string SelfSubtitle
        {
            get => _selfSubtitle;
            private set => Set(ref _selfSubtitle, value);
        }

        public ObservableCollection<AlphabetChatGroup> GroupedContacts
        {
            get => _groupedContacts;
            private set
            {
                if (ReferenceEquals(_groupedContacts, value))
                {
                    return;
                }

                _groupedContacts = value ?? new ObservableCollection<AlphabetChatGroup>();
                OnPropertyChanged(nameof(GroupedContacts));
            }
        }

        public ObservableCollection<AlphabetJumpTile> JumpTiles
        {
            get => _jumpTiles;
            private set
            {
                if (ReferenceEquals(_jumpTiles, value))
                {
                    return;
                }

                _jumpTiles = value ?? new ObservableCollection<AlphabetJumpTile>();
                OnPropertyChanged(nameof(JumpTiles));
            }
        }

        public ICommand CloseCommand { get; }
        public ICommand NewGroupCommand { get; }
        public ICommand NewContactCommand { get; }
        public ICommand NewCommunityCommand { get; }
        public ICommand NewChannelCommand { get; }
        public ICommand PhoneNumberCommand { get; }

        /// <summary>Opens the notes-to-self / personal chat (Message yourself).</summary>
        public ICommand NavigateToPrivateChatCommand { get; }

        public ICommand SelectContactCommand { get; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        private void RefreshSelfTile()
        {
            Profile profile = _profile?.GetCurrentProfile() ?? _jids.Self;
            string you = _strings?.Get("NewConversation_You", "You") ?? "You";
            string name = profile?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = you;
            }

            SelfDisplayName = name.Trim() + " (" + you + ")";
            SelfAvatarUrl = profile?.AvatarUrl;
            SelfSubtitle = _strings?.Get(
                "NewConversation_MessageYourself",
                "Message yourself") ?? "Message yourself";
        }

        private void HookCatalog()
        {
            if (_catalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged += OnChatsChanged;
            _chatState.DisplayNamesChanged += OnDisplayNamesChanged;
            if (_profile != null)
            {
                _profile.ProfileChanged += OnProfileChanged;
            }

            _catalogHooked = true;
        }

        private void UnhookCatalog()
        {
            _rebuildScheduled = false;
            if (!_catalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged -= OnChatsChanged;
            _chatState.DisplayNamesChanged -= OnDisplayNamesChanged;
            if (_profile != null)
            {
                _profile.ProfileChanged -= OnProfileChanged;
            }

            _catalogHooked = false;
        }

        private void OnChatsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_isOpen)
            {
                return;
            }

            ScheduleRebuild();
        }

        private void OnDisplayNamesChanged(object sender, EventArgs e)
        {
            if (!_isOpen)
            {
                return;
            }

            ScheduleRebuild();
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            if (!_isOpen)
            {
                return;
            }

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(RefreshSelfTile);
                return;
            }

            RefreshSelfTile();
        }

        /// <summary>
        /// Coalesces DisplayNamesChanged / Chats bursts: one A–Z rebuild per window instead of
        /// one per enrich hop, so opening Nova Conversa does not starve the Normal UI queue.
        /// </summary>
        private void ScheduleRebuild()
        {
            if (_rebuildScheduled || !_isOpen)
            {
                return;
            }

            _rebuildScheduled = true;
            _ = RunScheduledRebuildAsync(RebuildDebounce);
        }

        /// <summary>
        /// First paint after Open: one yield so leave-chat / pane flip drains, then catalog.
        /// </summary>
        private void ScheduleOpenRebuild()
        {
            if (_rebuildScheduled)
            {
                return;
            }

            _rebuildScheduled = true;
            _ = RunScheduledRebuildAsync(TimeSpan.Zero);
        }

        /// <summary>
        /// Awaited half of <see cref="ScheduleRebuild"/> — kept out of <c>async void</c> so a
        /// throw from rebuild lands in the catch below instead of the sync context.
        /// </summary>
        private async Task RunScheduledRebuildAsync(TimeSpan debounce)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                try
                {
                    if (debounce > TimeSpan.Zero)
                    {
                        await Task.Delay(debounce).ConfigureAwait(false);
                    }
                    else
                    {
                        // Leave the Open/IsOpen turn before CollectionViewSource bind.
                        await Task.Yield();
                    }
                }
                finally
                {
                    _rebuildScheduled = false;
                }

                if (!_isOpen)
                {
                    IsCatalogLoading = false;
                    return;
                }

                // First open: IsCatalogLoading is already true — wait a frame so the ring paints
                // before sticky x:Load + catalog bind clear it on the same hop.
                if (!_isContactsSurfaceReady)
                {
                    await Task.Delay(FirstOpenPaintBudget).ConfigureAwait(false);
                    if (!_isOpen)
                    {
                        IsCatalogLoading = false;
                        return;
                    }
                }

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(MaterializeContactsAndRebuild).ConfigureAwait(false);
                }
                else
                {
                    MaterializeContactsAndRebuild();
                }

                Debug.WriteLine(
                    "[NewConversation] catalog-ready; groups=" + GroupedContacts.Count +
                    "; surface=" + _isContactsSurfaceReady +
                    "; ms=" + sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                IsCatalogLoading = false;
                Debug.WriteLine("[NewConversation] Scheduled rebuild failed: " + ex.Message);
            }
        }

        /// <summary>
        /// UI-thread half of open: flip sticky x:Load for the A–Z host, then bind catalog.
        /// </summary>
        private void MaterializeContactsAndRebuild()
        {
            if (!_isContactsSurfaceReady)
            {
                IsContactsSurfaceReady = true;
            }

            RebuildCatalog();
            IsCatalogLoading = false;
        }

        private void RebuildCatalog()
        {
            var rows = NewConversationCatalog.Build(
                _chatState.Chats,
                jid => _contacts.IsSelf(jid) || _jids.IsSelfLinked(jid),
                c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty,
                _searchQuery,
                _contacts.PhoneContactNamesByJid);

            GroupedContacts = AlphabetChatGroups.Build(
                rows,
                c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty);
            JumpTiles = AlphabetChatGroups.BuildJumpTiles(GroupedContacts);
        }

        private Task ShowComingSoonAsync(string titleKey) =>
            ShowComingSoonAsync(titleKey, "Coming soon");

        private async Task ShowComingSoonAsync(string titleKey, string titleFallback)
        {
            string title = _strings?.Get(titleKey, titleFallback) ?? titleFallback;
            string body = _strings?.Get(
                "NewConversation_ComingSoon",
                "This option is not available yet.") ?? "This option is not available yet.";
            string close = _strings?.Get("Common_Close", "Close") ?? "Close";
            try
            {
                await _dialogs.ShowMessageAsync(title, body, close).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] ComingSoon failed: " + ex.Message);
            }
        }

        private async Task OpenNewContactAsync()
        {
            try
            {
                if (_localContacts != null &&
                    await _localContacts.OpenNewContactEditorAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] Open People new contact failed: " + ex.Message);
            }

            await ShowComingSoonAsync("NewConversation_NewContact.Text", "New contact")
                .ConfigureAwait(false);
        }

        private async Task OpenPhoneDialogAsync()
        {
            try
            {
                string jid = await _dialogs.ShowNewChatDialogAsync(_newChatFactory.Create())
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(jid))
                {
                    return;
                }

                await OpenJidAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] Phone dialog failed: " + ex.Message);
            }
        }

        private async Task NavigateToPrivateChatAsync()
        {
            // Prefer an existing Personal / self-linked row over creating a duplicate.
            string jid = null;
            foreach (ChatItem chat in _chatState.Chats)
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (chat.IsPersonal ||
                    _contacts.IsSelf(chat.JID) ||
                    _jids.IsSelfLinked(chat.JID))
                {
                    jid = chat.JID;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                Profile profile = _profile?.GetCurrentProfile() ?? _jids.Self;
                jid = profile?.Id;
                if (string.IsNullOrWhiteSpace(jid))
                {
                    jid = _jids.Self?.Id;
                }
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                string title = _strings?.Get("NewConversation_Title.Text", "New chat") ?? "New chat";
                string body = _strings?.Get(
                    "NewConversation_SelfUnavailable",
                    "Your account is not ready yet.") ?? "Your account is not ready yet.";
                string close = _strings?.Get("Common_Close", "Close") ?? "Close";
                await _dialogs.ShowMessageAsync(title, body, close).ConfigureAwait(false);
                return;
            }

            await OpenJidAsync(jid).ConfigureAwait(false);
        }

        private Task OpenContactAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return Task.CompletedTask;
            }

            return OpenJidAsync(chat.JID);
        }

        private async Task OpenJidAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(jid) ?? JidHelper.Normalize(jid) ?? jid;
            string openJid = jid;
            bool exists = false;
            foreach (ChatItem chat in _chatState.Chats)
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                string chatCanonical = _jids.GetCanonicalJid(chat.JID) ?? JidHelper.Normalize(chat.JID) ?? chat.JID;
                if (string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(chatCanonical, canonical, StringComparison.OrdinalIgnoreCase) ||
                    (_jids.IsSelfLinked(jid) &&
                     (_contacts.IsSelf(chat.JID) || _jids.IsSelfLinked(chat.JID) || chat.IsPersonal)))
                {
                    exists = true;
                    openJid = chat.JID;
                    break;
                }
            }

            if (!exists && _chats != null)
            {
                _chats.StartNewChat(jid);
                await Task.Delay(100).ConfigureAwait(false);
                openJid = jid;
            }

            IsOpen = false;
            ChatOpenRequested?.Invoke(this, openJid);
        }
    }
}
