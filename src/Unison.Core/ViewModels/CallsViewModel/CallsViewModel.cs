// =============================================================================
// CallsViewModel
//
// Calls tab: Favourites preview + Recents from ICallLogService (one row per JID/LID).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public sealed class CallsViewModel : INotifyPropertyChanged
    {
        public const int FavoritePreviewCap = CallFavoritePreview.DefaultCap;

        private readonly ChatStateStore _chatState;
        private readonly IChatService _chats;
        private readonly ICallLogService _callLog;
        private readonly IPersonStore _people;
        private readonly IJidResolver _jids;
        private readonly IStringResources _strings;
        private readonly IDispatcher _ui;

        private ObservableCollection<CallFavoriteItem> _favoritePreview =
            new ObservableCollection<CallFavoriteItem>();
        private ObservableCollection<CallRecentItem> _recents =
            new ObservableCollection<CallRecentItem>();
        private ObservableCollection<CallHistoryEntry> _selectedHistory =
            new ObservableCollection<CallHistoryEntry>();
        private ObservableCollection<CallHistoryEntry> _selectedHistoryRest =
            new ObservableCollection<CallHistoryEntry>();
        private CallHistoryEntry _selectedLatest;
        private bool _hasMoreFavorites;
        private bool _isShowingAllFavorites;
        private bool _hasFavorites;
        private bool _hasRecents;
        private bool _isCallDetailsOpen;
        private CallDetailsSubject _selectedSubject;

        public CallsViewModel(
            ChatStateStore chatState,
            IChatService chats,
            IStringResources strings,
            IDispatcher ui,
            ICallLogService callLog = null,
            IPersonStore people = null,
            IJidResolver jids = null)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _strings = strings;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _callLog = callLog;
            _people = people;
            _jids = jids;

            OpenNewCallCommand = new RelayCommand(() => NewCallRequested?.Invoke(this, EventArgs.Empty));
            OpenMenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            ShowAllFavoritesCommand = new RelayCommand(ShowAllFavorites);
            SelectFavoriteCommand = new RelayCommand<CallFavoriteItem>(SelectFavorite);
            SelectRecentCommand = new RelayCommand<CallRecentItem>(SelectRecent);
            CloseDetailsCommand = new RelayCommand(CloseDetails);
            OpenChatCommand = new RelayCommand(OpenSelectedChat, CanOpenSelectedChat);

            _chatState.Chats.CollectionChanged += OnChatsCollectionChanged;
            _chats.FavoritesMembershipChanged += OnFavoritesMembershipChanged;
            if (_callLog != null)
            {
                _callLog.Changed += OnCallLogChanged;
            }

            foreach (ChatItem chat in _chatState.Chats)
            {
                AttachChat(chat);
            }

            RebuildFavorites();
            RebuildRecents();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public event EventHandler MenuRequested;

        public event EventHandler NewCallRequested;

        /// <summary>Payload is the contact JID to open in Chats.</summary>
        public event EventHandler<string> OpenChatRequested;

        public ObservableCollection<CallFavoriteItem> FavoritePreview
        {
            get => _favoritePreview;
            private set
            {
                if (ReferenceEquals(_favoritePreview, value))
                {
                    return;
                }

                _favoritePreview = value;
                OnPropertyChanged(nameof(FavoritePreview));
            }
        }

        public ObservableCollection<CallRecentItem> Recents
        {
            get => _recents;
            private set
            {
                if (ReferenceEquals(_recents, value))
                {
                    return;
                }

                _recents = value;
                OnPropertyChanged(nameof(Recents));
                HasRecents = _recents != null && _recents.Count > 0;
            }
        }

        public bool HasMoreFavorites
        {
            get => _hasMoreFavorites;
            private set
            {
                if (_hasMoreFavorites == value)
                {
                    return;
                }

                _hasMoreFavorites = value;
                OnPropertyChanged(nameof(HasMoreFavorites));
                OnPropertyChanged(nameof(ShowFavoritesSeeAll));
            }
        }

        public bool IsShowingAllFavorites
        {
            get => _isShowingAllFavorites;
            private set
            {
                if (_isShowingAllFavorites == value)
                {
                    return;
                }

                _isShowingAllFavorites = value;
                OnPropertyChanged(nameof(IsShowingAllFavorites));
                OnPropertyChanged(nameof(ShowFavoritesSeeAll));
            }
        }

        public bool ShowFavoritesSeeAll => HasMoreFavorites && !IsShowingAllFavorites;

        public bool HasFavorites
        {
            get => _hasFavorites;
            private set
            {
                if (_hasFavorites == value)
                {
                    return;
                }

                _hasFavorites = value;
                OnPropertyChanged(nameof(HasFavorites));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowFavoritesSection));
            }
        }

        public bool HasRecents
        {
            get => _hasRecents;
            private set
            {
                if (_hasRecents == value)
                {
                    return;
                }

                _hasRecents = value;
                OnPropertyChanged(nameof(HasRecents));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowRecentsSection));
            }
        }

        public bool ShowFavoritesSection => HasFavorites;

        public bool ShowRecentsSection => HasRecents || HasFavorites;

        public bool ShowEmptyState => !HasFavorites && !HasRecents;

        public bool IsCallDetailsOpen
        {
            get => _isCallDetailsOpen;
            private set
            {
                if (_isCallDetailsOpen == value)
                {
                    return;
                }

                _isCallDetailsOpen = value;
                OnPropertyChanged(nameof(IsCallDetailsOpen));
            }
        }

        public CallDetailsSubject SelectedSubject
        {
            get => _selectedSubject;
            private set
            {
                if (ReferenceEquals(_selectedSubject, value))
                {
                    return;
                }

                _selectedSubject = value;
                OnPropertyChanged(nameof(SelectedSubject));
                (OpenChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ObservableCollection<CallHistoryEntry> SelectedHistory
        {
            get => _selectedHistory;
            private set
            {
                if (ReferenceEquals(_selectedHistory, value))
                {
                    return;
                }

                _selectedHistory = value;
                OnPropertyChanged(nameof(SelectedHistory));
            }
        }

        /// <summary>Newest entry for the open contact — shown above HISTORY.</summary>
        public CallHistoryEntry SelectedLatest
        {
            get => _selectedLatest;
            private set
            {
                if (ReferenceEquals(_selectedLatest, value))
                {
                    return;
                }

                _selectedLatest = value;
                OnPropertyChanged(nameof(SelectedLatest));
                OnPropertyChanged(nameof(HasSelectedLatest));
            }
        }

        public bool HasSelectedLatest => _selectedLatest != null;

        /// <summary>Older entries under the HISTORY subheader (flat, newest-first).</summary>
        public ObservableCollection<CallHistoryEntry> SelectedHistoryRest
        {
            get => _selectedHistoryRest;
            private set
            {
                if (ReferenceEquals(_selectedHistoryRest, value))
                {
                    return;
                }

                _selectedHistoryRest = value;
                OnPropertyChanged(nameof(SelectedHistoryRest));
                OnPropertyChanged(nameof(HasSelectedHistoryRest));
            }
        }

        public bool HasSelectedHistoryRest =>
            _selectedHistoryRest != null && _selectedHistoryRest.Count > 0;

        public string HistorySectionTitle =>
            _strings?.Get("Calls_HistorySection", "HISTORY") ?? "HISTORY";

        public ICommand OpenNewCallCommand { get; }

        public ICommand OpenMenuCommand { get; }

        public ICommand ShowAllFavoritesCommand { get; }

        public ICommand SelectFavoriteCommand { get; }

        public ICommand SelectRecentCommand { get; }

        public ICommand CloseDetailsCommand { get; }

        public ICommand OpenChatCommand { get; }

        public string DirectionLabel(CallDirection direction)
        {
            switch (direction)
            {
                case CallDirection.Outgoing:
                    return _strings?.Get("Calls_Outgoing", "Outgoing") ?? "Outgoing";
                case CallDirection.Incoming:
                    return _strings?.Get("Calls_Incoming", "Incoming") ?? "Incoming";
                case CallDirection.Missed:
                    return _strings?.Get("Calls_Missed", "Missed") ?? "Missed";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Test / host helper: replace recents without protocol wiring.</summary>
        public void SetRecentsForTest(IEnumerable<CallRecentItem> items)
        {
            Recents = new ObservableCollection<CallRecentItem>(items ?? Array.Empty<CallRecentItem>());
        }

        private void ShowAllFavorites()
        {
            IsShowingAllFavorites = true;
            RebuildFavorites();
        }

        private void SelectFavorite(CallFavoriteItem item)
        {
            if (item == null)
            {
                return;
            }

            SelectedSubject = new CallDetailsSubject
            {
                Jid = item.Jid,
                DisplayName = item.DisplayName,
                AvatarUrl = item.AvatarUrl,
                IsGroup = item.Chat != null && item.Chat.IsGroup
            };
            LoadSelectedHistory(item.Jid);
            IsCallDetailsOpen = true;
        }

        private void SelectRecent(CallRecentItem item)
        {
            if (item == null)
            {
                return;
            }

            SelectedSubject = new CallDetailsSubject
            {
                Jid = item.Jid,
                DisplayName = item.DisplayName,
                AvatarUrl = item.AvatarUrl,
                IsGroup = item.IsGroup
            };
            LoadSelectedHistory(item.Jid);
            IsCallDetailsOpen = true;
        }

        private void CloseDetails()
        {
            IsCallDetailsOpen = false;
            SelectedSubject = null;
            SelectedHistory = new ObservableCollection<CallHistoryEntry>();
            SelectedHistoryRest = new ObservableCollection<CallHistoryEntry>();
            SelectedLatest = null;
        }

        private bool CanOpenSelectedChat()
        {
            return SelectedSubject != null && !string.IsNullOrWhiteSpace(SelectedSubject.Jid);
        }

        private void OpenSelectedChat()
        {
            if (!CanOpenSelectedChat())
            {
                return;
            }

            OpenChatRequested?.Invoke(this, SelectedSubject.Jid);
        }

        private void OnCallLogChanged(object sender, EventArgs e)
        {
            _ = _ui.RunAsync(() =>
            {
                RebuildRecents();
                if (SelectedSubject != null)
                {
                    LoadSelectedHistory(SelectedSubject.Jid);
                }
            });
        }

        private void RebuildRecents()
        {
            if (_callLog == null)
            {
                Recents = new ObservableCollection<CallRecentItem>();
                return;
            }

            IReadOnlyList<CallRecentItem> rows = _callLog.GetRecents();
            var enriched = new List<CallRecentItem>();
            foreach (CallRecentItem row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                EnrichRecentIdentity(row);
                if (string.IsNullOrWhiteSpace(row.CategoryLabel))
                {
                    row.CategoryLabel = DirectionLabel(row.Direction);
                }

                if (string.IsNullOrEmpty(row.DirectionGlyph))
                {
                    row.DirectionGlyph = CallDirectionGlyphs.For(row.Direction);
                }

                enriched.Add(row);
            }

            Recents = new ObservableCollection<CallRecentItem>(enriched);
        }

        private void EnrichRecentIdentity(CallRecentItem row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Jid))
            {
                return;
            }

            string jid = _jids != null ? _jids.GetCanonicalJid(row.Jid) : row.Jid;
            if (!string.IsNullOrWhiteSpace(jid))
            {
                row.Jid = jid;
            }

            ChatItem chat = FindChatFlexible(row.Jid);
            if (chat != null)
            {
                if (!string.IsNullOrWhiteSpace(chat.Name))
                {
                    row.DisplayName = chat.Name;
                }

                if (string.IsNullOrWhiteSpace(row.AvatarUrl))
                {
                    row.AvatarUrl = chat.AvatarUrl ?? chat.AvatarHighUrl;
                }

                row.IsGroup = chat.IsGroup;
            }

            if (string.IsNullOrWhiteSpace(row.DisplayName) && _people != null)
            {
                Person person = _people.TryGetCached(row.Jid);
                if (person == null)
                {
                    string alias;
                    if (_jids != null && _jids.TryGetAlias(row.Jid, out alias))
                    {
                        person = _people.TryGetCached(alias);
                    }
                }

                if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                {
                    row.DisplayName = person.Name;
                    if (string.IsNullOrWhiteSpace(row.AvatarUrl))
                    {
                        row.AvatarUrl = person.AvatarUrl;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(row.DisplayName))
            {
                string phone = JidHelper.TryPhoneFromJid(row.Jid);
                row.DisplayName = !string.IsNullOrWhiteSpace(phone) ? phone : row.Jid;
            }
        }

        private ChatItem FindChatFlexible(string jid)
        {
            ChatItem chat = _chatState.FindChat(jid);
            if (chat != null)
            {
                return chat;
            }

            string alias;
            if (_jids != null && _jids.TryGetAlias(jid, out alias) && !string.IsNullOrWhiteSpace(alias))
            {
                return _chatState.FindChat(alias);
            }

            return null;
        }

        private void LoadSelectedHistory(string jid)
        {
            if (_callLog == null || string.IsNullOrWhiteSpace(jid))
            {
                SelectedHistory = new ObservableCollection<CallHistoryEntry>();
                SelectedHistoryRest = new ObservableCollection<CallHistoryEntry>();
                SelectedLatest = null;
                return;
            }

            IReadOnlyList<CallHistoryEntry> history = _callLog.GetHistory(jid);
            foreach (CallHistoryEntry entry in history)
            {
                if (entry == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.CategoryLabel))
                {
                    entry.CategoryLabel = DirectionLabel(entry.Direction);
                }

                if (string.IsNullOrEmpty(entry.DirectionGlyph))
                {
                    entry.DirectionGlyph = CallDirectionGlyphs.For(entry.Direction);
                }
            }

            SelectedHistory = new ObservableCollection<CallHistoryEntry>(history);
            if (history.Count == 0)
            {
                SelectedLatest = null;
                SelectedHistoryRest = new ObservableCollection<CallHistoryEntry>();
                return;
            }

            SelectedLatest = history[0];
            var rest = new List<CallHistoryEntry>();
            for (int i = 1; i < history.Count; i++)
            {
                if (history[i] != null)
                {
                    rest.Add(history[i]);
                }
            }

            SelectedHistoryRest = new ObservableCollection<CallHistoryEntry>(rest);
        }

        private void OnChatsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ChatItem chat in e.OldItems.OfType<ChatItem>())
                {
                    DetachChat(chat);
                }
            }

            if (e.NewItems != null)
            {
                foreach (ChatItem chat in e.NewItems.OfType<ChatItem>())
                {
                    AttachChat(chat);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (ChatItem chat in _chatState.Chats)
                {
                    AttachChat(chat);
                }
            }

            ScheduleRebuildFavorites();
        }

        private void AttachChat(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.PropertyChanged += OnChatPropertyChanged;
        }

        private void DetachChat(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            chat.PropertyChanged -= OnChatPropertyChanged;
        }

        private void OnChatPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(ChatItem.IsFavorite), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(ChatItem.Name), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, "DisplayName", StringComparison.Ordinal))
            {
                ScheduleRebuildFavorites();
            }
        }

        private void OnFavoritesMembershipChanged(object sender, EventArgs e)
        {
            ScheduleRebuildFavorites();
        }

        private void ScheduleRebuildFavorites()
        {
            _ = _ui.RunAsync(RebuildFavorites);
        }

        private void RebuildFavorites()
        {
            bool hasMore;
            IReadOnlyList<ChatItem> take = CallFavoritePreview.SelectOrdered(
                _chatState.Chats,
                _strings,
                FavoritePreviewCap,
                IsShowingAllFavorites,
                out hasMore);

            HasFavorites = take.Count > 0 || _chatState.Chats.Any(c => c != null && c.IsFavorite);
            HasMoreFavorites = hasMore;

            FavoritePreview = new ObservableCollection<CallFavoriteItem>(
                take.Select(c => new CallFavoriteItem(c, _strings)));
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
