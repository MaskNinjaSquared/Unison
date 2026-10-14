using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy shell: list column (pivot + frame) + detail host appended only while a chat is open.
    /// </summary>
    public sealed partial class ShellView : Page
    {
        private const double NarrowWindowWidth = 720;
        private const double WideListWidth = 380;

        private ShellViewModel _shell;
        private INavigator _navigator;
        private IDialogService _dialogs;
        private INewChatDialogViewModelFactory _newChatFactory;
        private IMessageService _messages;
        private IChatStateStore _chatState;
        private IStringResources _strings;
        private bool _pivotReady;
        private string _loadedSection;
        private bool _searchState;
        private bool _suppressSearchBoxCallback;
        private ChatListView _hookedList;
        private ChatsView _hookedChats;
        private ArchivedChatsView _hookedArchived;
        private bool _detailOpen;
        private bool _detailHooked;

        public ShellView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;

            if (App.Services != null)
            {
                _shell = App.Services.GetRequiredService<ShellViewModel>();
                _navigator = App.Services.GetRequiredService<INavigator>();
                _dialogs = App.Services.GetService<IDialogService>();
                _newChatFactory = App.Services.GetService<INewChatDialogViewModelFactory>();
                _messages = App.Services.GetService<IMessageService>();
                _chatState = App.Services.GetService<IChatStateStore>();
                _strings = App.Services.GetService<IStringResources>();
                DataContext = _shell;
            }

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _navigator?.AttachShellFrame(ContentFrame);
            _pivotReady = true;
            ApplyPivotHeaders();
            EnsureBrandUppercase();
            ReportNarrowFromSize(ActualWidth);
            HookDetail();
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += ShellView_BackRequested;
            LoadSection(ReadSelectedTag(), force: true);
            ApplySearchState(false);
            ApplyShellChrome();
            ApplyDetailLayout();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
            }

            UnhookListChrome();
            UnhookDetail();
            SystemNavigationManager.GetForCurrentView().BackRequested -= ShellView_BackRequested;
        }

        private void HookDetail()
        {
            if (_detailHooked || ShellChatDetail == null)
            {
                return;
            }

            ShellChatDetail.BackRequested += ShellChatDetail_BackRequested;
            _detailHooked = true;
        }

        private void UnhookDetail()
        {
            if (!_detailHooked || ShellChatDetail == null)
            {
                return;
            }

            ShellChatDetail.BackRequested -= ShellChatDetail_BackRequested;
            _detailHooked = false;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ReportNarrowFromSize(e.NewSize.Width);
            ApplyDetailLayout();
        }

        private void ReportNarrowFromSize(double width)
        {
            if (_shell == null || width <= 0)
            {
                return;
            }

            _shell.ReportWindowNarrow(width < NarrowWindowWidth);
        }

        private void ShellView_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_searchState)
            {
                e.Handled = true;
                ExitSearchState();
                return;
            }

            if (_detailOpen)
            {
                e.Handled = true;
                _ = CloseDetailAsync();
                return;
            }

            if (ContentFrame?.Content is SettingsView settings)
            {
                e.Handled = true;
                if (settings.DataContext is SettingsViewModel vm &&
                    vm.LeaveCommand != null &&
                    vm.LeaveCommand.CanExecute(null))
                {
                    vm.LeaveCommand.Execute(null);
                }
                else
                {
                    _navigator?.NavigateInShell(NavigationDestination.Chats);
                }

                return;
            }

            if (ContentFrame?.Content is IConversationShellPage conversation &&
                conversation.TryHandleBack())
            {
                e.Handled = true;
                return;
            }

            if (ContentFrame?.Content is StatusView status && status.TryHandleBack())
            {
                e.Handled = true;
            }
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.HidesLegacyShellChrome) ||
                e.PropertyName == nameof(ShellViewModel.ChatPane) ||
                e.PropertyName == nameof(ShellViewModel.IsNarrowWindow) ||
                e.PropertyName == nameof(ShellViewModel.ShowSystemBackButton) ||
                e.PropertyName == nameof(ShellViewModel.HasActiveChat))
            {
                ApplyShellChrome();
                ApplyDetailLayout();
            }
        }

        private void ApplyShellChrome()
        {
            bool settingsPage = ContentFrame?.Content is SettingsView;
            bool hideChrome = settingsPage || (_shell != null && _shell.HidesLegacyShellChrome);
            if (hideChrome && _searchState)
            {
                ExitSearchState();
            }

            if (hideChrome)
            {
                BrandHeader.Visibility = Visibility.Collapsed;
                SearchHeader.Visibility = Visibility.Collapsed;
                SectionPivot.Visibility = Visibility.Collapsed;
            }
            else if (_searchState)
            {
                BrandHeader.Visibility = Visibility.Collapsed;
                SearchHeader.Visibility = Visibility.Visible;
                SectionPivot.Visibility = Visibility.Collapsed;
            }
            else
            {
                BrandHeader.Visibility = Visibility.Visible;
                SearchHeader.Visibility = Visibility.Collapsed;
                SectionPivot.Visibility = Visibility.Visible;
            }

            try
            {
                bool showBack = hideChrome || _searchState || _detailOpen || settingsPage ||
                    (_shell != null && _shell.ShowSystemBackButton);
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    showBack
                        ? AppViewBackButtonVisibility.Visible
                        : AppViewBackButtonVisibility.Collapsed;
            }
            catch
            {
            }
        }

        private void ApplyDetailLayout()
        {
            bool narrow = _shell == null || _shell.IsNarrowWindow;
            if (!_detailOpen)
            {
                DetailColumn.Width = new GridLength(0);
                ListColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailHost.Visibility = Visibility.Collapsed;
                ListChromeRoot.Visibility = Visibility.Visible;
                return;
            }

            DetailHost.Visibility = Visibility.Visible;
            if (narrow)
            {
                // Fullscreen chat: list column gone, pivot hidden via HidesLegacyShellChrome.
                ListColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ListChromeRoot.Visibility = Visibility.Collapsed;
            }
            else
            {
                ListChromeRoot.Visibility = Visibility.Visible;
                ListColumn.Width = new GridLength(WideListWidth);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private async void ShellChatDetail_BackRequested(object sender, EventArgs e)
        {
            await CloseDetailAsync();
        }

        private async Task OpenDetailAsync(ChatItem chat)
        {
            if (chat == null || ShellChatDetail == null)
            {
                return;
            }

            try
            {
                await ShellChatDetail.PrepareActiveChatAsync(chat);
                if (!ShellChatDetail.HasActiveChat)
                {
                    return;
                }

                _shell?.SelectChat(chat);
                _shell?.ReportActiveChat(true);
                _detailOpen = true;
                ApplyDetailLayout();
                ApplyShellChrome();
                await ShellChatDetail.CompleteActiveChatLoadAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppLegacy] OpenDetail: " + ex.Message);
            }
        }

        private async Task CloseDetailAsync()
        {
            try
            {
                if (ShellChatDetail != null)
                {
                    await ShellChatDetail.SetActiveChatAsync(null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppLegacy] CloseDetail: " + ex.Message);
            }

            if (ContentFrame?.Content is ChatsView chats)
            {
                chats.ListChrome?.ClearSelection();
            }
            else if (ContentFrame?.Content is ArchivedChatsView archived)
            {
                archived.ListChrome?.ClearSelection();
            }

            _shell?.ClearChat();
            _detailOpen = false;
            ApplyDetailLayout();
            ApplyShellChrome();
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            HookListChromeFromContent();
            ApplyShellChrome();
        }

        private void HookListChromeFromContent()
        {
            UnhookListChrome();

            if (ContentFrame?.Content is ChatsView chats)
            {
                _hookedChats = chats;
                _hookedList = chats.ListChrome;
                chats.ChatOpenRequested += Chats_ChatOpenRequested;
            }
            else if (ContentFrame?.Content is ArchivedChatsView archived)
            {
                _hookedArchived = archived;
                _hookedList = archived.ListChrome;
                archived.ChatOpenRequested += Archived_ChatOpenRequested;
            }

            if (_hookedList != null)
            {
                _hookedList.SearchRequested += List_SearchRequested;
                _hookedList.NewChatRequested += List_NewChatRequested;
                _hookedList.ProfileRequested += List_ProfileRequested;
                _hookedList.DebugRequested += List_DebugRequested;
                _hookedList.SettingsRequested += List_SettingsRequested;
            }
        }

        private void UnhookListChrome()
        {
            if (_hookedChats != null)
            {
                _hookedChats.ChatOpenRequested -= Chats_ChatOpenRequested;
                _hookedChats = null;
            }

            if (_hookedArchived != null)
            {
                _hookedArchived.ChatOpenRequested -= Archived_ChatOpenRequested;
                _hookedArchived = null;
            }

            if (_hookedList != null)
            {
                _hookedList.SearchRequested -= List_SearchRequested;
                _hookedList.NewChatRequested -= List_NewChatRequested;
                _hookedList.ProfileRequested -= List_ProfileRequested;
                _hookedList.DebugRequested -= List_DebugRequested;
                _hookedList.SettingsRequested -= List_SettingsRequested;
                _hookedList = null;
            }
        }

        private void Chats_ChatOpenRequested(object sender, ChatSelectedEventArgs e)
        {
            if (e?.SelectedChat == null)
            {
                _ = CloseDetailAsync();
                return;
            }

            _ = OpenDetailAsync(e.SelectedChat);
        }

        private void Archived_ChatOpenRequested(object sender, ChatSelectedEventArgs e)
        {
            Chats_ChatOpenRequested(sender, e);
        }

        private void List_SearchRequested(object sender, EventArgs e) => EnterSearchState();

        private void List_NewChatRequested(object sender, EventArgs e) => _ = StartNewChatAsync();

        private void List_ProfileRequested(object sender, EventArgs e) => ProfileMenu_Click(sender, null);

        private void List_DebugRequested(object sender, EventArgs e) => DebugMenu_Click(sender, null);

        private void List_SettingsRequested(object sender, EventArgs e) => SettingsMenu_Click(sender, null);

        private void EnsureBrandUppercase()
        {
            if (BrandTitle == null)
            {
                return;
            }

            string text = BrandTitle.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = _strings?.Get("Shell_LegacyBrand.Text", "WHATSAPP LEGACY") ?? "WHATSAPP LEGACY";
            }

            BrandTitle.Text = text.ToUpperInvariant();
        }

        private void ApplyPivotHeaders()
        {
            if (SectionPivot?.Items == null)
            {
                return;
            }

            SetPivotHeader(0, "Shell_Chats.Text", "chats");
            SetPivotHeader(1, "Shell_LegacyPivot_Calls.Header", "calls");
            SetPivotHeader(2, "Shell_Status.Text", "status");
            SetPivotHeader(3, "Shell_LegacyPivot_Favorites.Header", "favorites");
            SetPivotHeader(4, "Shell_Archived.Text", "archived");
        }

        private void SetPivotHeader(int index, string resourceKey, string fallback)
        {
            if (index < 0 || index >= SectionPivot.Items.Count)
            {
                return;
            }

            var item = SectionPivot.Items[index] as PivotItem;
            if (item == null)
            {
                return;
            }

            string text = _strings?.Get(resourceKey, fallback);
            if (string.IsNullOrWhiteSpace(text) ||
                text.StartsWith("Shell_", StringComparison.Ordinal))
            {
                text = fallback;
            }

            item.Header = text.ToLowerInvariant();
        }

        private void SectionPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_pivotReady)
            {
                return;
            }

            if (_searchState)
            {
                ExitSearchState();
            }

            LoadSection(ReadSelectedTag(), force: false);
        }

        private string ReadSelectedTag()
        {
            var item = SectionPivot?.SelectedItem as PivotItem;
            return item?.Tag as string ?? "chats";
        }

        private void LoadSection(string tag, bool force)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = "chats";
            }

            if (!force && string.Equals(_loadedSection, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _loadedSection = tag;
            if (_shell != null)
            {
                _shell.ActiveSection = tag;
            }

            if (!string.Equals(tag, "chats", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tag, "archived", StringComparison.OrdinalIgnoreCase) &&
                _detailOpen)
            {
                _ = CloseDetailAsync();
            }

            Type pageType;
            object parameter = null;
            switch (tag)
            {
                case "status":
                    pageType = typeof(StatusView);
                    break;
                case "archived":
                    pageType = typeof(ArchivedChatsView);
                    break;
                case "calls":
                case "favorites":
                    pageType = typeof(LegacyPivotPlaceholderView);
                    parameter = tag;
                    break;
                default:
                    pageType = typeof(ChatsView);
                    break;
            }

            if (ContentFrame.Content != null && ContentFrame.Content.GetType() == pageType && parameter == null)
            {
                HookListChromeFromContent();
                return;
            }

            ContentFrame.Navigate(pageType, parameter);
        }

        private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Debug.WriteLine("[WhatsAppLegacy] Navigate failed: " + e.Exception?.Message);
            e.Handled = true;
        }

        private void CancelSearchButton_Click(object sender, RoutedEventArgs e) => ExitSearchState();

        private void SearchBackButton_Click(object sender, RoutedEventArgs e) => ExitSearchState();

        private void EnterSearchState()
        {
            if (_shell != null && _shell.HidesLegacyShellChrome)
            {
                return;
            }

            if (!string.Equals(_loadedSection, "chats", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_loadedSection, "archived", StringComparison.OrdinalIgnoreCase))
            {
                SectionPivot.SelectedIndex = 0;
            }

            ApplySearchState(true);

            ChatListViewModel listVm = TryGetActiveChatListViewModel();
            _suppressSearchBoxCallback = true;
            try
            {
                ShellSearchBox.Text = listVm?.SearchQuery ?? string.Empty;
            }
            finally
            {
                _suppressSearchBoxCallback = false;
            }

            ShellSearchBox.Focus(FocusState.Programmatic);
            ApplyShellChrome();
        }

        private void ExitSearchState()
        {
            ChatListViewModel listVm = TryGetActiveChatListViewModel();
            if (listVm != null)
            {
                listVm.SearchQuery = string.Empty;
            }

            _suppressSearchBoxCallback = true;
            try
            {
                ShellSearchBox.Text = string.Empty;
            }
            finally
            {
                _suppressSearchBoxCallback = false;
            }

            ApplySearchState(false);
            ApplyShellChrome();
        }

        private void ApplySearchState(bool search)
        {
            _searchState = search;
        }

        private void ShellSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchBoxCallback || !_searchState)
            {
                return;
            }

            ChatListViewModel listVm = TryGetActiveChatListViewModel();
            if (listVm != null)
            {
                listVm.SearchQuery = ShellSearchBox.Text ?? string.Empty;
            }
        }

        private ChatListViewModel TryGetActiveChatListViewModel()
        {
            if (ContentFrame?.Content is ChatsView chats)
            {
                return chats.ChatListViewModel;
            }

            if (ContentFrame?.Content is ArchivedChatsView archived)
            {
                return archived.ChatListViewModel;
            }

            return null;
        }

        private async Task StartNewChatAsync()
        {
            if (_dialogs == null || _newChatFactory == null || _messages == null || _chatState == null)
            {
                return;
            }

            try
            {
                if (!string.Equals(_loadedSection, "chats", StringComparison.OrdinalIgnoreCase))
                {
                    SectionPivot.SelectedIndex = 0;
                }

                string jid = await _dialogs.ShowNewChatDialogAsync(_newChatFactory.Create());
                if (string.IsNullOrEmpty(jid))
                {
                    return;
                }

                bool exists = false;
                foreach (ChatItem chat in _chatState.Chats)
                {
                    if (chat != null && string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    _messages.StartNewChat(jid);
                    await Task.Delay(100);
                }

                _shell?.QueueOpenChatFromActivation("chat=" + Uri.EscapeDataString(jid));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppLegacy] New chat: " + ex.Message);
            }
        }

        private void DebugMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_searchState)
            {
                ExitSearchState();
            }

            _ = CloseDetailAsync();
            _navigator?.NavigateInShell(NavigationDestination.Debug);
            _loadedSection = "debug";
        }

        private void SettingsMenu_Click(object sender, RoutedEventArgs e)
        {
            _navigator?.OpenSettings();
        }

        private void ProfileMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_shell?.OpenProfileSettingsCommand != null &&
                _shell.OpenProfileSettingsCommand.CanExecute(null))
            {
                _shell.OpenProfileSettingsCommand.Execute(null);
                return;
            }

            _navigator?.OpenSettings(true);
        }
    }
}
