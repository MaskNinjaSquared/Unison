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
    public sealed class NewConversationViewModel : Observable
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

        private bool _isOpen;
        private bool _catalogHooked;
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
            IDispatcher dispatcher = null)
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

            CloseCommand = new RelayCommand(Close);
            NewGroupCommand = new RelayCommand(() => _ = ShowComingSoonAsync("NewConversation_NewGroup.Text"));
            NewContactCommand = new RelayCommand(() => _ = ShowComingSoonAsync("NewConversation_NewContact.Text"));
            NewCommunityCommand = new RelayCommand(() => _ = ShowComingSoonAsync("NewConversation_NewCommunity.Text"));
            PhoneNumberCommand = new RelayCommand(() => _ = OpenPhoneDialogAsync());
            NavigateToPrivateChatCommand = new RelayCommand(() => _ = NavigateToPrivateChatAsync());
            SelectContactCommand = new RelayCommand<ChatItem>(chat => _ = OpenContactAsync(chat));
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
                    RebuildCatalog();
                }
                else
                {
                    UnhookCatalog();
                    SearchQuery = string.Empty;
                }
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

        private void ScheduleRebuild()
        {
            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(RebuildCatalog);
                return;
            }

            RebuildCatalog();
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

        private async Task ShowComingSoonAsync(string titleKey)
        {
            string title = _strings?.Get(titleKey, "Coming soon") ?? "Coming soon";
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
