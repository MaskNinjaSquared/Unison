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
        private readonly IChatMessageVmFactory _messageVmFactory;
        private readonly IWhatsAppService _whatsApp;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly ChatItem _source;
        private readonly bool _isGroup;
        private int _rebuildBusy;
        private bool _rebuildAgain;

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
            IWhatsAppService whatsApp = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _isGroup = isGroup;
            _shortcutService = shortcutService;
            _chatStore = chatStore;
            _chatService = chatService;
            _messageStore = messageStore;
            _messageVmFactory = messageVmFactory;
            _whatsApp = whatsApp;
            _dispatcher = dispatcher;
            _strings = strings;

            _source.PropertyChanged += Source_PropertyChanged;
            if (_whatsApp != null)
            {
                _whatsApp.OnChatMessagesChanged += WhatsApp_ChatMessagesChanged;
            }

            PinToStartCommand = new RelayCommand(
                () => _ = ToggleWidgetPinAsync(),
                () => _source != null &&
                      !string.IsNullOrWhiteSpace(_source.JID) &&
                      _shortcutService != null &&
                      _chatStore != null);

            PinChatCommand = new RelayCommand(
                () => _ = ToggleChatPinAsync(),
                () => _chatService != null && !string.IsNullOrWhiteSpace(_source?.JID));

            SetNotificationsCommand = new RelayCommand<bool>(
                enabled => _ = SetNotificationsEnabledAsync(enabled),
                _ => _chatStore != null && !string.IsNullOrWhiteSpace(_source?.JID));

            RefreshDerived();
            _ = RebuildFilteredAsync();
            if (_whatsApp != null)
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

        public bool HasMedia => MediaItems.Count > 0;

        public bool HasFiles => FileItems.Count > 0;

        public ChatItem Source => _source;

        public bool IsGroup => _isGroup;

        public bool IsUser => !_isGroup;

        public string AvatarUrl => _source.GetAvatarUrl(preferHigh: true);

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

        /// <summary>True when the chat is not muted — notifications are delivered.</summary>
        public bool NotificationsEnabled => !_source.IsMutedLocally;

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

        public void Detach()
        {
            _source.PropertyChanged -= Source_PropertyChanged;
            if (_whatsApp != null)
            {
                _whatsApp.OnChatMessagesChanged -= WhatsApp_ChatMessagesChanged;
            }
        }

        private void WhatsApp_ChatMessagesChanged(object sender, string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
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

            _ = RebuildFilteredAsync();
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
                    List<ChatMessage> loaded = null;
                    if (_messageStore != null && !string.IsNullOrWhiteSpace(_source.JID))
                    {
                        try
                        {
                            loaded = await _messageStore.LoadMessagesAsync(_source.JID);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[ChatDetailInfoViewModel] Load messages failed: " + ex.Message);
                        }
                    }

                    var media = new List<ChatMessageViewModel>();
                    var files = new List<ChatMessageViewModel>();
                    if (loaded != null && _messageVmFactory != null)
                    {
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

                            if (IsMediaKind(message.Kind) || message.IsAudio || message.IsVideo || message.IsImage)
                            {
                                media.Add(_messageVmFactory.Create(message));
                            }
                            else if (message.Kind == ChatMessageKind.Document)
                            {
                                files.Add(_messageVmFactory.Create(message));
                            }
                        }
                    }

                    Action apply = () => ReplaceCollection(MediaItems, media, nameof(HasMedia));
                    Action applyFiles = () => ReplaceCollection(FileItems, files, nameof(HasFiles));
                    if (_dispatcher != null)
                    {
                        await _dispatcher.RunAsync(() =>
                        {
                            apply();
                            applyFiles();
                        });
                    }
                    else
                    {
                        apply();
                        applyFiles();
                    }
                }
                while (_rebuildAgain);
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildBusy, 0);
                if (_rebuildAgain)
                {
                    _ = RebuildFilteredAsync();
                }
            }
        }

        private void ReplaceCollection(
            ObservableCollection<ChatMessageViewModel> target,
            List<ChatMessageViewModel> next,
            string hasPropertyName)
        {
            target.Clear();
            foreach (var item in next)
            {
                target.Add(item);
            }

            OnPropertyChanged(hasPropertyName);
            OnPropertyChanged(ReferenceEquals(target, MediaItems) ? nameof(MediaItems) : nameof(FileItems));
        }

        private static bool IsMediaKind(ChatMessageKind kind)
        {
            return kind == ChatMessageKind.Image ||
                   kind == ChatMessageKind.Video ||
                   kind == ChatMessageKind.Audio ||
                   kind == ChatMessageKind.Voice;
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
            else if (e.PropertyName == nameof(ChatItem.AvatarUrl) ||
                     e.PropertyName == nameof(ChatItem.AvatarHighUrl))
            {
                OnPropertyChanged(nameof(AvatarUrl));
            }
            else if (e.PropertyName == nameof(ChatItem.GroupMemberCount))
            {
                OnPropertyChanged(nameof(HasMembersCount));
                OnPropertyChanged(nameof(MembersCountText));
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
            OnPropertyChanged(nameof(StatusOrDescription));
            OnPropertyChanged(nameof(HasStatusOrDescription));
            OnPropertyChanged(nameof(HasMembersCount));
            OnPropertyChanged(nameof(MembersCountText));
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
