using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
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
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly ChatItem _source;
        private readonly bool _isGroup;

        private string _notificationsValue;
        private string _pinMenuLabel;

        public ChatDetailInfoViewModel(
            ChatItem source,
            bool isGroup,
            IShortcutService shortcutService,
            IChatStore chatStore,
            IDispatcher dispatcher,
            IStringResources strings)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _isGroup = isGroup;
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _dispatcher = dispatcher;
            _strings = strings;

            _source.PropertyChanged += Source_PropertyChanged;

            PinToStartCommand = new RelayCommand(
                () => _ = ToggleWidgetPinAsync(),
                () => _source != null &&
                      !string.IsNullOrWhiteSpace(_source.JID) &&
                      _shortcutService != null &&
                      _chatStore != null);

            RefreshDerived();
        }

        public ChatItem Source => _source;

        public bool IsGroup => _isGroup;

        public bool IsUser => !_isGroup;

        public string AvatarUrl => _source.AvatarUrl;

        public string DisplayName =>
            _source.GetNameResolved(_strings) ?? string.Empty;

        public string PhoneValue
        {
            get
            {
                if (_isGroup)
                {
                    return string.Empty;
                }

                return JidHelper.TryPhoneFromJid(_source.JID) ?? string.Empty;
            }
        }

        public bool HasPhone => !string.IsNullOrWhiteSpace(PhoneValue);

        /// <summary>Contact about / group description — local-only placeholder until synced.</summary>
        public string StatusOrDescription => string.Empty;

        public bool HasStatusOrDescription => !string.IsNullOrWhiteSpace(StatusOrDescription);

        public string NotificationsValue
        {
            get => _notificationsValue;
            private set => Set(ref _notificationsValue, value);
        }

        public string PinMenuLabel
        {
            get => _pinMenuLabel;
            private set => Set(ref _pinMenuLabel, value);
        }

        public string ProfilePivotHeader =>
            _isGroup
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

        public ICommand PinToStartCommand { get; }

        public void Detach()
        {
            _source.PropertyChanged -= Source_PropertyChanged;
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
            }
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl))
            {
                OnPropertyChanged(nameof(AvatarUrl));
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
        }

        private void RefreshDerived()
        {
            RefreshNotifications();
            RefreshPinLabel();
        }

        private void RefreshNotifications()
        {
            NotificationsValue = _source.IsMutedLocally
                ? Loc("ChatDetailInfo_NotificationsOff", "Off")
                : Loc("ChatDetailInfo_NotificationsOn", "On");
        }

        private void RefreshPinLabel()
        {
            PinMenuLabel = _source.IsWidgetPinned
                ? Loc("ChatDetailInfo_UnpinAppBarLabel", "Unpin from\nStart")
                : Loc("ChatDetailInfo_PinAppBarLabel", "Pin to\nStart");
        }

        private void RaiseDisplayProperties()
        {
            OnPropertyChanged(nameof(AvatarUrl));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PhoneValue));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(StatusOrDescription));
            OnPropertyChanged(nameof(HasStatusOrDescription));
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
