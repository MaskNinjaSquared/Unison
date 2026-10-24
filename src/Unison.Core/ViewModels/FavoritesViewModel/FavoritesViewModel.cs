// =============================================================================
// FavoritesViewModel
//
// Favourites tab: empty CTA, A–Z list, and an overlay picker of non-favourite
// chats. Same ChatStateStore as the chats list — membership is ChatItem.IsFavorite.
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
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public sealed class FavoritesViewModel : INotifyPropertyChanged
    {
        private readonly ChatStateStore _chatState;
        private readonly IChatService _chats;
        private readonly IStringResources _strings;
        private readonly IDispatcher _ui;
        private bool _isPickerOpen;
        private bool _hasFavorites;
        private ObservableCollection<AlphabetChatGroup> _groupedFavorites =
            new ObservableCollection<AlphabetChatGroup>();
        private ObservableCollection<ChatItem> _pickerCandidates =
            new ObservableCollection<ChatItem>();

        public FavoritesViewModel(
            ChatStateStore chatState,
            IChatService chats,
            IStringResources strings,
            IDispatcher ui)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _strings = strings;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));

            OpenPickerCommand = new RelayCommand(() => IsPickerOpen = true);
            ClosePickerCommand = new RelayCommand(() => IsPickerOpen = false);
            AddFavoriteCommand = new RelayCommand<ChatItem>(AddFavorite);
            RemoveFavoriteCommand = new RelayCommand<ChatItem>(RemoveFavorite);
            OpenChatCommand = new RelayCommand<ChatItem>(OpenChat);
            OpenMenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));

            _chatState.Chats.CollectionChanged += OnChatsCollectionChanged;
            foreach (ChatItem chat in _chatState.Chats)
            {
                AttachChat(chat);
            }

            Rebuild();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public event EventHandler MenuRequested;

        public event EventHandler<ChatItem> OpenChatRequested;

        public ObservableCollection<AlphabetChatGroup> GroupedFavorites
        {
            get => _groupedFavorites;
            private set
            {
                if (ReferenceEquals(_groupedFavorites, value))
                {
                    return;
                }

                _groupedFavorites = value;
                OnPropertyChanged(nameof(GroupedFavorites));
            }
        }

        public ObservableCollection<ChatItem> PickerCandidates
        {
            get => _pickerCandidates;
            private set
            {
                if (ReferenceEquals(_pickerCandidates, value))
                {
                    return;
                }

                _pickerCandidates = value;
                OnPropertyChanged(nameof(PickerCandidates));
            }
        }

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
            }
        }

        public bool ShowEmptyState => !HasFavorites;

        public bool IsPickerOpen
        {
            get => _isPickerOpen;
            set
            {
                if (_isPickerOpen == value)
                {
                    return;
                }

                _isPickerOpen = value;
                OnPropertyChanged(nameof(IsPickerOpen));
                if (value)
                {
                    RebuildPicker();
                }
            }
        }

        public string SortLabel =>
            _strings?.Get("Favorites_SortAlphabetical.Text", "A-Z") ?? "A-Z";

        public string PickerTitle =>
            _strings?.Get("Favorites_AddPeopleOrGroups.Content", "Add people or groups")
            ?? "Add people or groups";

        public ICommand OpenPickerCommand { get; }

        public ICommand ClosePickerCommand { get; }

        public ICommand AddFavoriteCommand { get; }

        public ICommand RemoveFavoriteCommand { get; }

        public ICommand OpenChatCommand { get; }

        public ICommand OpenMenuCommand { get; }

        private void AddFavorite(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            _ = _chats.SetFavoriteAsync(chat, favorite: true);
            IsPickerOpen = false;
        }

        private void RemoveFavorite(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            _ = _chats.SetFavoriteAsync(chat, favorite: false);
        }

        private void OpenChat(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            OpenChatRequested?.Invoke(this, chat);
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

            ScheduleRebuild();
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
                ScheduleRebuild();
            }
        }

        private void ScheduleRebuild()
        {
            _ = _ui.RunAsync(Rebuild);
        }

        private void Rebuild()
        {
            List<ChatItem> favorites = _chatState.Chats
                .Where(c => c != null && c.IsFavorite)
                .ToList();

            HasFavorites = favorites.Count > 0;
            GroupedFavorites = AlphabetChatGroups.Build(
                favorites,
                c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty);

            if (IsPickerOpen)
            {
                RebuildPicker();
            }
        }

        private void RebuildPicker()
        {
            var candidates = new ObservableCollection<ChatItem>(
                _chatState.Chats
                    .Where(c => c != null && !c.IsFavorite)
                    .OrderBy(
                        c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty,
                        StringComparer.CurrentCultureIgnoreCase));
            PickerCandidates = candidates;
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
