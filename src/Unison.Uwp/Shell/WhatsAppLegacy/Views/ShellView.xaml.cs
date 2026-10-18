using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
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
    /// Legacy shell: pivot hosts each section after its index is opened. The frame is only settings and debug.
    /// </summary>
    public sealed partial class ShellView : Page
    {
        private const double NarrowWindowWidth = 720;

        private bool _splitterDragging;
        private bool _splitterHover;
        private double _dragStartX;
        private double _dragStartListWidth;
        private CoreCursor _previousCursor;

        private ShellViewModel _shell;
        private INavigator _navigator;
        private IDialogService _dialogs;
        private INewChatDialogViewModelFactory _newChatFactory;
        private IChatService _chats;
        private IChatStateStore _chatState;
        private IStringResources _strings;
        private IJidResolver _jids;
        private bool _pivotReady;
        private string _loadedSection;
        private bool _searchState;
        private ChatListView _hookedList;
        private ChatsView _hookedChats;
        private ArchivedChatsView _hookedArchived;
        private LegacyPivotPlaceholderView _hookedPlaceholder;
        private StatusView _hookedStatusBar;
        private bool _detailOpen;
        private bool _detailHooked;
        private bool _overlayOpen;
        private StatusView _statusSection;
        private int _sectionLoadGeneration;
        private readonly bool _isWindowsMobile;

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
                _chats = App.Services.GetService<IChatService>();
                _chatState = App.Services.GetService<IChatStateStore>();
                _strings = App.Services.GetService<IStringResources>();
                _jids = App.Services.GetService<IJidResolver>();
                _isWindowsMobile = App.Services.GetService<ISystemInfoProvider>()?.IsMobile() == true;
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
            // The header strip is created with the template, which can land after this pass.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => InsetPivotHeaders(SectionPivot));
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
            // Overlay first: Settings/Debug sit above the list and any open chat. Checking
            // _detailOpen before them left the hardware back closing the chat underneath
            // while the overlay stayed up (CloseDetailAsync is async and can still be mid-flight).
            if (TryHandleOverlayBack())
            {
                e.Handled = true;
                return;
            }

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

            if (ActivePivotContent() is IConversationShellPage conversation &&
                conversation.TryHandleBack())
            {
                e.Handled = true;
                return;
            }

            if (ActivePivotContent() is StatusView status && status.TryHandleBack())
            {
                e.Handled = true;
            }
        }

        /// <summary>Closes Settings or Debug when either is the active overlay.</summary>
        private bool TryHandleOverlayBack()
        {
            if (ContentFrame?.Content is SettingsView settings)
            {
                if (settings.DataContext is SettingsViewModel vm &&
                    vm.LeaveCommand != null &&
                    vm.LeaveCommand.CanExecute(null))
                {
                    vm.LeaveCommand.Execute(null);
                }
                else
                {
                    DismissOverlay();
                    if (_shell != null)
                    {
                        _shell.ActiveSection = "chats";
                    }
                }

                return true;
            }

            if (_overlayOpen && ContentFrame?.Content is DebugView)
            {
                DismissOverlay();
                return true;
            }

            return false;
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ActiveSection))
            {
                string section = _shell.ActiveSection;
                if (IsPivotSection(section))
                {
                    DismissOverlay();
                    LoadSection(section, force: false);
                }

                return;
            }

            if (e.PropertyName == nameof(ShellViewModel.HidesLegacyShellChrome) ||
                e.PropertyName == nameof(ShellViewModel.HasOpenStatusDetail) ||
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
            bool settingsPage = _overlayOpen && ContentFrame?.Content is SettingsView;
            bool hideChrome = _overlayOpen || (_shell != null && _shell.HidesLegacyShellChrome);
            if (hideChrome && _searchState)
            {
                ExitSearchState();
            }

            if (ContentFrame != null)
            {
                ContentFrame.Visibility = _overlayOpen ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_shell != null && _shell.HidesLegacyShellChrome)
            {
                BrandHeader.Visibility = Visibility.Collapsed;
                SearchHeader.Visibility = Visibility.Collapsed;
                // Chat detail lives in DetailHost — collapse the whole pivot.
                // Status detail lives inside StatusView in the pivot — keep the body, hide tabs.
                if (_shell.HasOpenStatusDetail)
                {
                    SectionPivot.Visibility = Visibility.Visible;
                    SetPivotHeadersVisible(false);
                }
                else
                {
                    SectionPivot.Visibility = Visibility.Collapsed;
                    SetPivotHeadersVisible(true);
                }
            }
            else if (_overlayOpen)
            {
                // Keep the pivot laid out under the opaque overlay so GoBack does not relayout the list.
                BrandHeader.Visibility = Visibility.Collapsed;
                SearchHeader.Visibility = Visibility.Collapsed;
                SectionPivot.Visibility = Visibility.Visible;
                SetPivotHeadersVisible(true);
            }
            else if (_searchState)
            {
                BrandHeader.Visibility = Visibility.Collapsed;
                SearchHeader.Visibility = Visibility.Visible;
                SectionPivot.Visibility = Visibility.Visible;
                SetPivotHeadersVisible(false);
            }
            else
            {
                BrandHeader.Visibility = Visibility.Visible;
                SearchHeader.Visibility = Visibility.Collapsed;
                SectionPivot.Visibility = Visibility.Visible;
                SetPivotHeadersVisible(true);
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
                ListColumn.MinWidth = 0;
                ListColumn.MaxWidth = 10000;
                ListColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailHost.Visibility = Visibility.Collapsed;
                ListChromeRoot.Visibility = Visibility.Visible;
                ApplyListDesktopDivider(narrow);
                return;
            }

            DetailHost.Visibility = Visibility.Visible;
            if (narrow)
            {
                // Fullscreen chat: list column gone, pivot hidden via HidesLegacyShellChrome.
                ListColumn.MinWidth = 0;
                ListColumn.MaxWidth = 10000;
                ListColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ListChromeRoot.Visibility = Visibility.Collapsed;
            }
            else
            {
                ListChromeRoot.Visibility = Visibility.Visible;
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ApplyListWidth(_shell != null
                    ? _shell.ChatListPaneWidth
                    : ChatPaneLayoutConstants.DefaultListWidth);
            }

            ApplyListDesktopDivider(narrow);
            UpdateSplitterPosition();
        }

        /// <summary>Hairline on the list's right edge. Desktop only; the phone list stays full-bleed.</summary>
        private void ApplyListDesktopDivider(bool narrow)
        {
            if (ListDesktopDivider == null)
            {
                return;
            }

            bool listOnScreen = ListChromeRoot != null &&
                ListChromeRoot.Visibility == Visibility.Visible;
            bool sideBySide = !narrow && listOnScreen && _detailOpen;
            ListDesktopDivider.Visibility = sideBySide
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (PaneSplitter != null)
            {
                PaneSplitter.Visibility = sideBySide
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void ApplyListWidth(double desired)
        {
            double max = GetMaxListWidth();
            double width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(max, desired));
            ListColumn.Width = new GridLength(width);
            ListColumn.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            ListColumn.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
        }

        private double GetMaxListWidth()
        {
            double total = RootContentGrid != null ? RootContentGrid.ActualWidth : 0;
            if (total <= 0)
            {
                return ChatPaneLayoutConstants.MaxListWidth;
            }

            double maxFromDetail = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                total - ChatPaneLayoutConstants.MinDetailWidth);
            return Math.Min(ChatPaneLayoutConstants.MaxListWidth, maxFromDetail);
        }

        private void UpdateSplitterPosition()
        {
            if (PaneSplitter == null || PaneSplitter.Visibility != Visibility.Visible)
            {
                return;
            }

            double listWidth = ListColumn.ActualWidth;
            if (listWidth <= 0 && ListColumn.Width.IsAbsolute)
            {
                listWidth = ListColumn.Width.Value;
            }

            double left = Math.Max(0, listWidth - ChatPaneLayoutConstants.SplitterOverlapList);
            PaneSplitter.Margin = new Thickness(left, 0, 0, 0);
        }

        private void UpdateSplitterChrome()
        {
            if (SplitterChrome == null)
            {
                return;
            }

            SplitterChrome.Opacity = (_splitterHover || _splitterDragging) ? 1 : 0;
        }

        private void PaneSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (PaneSplitter.Visibility != Visibility.Visible)
            {
                return;
            }

            _splitterHover = true;
            UpdateSplitterChrome();
            SetResizeCursor();
        }

        private void PaneSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                return;
            }

            _splitterHover = false;
            UpdateSplitterChrome();
            RestoreCursor();
        }

        private void PaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (PaneSplitter.Visibility != Visibility.Visible)
            {
                return;
            }

            _splitterDragging = true;
            _splitterHover = true;
            _dragStartX = e.GetCurrentPoint(RootContentGrid).Position.X;
            _dragStartListWidth = ListColumn.ActualWidth > 0
                ? ListColumn.ActualWidth
                : (ListColumn.Width.IsAbsolute
                    ? ListColumn.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);
            PaneSplitter.CapturePointer(e.Pointer);
            UpdateSplitterChrome();
            SetResizeCursor();
            e.Handled = true;
        }

        private void PaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            double x = e.GetCurrentPoint(RootContentGrid).Position.X;
            ApplyListWidth(_dragStartListWidth + (x - _dragStartX));
            UpdateSplitterPosition();
            e.Handled = true;
        }

        private void PaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            EndSplitterDrag(e.Pointer);
            e.Handled = true;
        }

        private void PaneSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                EndSplitterDrag(null);
            }
        }

        private void EndSplitterDrag(Pointer pointer)
        {
            _splitterDragging = false;
            if (pointer != null)
            {
                try
                {
                    PaneSplitter.ReleasePointerCapture(pointer);
                }
                catch
                {
                }
            }

            double width = ListColumn.ActualWidth > 0
                ? ListColumn.ActualWidth
                : (ListColumn.Width.IsAbsolute
                    ? ListColumn.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);
            width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(GetMaxListWidth(), width));
            if (_shell != null)
            {
                _shell.ChatListPaneWidth = width;
            }

            UpdateSplitterChrome();
            if (!_splitterHover)
            {
                RestoreCursor();
            }
        }

        private void SetResizeCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                if (_previousCursor == null)
                {
                    _previousCursor = window.CoreWindow.PointerCursor;
                }

                window.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 1);
            }
            catch
            {
            }
        }

        private void RestoreCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                window.CoreWindow.PointerCursor = _previousCursor ?? new CoreCursor(CoreCursorType.Arrow, 1);
                _previousCursor = null;
            }
            catch
            {
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
                bool alreadyOpen = _detailOpen &&
                    ShellChatDetail.HasActiveChat &&
                    IsSameConversation(ShellChatDetail.ActiveChatItem, chat);

                await ShellChatDetail.PrepareActiveChatAsync(chat);
                if (!ShellChatDetail.HasActiveChat)
                {
                    return;
                }

                _shell?.SelectChat(chat);
                if (alreadyOpen)
                {
                    // The list re-reports the open chat after every reorder. Loading it again
                    // cleared and rebuilt the whole timeline under the user.
                    return;
                }

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

        private bool IsSameConversation(ChatItem active, ChatItem chat)
        {
            if (active == null || chat == null ||
                string.IsNullOrWhiteSpace(active.JID) || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            string activeJid = _jids?.GetCanonicalJid(active.JID) ?? active.JID;
            string chatJid = _jids?.GetCanonicalJid(chat.JID) ?? chat.JID;
            return string.Equals(activeJid, chatJid, StringComparison.OrdinalIgnoreCase);
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

            if (ActivePivotContent() is ChatsView chats)
            {
                chats.ListChrome?.ClearSelection();
            }
            else if (ActivePivotContent() is ArchivedChatsView archived)
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
            SyncOverlayFromFrame();
        }

        private void SyncOverlayFromFrame()
        {
            object content = ContentFrame?.Content;
            if (IsPivotOverlayContent(content))
            {
                // NavigateToSection(Chats) used to remount the list inside this overlay frame.
                _overlayOpen = false;
                ApplyShellChrome();
                ScheduleOverlayTeardown();
                return;
            }

            _overlayOpen = content is SettingsView || content is DebugView;
            if (_overlayOpen && _detailOpen)
            {
                _ = CloseDetailAsync();
            }

            ApplyShellChrome();
        }

        private static bool IsPivotOverlayContent(object content)
        {
            return content is ChatsView
                || content is ArchivedChatsView
                || content is StatusView
                || content is LegacyPivotPlaceholderView;
        }

        private void HookListChromeFromContent()
        {
            UnhookListChrome();
            object content = ActivePivotContent();

            if (content is ChatsView chats)
            {
                _hookedChats = chats;
                _hookedList = chats.ListChrome;
                chats.ChatOpenRequested += Chats_ChatOpenRequested;
            }
            else if (content is ArchivedChatsView archived)
            {
                _hookedArchived = archived;
                _hookedList = archived.ListChrome;
                archived.ChatOpenRequested += Archived_ChatOpenRequested;
            }
            else if (content is LegacyPivotPlaceholderView placeholder)
            {
                _hookedPlaceholder = placeholder;
                placeholder.SearchRequested += List_SearchRequested;
                placeholder.ProfileRequested += List_ProfileRequested;
                placeholder.DebugRequested += List_DebugRequested;
                placeholder.SettingsRequested += List_SettingsRequested;
            }
            else if (content is StatusView status)
            {
                _hookedStatusBar = status;
                status.SearchRequested += List_SearchRequested;
                status.ProfileRequested += List_ProfileRequested;
                status.DebugRequested += List_DebugRequested;
                status.SettingsRequested += List_SettingsRequested;
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

            if (_hookedPlaceholder != null)
            {
                _hookedPlaceholder.SearchRequested -= List_SearchRequested;
                _hookedPlaceholder.ProfileRequested -= List_ProfileRequested;
                _hookedPlaceholder.DebugRequested -= List_DebugRequested;
                _hookedPlaceholder.SettingsRequested -= List_SettingsRequested;
                _hookedPlaceholder = null;
            }

            if (_hookedStatusBar != null)
            {
                _hookedStatusBar.SearchRequested -= List_SearchRequested;
                _hookedStatusBar.ProfileRequested -= List_ProfileRequested;
                _hookedStatusBar.DebugRequested -= List_DebugRequested;
                _hookedStatusBar.SettingsRequested -= List_SettingsRequested;
                _hookedStatusBar = null;
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

        private static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            string lower = text.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
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

            item.Header = CapitalizeFirst(text);
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

        private static bool IsPivotSection(string tag)
        {
            return string.Equals(tag, "chats", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "calls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "status", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "favorites", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "archived", StringComparison.OrdinalIgnoreCase);
        }

        private object ActivePivotContent()
        {
            ContentPresenter presenter = PresenterFor(ReadSelectedTag());
            return presenter == null ? null : presenter.Content;
        }

        private ContentPresenter PresenterFor(string tag)
        {
            if (string.Equals(tag, "calls", StringComparison.OrdinalIgnoreCase))
            {
                return CallsPresenter;
            }

            if (string.Equals(tag, "status", StringComparison.OrdinalIgnoreCase))
            {
                return StatusPresenter;
            }

            if (string.Equals(tag, "favorites", StringComparison.OrdinalIgnoreCase))
            {
                return FavoritesPresenter;
            }

            if (string.Equals(tag, "archived", StringComparison.OrdinalIgnoreCase))
            {
                return ArchivedPresenter;
            }

            return ChatsPresenter;
        }

        private string ReadSelectedTag()
        {
            var item = SectionPivot?.SelectedItem as PivotItem;
            return item?.Tag as string ?? "chats";
        }

        private async void LoadSection(string tag, bool force)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = "chats";
            }

            if (!IsPivotSection(tag))
            {
                return;
            }

            if (!force && string.Equals(_loadedSection, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int generation = ++_sectionLoadGeneration;
            string previous = _loadedSection;
            _loadedSection = tag;
            SelectPivotIndex(tag);

            if (string.Equals(previous, "status", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tag, "status", StringComparison.OrdinalIgnoreCase) &&
                _statusSection != null)
            {
                await _statusSection.LeaveAsync();
            }

            if (generation != _sectionLoadGeneration)
            {
                return;
            }

            if (!string.Equals(tag, "chats", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tag, "archived", StringComparison.OrdinalIgnoreCase) &&
                _detailOpen)
            {
                await CloseDetailAsync();
            }

            if (generation != _sectionLoadGeneration)
            {
                return;
            }

            await OpenSectionAsync(tag);
            if (generation != _sectionLoadGeneration)
            {
                return;
            }

            if (_shell != null &&
                !string.Equals(_shell.ActiveSection, tag, StringComparison.OrdinalIgnoreCase))
            {
                _shell.ActiveSection = tag;
            }

            HookListChromeFromContent();
            ApplyShellChrome();
        }

        /// <summary>
        /// First open: show spinner, yield so it paints (Mobile needs a real delay), then create.
        /// Revisit: only re-init Status.
        /// </summary>
        private async Task OpenSectionAsync(string tag)
        {
            ContentPresenter presenter = PresenterFor(tag);
            if (presenter == null)
            {
                return;
            }

            if (presenter.Content != null)
            {
                if (presenter.Content is StatusView openStatus)
                {
                    await openStatus.InitAsync(_shell);
                    _statusSection = openStatus;
                }

                return;
            }

            SetSectionLoadingVisible(true);
            try
            {
                await YieldForSectionPaintAsync();
                FrameworkElement section = CreateSectionView(tag);
                presenter.Content = section;
                await InitSectionViewAsync(section, tag);

                var status = section as StatusView;
                if (status != null)
                {
                    _statusSection = status;
                }
            }
            finally
            {
                SetSectionLoadingVisible(false);
            }
        }

        private FrameworkElement CreateSectionView(string tag)
        {
            if (string.Equals(tag, "status", StringComparison.OrdinalIgnoreCase))
            {
                return new StatusView();
            }

            if (string.Equals(tag, "archived", StringComparison.OrdinalIgnoreCase))
            {
                return new ArchivedChatsView();
            }

            if (string.Equals(tag, "calls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "favorites", StringComparison.OrdinalIgnoreCase))
            {
                return new LegacyPivotPlaceholderView();
            }

            return new ChatsView();
        }

        private Task InitSectionViewAsync(FrameworkElement section, string tag)
        {
            if (section is StatusView status)
            {
                return status.InitAsync(_shell);
            }

            if (section is ArchivedChatsView archived)
            {
                return archived.InitAsync(_shell);
            }

            if (section is LegacyPivotPlaceholderView placeholder)
            {
                return placeholder.InitAsync(tag);
            }

            if (section is ChatsView chats)
            {
                return chats.InitAsync(_shell);
            }

            return Task.CompletedTask;
        }

        private void SetSectionLoadingVisible(bool visible)
        {
            if (SectionLoadingOverlay == null)
            {
                return;
            }

            SectionLoadingOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Lets the pivot spinner paint before InitializeComponent / Attach freeze the UI thread.
        /// Mobile needs a real delay — one dispatcher turn is not enough (same as chat detail).
        /// </summary>
        private async Task YieldForSectionPaintAsync()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { });
            if (_isWindowsMobile)
            {
                await Task.Delay(48);
            }
            else
            {
                await Task.Yield();
            }
        }

        private void SelectPivotIndex(string tag)
        {
            if (SectionPivot?.Items == null)
            {
                return;
            }

            for (int i = 0; i < SectionPivot.Items.Count; i++)
            {
                var item = SectionPivot.Items[i] as PivotItem;
                if (item != null &&
                    string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase) &&
                    SectionPivot.SelectedIndex != i)
                {
                    SectionPivot.SelectedIndex = i;
                    return;
                }
            }
        }

        /// <summary>Hides Settings/Debug immediately; the pivot list underneath is already live.</summary>
        internal static void CloseOverlayFrom(DependencyObject source)
        {
            ShellView shell = FindAncestor<ShellView>(source);
            if (shell == null)
            {
                shell = (Window.Current?.Content as Frame)?.Content as ShellView
                    ?? Window.Current?.Content as ShellView;
            }

            if (shell != null)
            {
                shell.DismissOverlay();
                return;
            }

            Debug.WriteLine("[WhatsAppLegacy] Close overlay: shell not found");
        }

        private void DismissOverlay()
        {
            if (!_overlayOpen && (ContentFrame == null || ContentFrame.Content == null))
            {
                return;
            }

            _overlayOpen = false;
            if (ContentFrame != null)
            {
                ContentFrame.Visibility = Visibility.Collapsed;
            }

            ApplyShellChrome();
            ScheduleOverlayTeardown();
        }

        private void ScheduleOverlayTeardown()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, TearDownOverlayContent);
        }

        private void TearDownOverlayContent()
        {
            if (_overlayOpen || ContentFrame == null || ContentFrame.Content == null)
            {
                return;
            }

            try
            {
                ContentFrame.BackStack.Clear();
                ContentFrame.ForwardStack.Clear();
                ContentFrame.Content = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppLegacy] Dismiss overlay: " + ex.Message);
            }
        }

        private static T FindAncestor<T>(DependencyObject start) where T : class
        {
            DependencyObject current = start;
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void SetPivotHeadersVisible(bool visible)
        {
            ApplyPivotHeaderVisibility(SectionPivot, visible);
            if (visible)
            {
                // 10px each side of the header strip only. The section content stays full width.
                InsetPivotHeaders(SectionPivot);
            }
        }

        /// <summary>
        /// Collapse the pivot tab strip (and its clipper) so Minimal status/chat chrome
        /// does not leave an empty header band above the content.
        /// </summary>
        private static void ApplyPivotHeaderVisibility(DependencyObject root, bool visible)
        {
            if (root == null)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var panel = child as PivotHeaderPanel;
                if (panel != null)
                {
                    panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    panel.Height = visible ? double.NaN : 0;
                    panel.Opacity = visible ? 1 : 0;
                    panel.IsHitTestVisible = visible;
                    if (!visible)
                    {
                        panel.Margin = new Thickness(0);
                    }
                }

                var element = child as FrameworkElement;
                if (element != null &&
                    (string.Equals(element.Name, "HeaderClipper", StringComparison.Ordinal) ||
                     string.Equals(element.Name, "StaticHeader", StringComparison.Ordinal) ||
                     string.Equals(element.Name, "Header", StringComparison.Ordinal)))
                {
                    element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    element.Height = visible ? double.NaN : 0;
                }

                ApplyPivotHeaderVisibility(child, visible);
            }
        }

        private static void InsetPivotHeaders(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var panel = child as PivotHeaderPanel;
                if (panel != null)
                {
                    panel.Margin = new Thickness(10, 0, 10, 0);
                }

                InsetPivotHeaders(child);
            }
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null)
                {
                    return match;
                }

                T nested = FindChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Debug.WriteLine("[WhatsAppLegacy] Navigate failed: " + e.Exception?.Message);
            e.Handled = true;
        }

        private void SearchBackButton_Click(object sender, RoutedEventArgs e) => ExitSearchState();

        private void EnterSearchState()
        {
            if (_shell != null && _shell.HidesLegacyShellChrome)
            {
                return;
            }

            ApplySearchState(true);
            ShellSearchBox.Focus(FocusState.Programmatic);
            ApplyShellChrome();
        }

        private void ExitSearchState()
        {
            _shell?.InvokeFromPivotCommand(ReadSelectedTag(), string.Empty, TryGetActiveChatListViewModel());
            ShellSearchBox.Text = string.Empty;
            ApplySearchState(false);
            ApplyShellChrome();
        }

        private void ApplySearchState(bool search)
        {
            _searchState = search;
        }

        private void ShellSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || !_searchState)
            {
                return;
            }

            e.Handled = true;
            _shell?.InvokeFromPivotCommand(
                ReadSelectedTag(),
                ShellSearchBox.Text,
                TryGetActiveChatListViewModel());
        }

        private ChatListViewModel TryGetActiveChatListViewModel()
        {
            if (ActivePivotContent() is ChatsView chats)
            {
                return chats.ChatListViewModel;
            }

            if (ActivePivotContent() is ArchivedChatsView archived)
            {
                return archived.ChatListViewModel;
            }

            return null;
        }

        private async Task StartNewChatAsync()
        {
            if (_dialogs == null || _newChatFactory == null || _chats == null || _chatState == null)
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
                    _chats.StartNewChat(jid);
                    await Task.Delay(100);
                }

                _shell?.QueueOpenChatFromActivation("chat=" + Uri.EscapeDataString(jid));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppLegacy] New chat: " + ex.Message);
            }
        }

        private async void DebugMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_searchState)
            {
                ExitSearchState();
            }

            await CloseDetailAsync();
            _navigator?.NavigateInShell(NavigationDestination.Debug);
            SyncOverlayFromFrame();
        }

        private async void SettingsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_searchState)
            {
                ExitSearchState();
            }

            await CloseDetailAsync();
            _navigator?.OpenSettings();
            SyncOverlayFromFrame();
        }

        private void ProfileMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_shell?.OpenProfileSettingsCommand != null &&
                _shell.OpenProfileSettingsCommand.CanExecute(null))
            {
                _shell.OpenProfileSettingsCommand.Execute(null);
                SyncOverlayFromFrame();
                return;
            }

            _navigator?.OpenSettings(true);
            SyncOverlayFromFrame();
        }
    }
}
