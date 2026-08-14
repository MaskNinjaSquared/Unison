using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.Helpers;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Baileys.Protocol;
using Proto;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Views
{
    public class ChatSelectedEventArgs : EventArgs
    {
        public ChatItem SelectedChat { get; }
        public ChatSelectedEventArgs(ChatItem chat) => SelectedChat = chat;
    }

    public sealed partial class ChatListView : UserControl
    {
        private IWhatsAppService WhatsApp => App.GetWhatsAppService();

        public ObservableCollection<ChatItem> Chats => WhatsApp.Chats;
        public ObservableCollection<ChatItem> VisibleChats { get; } = new ObservableCollection<ChatItem>();
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;
        public event EventHandler MenuClicked;

        /// <summary>
        /// DI ViewModel. List still renders via code-behind VisibleChats (InitialSyncSafeMode /
        /// incremental batches). Call ViewModel.Attach() only when ItemsSource migrates fully.
        /// </summary>
        public ChatListViewModel ViewModel { get; private set; }

        private bool _subscriptionsAttached;
        private bool _isRefreshingVisibleChats;
        private bool _suppressSelectionChanged;
        private string _lastSelectedChatJid;
        private readonly DispatcherTimer _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        private readonly DispatcherTimer _initialSyncRenderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        private bool _initialSyncRendering;
        private bool _initialSyncCompleted;
        private int _initialSyncProcessed;
        private int _initialSyncTotal;
        private int _initialSyncVisibleTarget;
        private const int InitialSyncUiBatchSize = 20;

        public ChatListView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<ChatListViewModel>();
                DataContext = ViewModel;
            }

            this.InitializeComponent();
            this.Loaded += ChatListView_Loaded;
            this.Unloaded += ChatListView_Unloaded;
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
            _initialSyncRenderTimer.Tick += InitialSyncRenderTimer_Tick;
        }

        private void ChatListView_Loaded(object sender, RoutedEventArgs e)
        {
            // Always re-hook shell menu: navigating to Settings unloads this control and
            // drops MenuRequested; a subsequent Loaded must restore it or "…" does nothing.
            if (ViewModel != null)
            {
                ViewModel.MenuRequested -= ViewModel_MenuRequested;
                ViewModel.MenuRequested += ViewModel_MenuRequested;
                ViewModel.OpenChatRequested -= ViewModel_OpenChatRequested;
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
            }

            if (_subscriptionsAttached)
            {
                return;
            }
            _subscriptionsAttached = true;

            if (ViewModel != null)
            {
                ViewModel.BeforeLocalConversationsCleared += ViewModel_BeforeLocalConversationsCleared;
                ViewModel.AfterMenuActionCompleted += ViewModel_AfterMenuActionCompleted;
            }

            var service = WhatsApp;
            service.OnConnectionUpdate += Service_OnConnectionUpdate;
            service.OnHistorySyncReceived += Service_OnHistorySyncReceived;
            service.Chats.CollectionChanged += Chats_CollectionChanged;
            service.OnSyncStatus += Service_OnSyncStatus;
            service.OnDisplayNamesUpdated += Service_OnDisplayNamesUpdated;
            service.OnInitialSyncProgress += Service_OnInitialSyncProgress;

            foreach (var chat in service.Chats)
            {
                if (chat == null) continue;
                chat.PropertyChanged -= ChatItem_PropertyChanged;
                chat.PropertyChanged += ChatItem_PropertyChanged;
            }

            if (service.IsInitialSyncSafeMode)
            {
                BeginInitialSyncRendering(
                    service.InitialSyncProcessedConversations,
                    service.InitialSyncTotalConversations);
            }

            ChatLoadingOverlay.Visibility = service.Chats.Count > 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateSyncStatus(service.CurrentConnectionStatus);
            if (!_initialSyncRendering)
            {
                RefreshVisibleChats();
            }
        }

        private void ChatListView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscriptionsAttached)
            {
                return;
            }

            if (ViewModel != null)
            {
                ViewModel.BeforeLocalConversationsCleared -= ViewModel_BeforeLocalConversationsCleared;
                ViewModel.AfterMenuActionCompleted -= ViewModel_AfterMenuActionCompleted;
                ViewModel.MenuRequested -= ViewModel_MenuRequested;
                ViewModel.OpenChatRequested -= ViewModel_OpenChatRequested;
            }

            _searchDebounceTimer.Stop();
            _initialSyncRenderTimer.Stop();
            var service = WhatsApp;
            service.OnConnectionUpdate -= Service_OnConnectionUpdate;
            service.OnHistorySyncReceived -= Service_OnHistorySyncReceived;
            service.Chats.CollectionChanged -= Chats_CollectionChanged;
            service.OnSyncStatus -= Service_OnSyncStatus;
            service.OnDisplayNamesUpdated -= Service_OnDisplayNamesUpdated;
            service.OnInitialSyncProgress -= Service_OnInitialSyncProgress;
            foreach (var chat in service.Chats)
            {
                if (chat != null) chat.PropertyChanged -= ChatItem_PropertyChanged;
            }
            _subscriptionsAttached = false;
        }

        private void Service_OnConnectionUpdate(object sender, string status)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateSyncStatus(status));
        }

        private void Service_OnHistorySyncReceived(object sender, Proto.HistorySync sync)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_initialSyncRendering)
                {
                    _initialSyncCompleted = true;
                    _initialSyncVisibleTarget = Math.Max(_initialSyncVisibleTarget, Chats.Count);
                    EnsureInitialSyncRenderTimer();
                    return;
                }

                _ = PresentSyncAsync(null, visible: false);
                ChatLoadingOverlay.Visibility = Visibility.Collapsed;
                RefreshVisibleChats();
            });
        }

        private void Service_OnInitialSyncProgress(object sender, InitialSyncProgressEventArgs e)
        {
            if (e == null) return;

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!e.IsCompleted)
                {
                    BeginInitialSyncRendering(e.ProcessedConversations, e.TotalConversations);
                }
                else
                {
                    _initialSyncRendering = true;
                    _initialSyncCompleted = true;
                    _initialSyncProcessed = Math.Max(_initialSyncProcessed, e.ProcessedConversations);
                    _initialSyncTotal = Math.Max(_initialSyncTotal, e.TotalConversations);
                    _initialSyncVisibleTarget = Math.Max(_initialSyncVisibleTarget, Chats.Count);
                    UpdateInitialSyncText();
                    EnsureInitialSyncRenderTimer();
                }
            });
        }

        private void Service_OnSyncStatus(object sender, string statusMessage)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_initialSyncRendering)
                {
                    UpdateInitialSyncText();
                    return;
                }

                if (ViewModel != null && ViewModel.IsConversationResyncInProgress)
                {
                    if (string.IsNullOrEmpty(statusMessage))
                    {
                        return;
                    }

                    // Prefer localized preparing text over raw service strings during resync.
                    if (string.Equals(statusMessage, "Saving chats...", StringComparison.Ordinal))
                    {
                        return;
                    }

                    ChatLoadingOverlay.Visibility = Visibility.Visible;
                    ChatLoadingText.Text = statusMessage;
                    _ = PresentSyncAsync(statusMessage, visible: true);
                    return;
                }

                if (!string.IsNullOrEmpty(statusMessage))
                    _ = PresentSyncAsync(statusMessage, visible: true);
                else
                    _ = PresentSyncAsync(null, visible: false);
            });
        }

        private void Service_OnDisplayNamesUpdated(object sender, EventArgs e)
        {
            if (_initialSyncRendering || WhatsApp.IsInitialSyncSafeMode)
            {
                return;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => ScheduleVisibleChatsRefresh());
        }

        private void Chats_CollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            // Persisted rows are inserted as one startup batch. Queuing a dispatcher
            // callback and list mutation for every row produced hundreds of pending UI
            // operations on low-memory phones. Attach item notifications now and let
            // the single OnHistorySyncReceived event rebuild VisibleChats once.
            if (WhatsApp.IsLoadingPersistedChats)
            {
                if (args?.NewItems != null)
                {
                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null) continue;
                        newItem.PropertyChanged -= ChatItem_PropertyChanged;
                        newItem.PropertyChanged += ChatItem_PropertyChanged;
                    }
                }
                return;
            }

            if (_initialSyncRendering || WhatsApp.IsInitialSyncSafeMode)
            {
                if (args?.OldItems != null)
                {
                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem != null) oldItem.PropertyChanged -= ChatItem_PropertyChanged;
                    }
                }
                if (args?.NewItems != null)
                {
                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null) continue;
                        newItem.PropertyChanged -= ChatItem_PropertyChanged;
                        newItem.PropertyChanged += ChatItem_PropertyChanged;
                    }
                }

                _initialSyncRendering = true;
                _initialSyncVisibleTarget = Math.Max(_initialSyncVisibleTarget, Chats.Count);
                EnsureInitialSyncRenderTimer();
                return;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (WhatsApp.Chats.Count > 0)
                {
                    ChatLoadingOverlay.Visibility = Visibility.Collapsed;
                }

                if (args?.OldItems != null)
                {
                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem != null) oldItem.PropertyChanged -= ChatItem_PropertyChanged;
                    }
                }
                if (args?.NewItems != null)
                {
                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null) continue;
                        newItem.PropertyChanged -= ChatItem_PropertyChanged;
                        newItem.PropertyChanged += ChatItem_PropertyChanged;
                    }
                }

                string selectedJid = (ChatList.SelectedItem as ChatItem)?.JID ?? _lastSelectedChatJid;
                bool incrementalApplied;

                _suppressSelectionChanged = true;
                try
                {
                    incrementalApplied = TryApplyIncrementalCollectionChange(args);
                    if (incrementalApplied && !string.IsNullOrWhiteSpace(selectedJid))
                    {
                        RestoreSelectionByJid(selectedJid);
                    }
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }

                if (!incrementalApplied)
                {
                    ScheduleVisibleChatsRefresh();
                }
                else
                {
                    // After Move/Remove restore, rebind detail to the live list instance.
                    var selected = ChatList.SelectedItem as ChatItem;
                    if (selected != null)
                    {
                        ChatSelected?.Invoke(this, new ChatSelectedEventArgs(selected));
                    }
                    else if (!string.IsNullOrWhiteSpace(selectedJid))
                    {
                        RestoreSelectionByJid(selectedJid);
                        selected = ChatList.SelectedItem as ChatItem;
                        if (selected != null)
                        {
                            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(selected));
                        }
                    }
                }
            });
        }

        private void UpdateSyncStatus(string status)
        {
            if (_initialSyncRendering)
            {
                UpdateInitialSyncText();
                return;
            }

            switch (status)
            {
                case "connecting":
                    _ = PresentSyncAsync(LocalizedStrings.Get("ChatList_Connecting"), visible: true);
                    break;
                case "connected":
                    _ = PresentSyncAsync(LocalizedStrings.Get("ChatList_Handshake"), visible: true);
                    break;
                case "open":
                    _ = PresentSyncAsync(LocalizedStrings.Get("ChatList_Updating"), visible: true);
                    break;
                case "close":
                case "synced":
                    _ = PresentSyncAsync(null, visible: false);
                    break;
            }
        }

        /// <summary>
        /// Desktop: header chip. Mobile (<see cref="ChatListViewModel.DisplaySync"/> false): status bar "...".
        /// </summary>
        private Task PresentSyncAsync(string message, bool visible)
        {
            if (ViewModel != null)
            {
                return ViewModel.PresentSyncStatusAsync(message, visible);
            }

            // Fallback before DI is ready: always use in-header panel.
            if (visible && !string.IsNullOrEmpty(message))
            {
                SyncStatusPanel.Visibility = Visibility.Visible;
                SyncStatusText.Text = message;
            }
            else
            {
                SyncStatusPanel.Visibility = Visibility.Collapsed;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// UWP TextBlock does not clip overflow into adjacent Grid columns; long sync strings
        /// painted over header icons. Clip to arranged bounds + CharacterEllipsis.
        /// Clear Clip when collapsed so a stale zero-rect does not stick across refresh.
        /// </summary>
        private void SyncStatusPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (SyncStatusPanel == null)
            {
                return;
            }

            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0 ||
                SyncStatusPanel.Visibility != Visibility.Visible)
            {
                SyncStatusPanel.Clip = null;
                return;
            }

            SyncStatusPanel.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }

        private void BeginInitialSyncRendering(int processed, int total)
        {
            _initialSyncRendering = true;
            _initialSyncCompleted = false;
            _initialSyncProcessed = Math.Max(_initialSyncProcessed, processed);
            _initialSyncTotal = Math.Max(_initialSyncTotal, total);
            _initialSyncVisibleTarget = Math.Max(
                _initialSyncVisibleTarget,
                Math.Min(Chats.Count, Math.Max(InitialSyncUiBatchSize, processed)));

            ChatLoadingOverlay.Visibility = VisibleChats.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateInitialSyncText();
            EnsureInitialSyncRenderTimer();
        }

        private void UpdateInitialSyncText()
        {
            if (_initialSyncCompleted)
            {
                string done = LocalizedStrings.Get("ChatList_SyncDone");
                _ = PresentSyncAsync(done, visible: true);
                ChatLoadingText.Text = LocalizedStrings.Get("ChatList_Organizing");
                return;
            }

            if (_initialSyncTotal > 0)
            {
                string text = LocalizedStrings.Format("ChatList_SyncProgress", _initialSyncProcessed, _initialSyncTotal);
                _ = PresentSyncAsync(text, visible: true);
                ChatLoadingText.Text = text;
            }
            else
            {
                string text = LocalizedStrings.Format("ChatList_SyncProgressCount", _initialSyncProcessed);
                _ = PresentSyncAsync(text, visible: true);
                ChatLoadingText.Text = text;
            }
        }

        private void EnsureInitialSyncRenderTimer()
        {
            if (!_initialSyncRenderTimer.IsEnabled)
            {
                _initialSyncRenderTimer.Start();
            }
        }

        private void InitialSyncRenderTimer_Tick(object sender, object e)
        {
            if (!_initialSyncRendering)
            {
                _initialSyncRenderTimer.Stop();
                return;
            }

            var service = WhatsApp;
            var source = Chats
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                .OrderByDescending(c => c.IsChatPinned)
                .ThenByDescending(c => c.PinnedTimestamp ?? 0)
                .ThenByDescending(c => c.LastMessageTimestampUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            int desiredCount = _initialSyncCompleted
                ? source.Count
                : Math.Min(source.Count, Math.Max(_initialSyncVisibleTarget, InitialSyncUiBatchSize));
            int added = 0;
            var visibleCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var visible in VisibleChats)
            {
                if (visible == null) continue;
                visibleCanonical.Add(service.GetCanonicalJid(visible.JID) ?? visible.JID);
            }

            foreach (var chat in source)
            {
                if (VisibleChats.Count >= desiredCount || added >= InitialSyncUiBatchSize)
                {
                    break;
                }

                string canonical = service.GetCanonicalJid(chat.JID) ?? chat.JID;
                if (!visibleCanonical.Add(canonical))
                {
                    continue;
                }

                try
                {
                    App.Services?.GetService<IChatStore>()?.ApplyTo(chat);
                }
                catch
                {
                }

                VisibleChats.Add(chat);
                added++;
            }

            if (VisibleChats.Count > 0)
            {
                ChatLoadingOverlay.Visibility = Visibility.Collapsed;
            }

            if (_initialSyncCompleted)
            {
                int uniqueSourceCount = source
                    .Select(c => service.GetCanonicalJid(c.JID) ?? c.JID)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (VisibleChats.Count >= uniqueSourceCount)
                {
                    _initialSyncRenderTimer.Stop();
                    _initialSyncRendering = false;
                    _initialSyncCompleted = false;
                    RefreshVisibleChats();
                    _ = PresentSyncAsync(null, visible: false);
                    ChatLoadingOverlay.Visibility = Visibility.Collapsed;
                    RuntimeDiagnosticsService.Instance.Write(
                        "history",
                        "initial-sync-ui-batches-complete",
                        "visible=" + VisibleChats.Count + "; source=" + source.Count);
                }
            }
        }

        private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ObservableCollection.Move can make ListView report a temporary null
            // selection while the selected row is moved to the top. That transient
            // event used to be propagated as a real deselection, causing MainView to
            // call SetActiveChatAsync(null) immediately after sending a message.
            if (_suppressSelectionChanged || _isRefreshingVisibleChats)
            {
                return;
            }

            var chat = ChatList.SelectedItem as ChatItem;
            if (chat == null)
            {
                // Never notify a null selection — list rebuilds / Move / dedupe must not
                // close the open chat. Re-select the last known conversation when possible.
                if (!string.IsNullOrWhiteSpace(_lastSelectedChatJid))
                {
                    RestoreSelectionByJid(_lastSelectedChatJid);
                }

                return;
            }

            _lastSelectedChatJid = chat.JID;

            // Same conversation, possibly a new ChatItem instance after dedupe — still notify
            // so ChatsView can rebind PendingChat without tearing down the detail.
            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
        }

        /// <summary>Pin chat as Start secondary tile (ContextFlyout from chat row).</summary>
        internal void PinChatToStart(ChatItem chat)
        {
            if (chat == null || ViewModel?.PinChatToStartCommand == null)
            {
                return;
            }

            if (ViewModel.PinChatToStartCommand.CanExecute(chat))
            {
                ViewModel.PinChatToStartCommand.Execute(chat);
            }
        }

        internal void ToggleLocalMute(ChatItem chat)
        {
            SetLocalMute(chat, chat != null && chat.IsMutedLocally
                ? (long?)null
                : ChatMuteHelper.ForeverUnixSeconds);
        }

        internal void SetLocalMute(ChatItem chat, long? mutedUntilUnixSeconds)
        {
            if (chat == null || ViewModel?.SetLocalMuteCommand == null)
            {
                return;
            }

            var request = mutedUntilUnixSeconds.HasValue
                ? ChatMuteRequest.Mute(chat, mutedUntilUnixSeconds.Value)
                : ChatMuteRequest.Unmute(chat);

            if (ViewModel.SetLocalMuteCommand.CanExecute(request))
            {
                ViewModel.SetLocalMuteCommand.Execute(request);
            }
        }

        /// <summary>Selects a chat in the list without raising <see cref="ChatSelected"/> (caller opens detail).</summary>
        internal void HighlightChatQuiet(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            bool previousSuppression = _suppressSelectionChanged;
            _suppressSelectionChanged = true;
            try
            {
                if (!VisibleChats.Contains(chat))
                {
                    // Ensure the row is visible even when a filter is active.
                    VisibleChats.Insert(0, chat);
                }

                ChatList.SelectedItem = chat;
                _lastSelectedChatJid = chat.JID;
            }
            finally
            {
                _suppressSelectionChanged = previousSuppression;
            }
        }

        /// <summary>Finds a chat by JID / canonical id in the full source collection.</summary>
        internal ChatItem FindChatByJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            var service = WhatsApp;
            string canonical = service.GetCanonicalJid(jid);
            return Chats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(service.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        public void ClearSelection()
        {
            // Back navigation already clears ChatDetailView explicitly. Suppress the
            // ListView event here so this intentional clear cannot race with a pending
            // collection move/refresh.
            _suppressSelectionChanged = true;
            try
            {
                ChatList.SelectedItem = null;
                _lastSelectedChatJid = null;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        private void ViewModel_MenuRequested(object sender, EventArgs e)
        {
            Debug.WriteLine("[ChatListView] MenuRequested → MenuClicked");
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Direct path so shell hamburger still opens if Command/MenuRequested was unhooked
            // after Settings navigation (UWP Unloaded/Loaded races).
            Debug.WriteLine("[ChatListView] MenuButton_Click");
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private async void ViewModel_OpenChatRequested(object sender, string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            // StartNewChat may populate asynchronously; give the source one short chance.
            ChatItem chat = null;
            for (int i = 0; i < 5 && chat == null; i++)
            {
                chat = Chats.FirstOrDefault(c => c.JID == jid);
                if (chat == null)
                {
                    await Task.Delay(50);
                }
            }

            if (chat != null)
            {
                ChatList.SelectedItem = chat;
            }
        }

        private void ViewModel_BeforeLocalConversationsCleared(object sender, EventArgs e)
        {
            _suppressSelectionChanged = true;
            try
            {
                ChatList.SelectedItem = null;
                _lastSelectedChatJid = null;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            VisibleChats.Clear();
            ChatLoadingOverlay.Visibility = Visibility.Visible;
            ChatLoadingText.Text = LocalizedStrings.Get(
                "ChatList_ResyncCleaningHistory",
                "Cleaning history...");

            // Leave NarrowDetail empty-state; restore list pane during wipe/resync.
            try
            {
                FindAncestorChatsView()?.NotifyLocalConversationsCleared();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatListView] NotifyLocalConversationsCleared: " + ex.Message);
            }
        }

        private ChatsView FindAncestorChatsView()
        {
            DependencyObject current = this;
            while (current != null)
            {
                var match = current as ChatsView;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return Window.Current?.Content != null
                ? FindInSubtree<ChatsView>(Window.Current.Content as DependencyObject)
                : null;
        }

        private static T FindInSubtree<T>(DependencyObject root) where T : class
        {
            if (root == null)
            {
                return null;
            }

            var match = root as T;
            if (match != null)
            {
                return match;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindInSubtree<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void ViewModel_AfterMenuActionCompleted(object sender, EventArgs e)
        {
            if (_initialSyncRendering || WhatsApp.IsInitialSyncSafeMode)
            {
                UpdateInitialSyncText();
                return;
            }

            RefreshVisibleChats();

            if (VisibleChats.Count == 0)
            {
                ChatLoadingOverlay.Visibility = Visibility.Visible;
                ChatLoadingText.Text = LocalizedStrings.Get(
                    "ChatList_ResyncingConversations",
                    "Re-syncing conversations...");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleVisibleChatsRefresh();
        }

        private void ScheduleVisibleChatsRefresh()
        {
            if (_initialSyncRendering || WhatsApp.IsInitialSyncSafeMode)
            {
                return;
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounceTimer_Tick(object sender, object e)
        {
            _searchDebounceTimer.Stop();
            if (_initialSyncRendering || WhatsApp.IsInitialSyncSafeMode)
            {
                return;
            }

            RefreshVisibleChats();
        }

        private void ChatList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args == null || args.InRecycleQueue || args.Item == null)
            {
                return;
            }

            // Busca sob demanda somente para linhas que realmente entraram na tela.
            // Isso corrige os avatares que ficavam fora do primeiro lote de 12 sem
            // voltar a baixar fotos de todos os contatos ao mesmo tempo.
            var chat = args.Item as ChatItem;
            if (chat != null && !_initialSyncRendering && !WhatsApp.IsInitialSyncSafeMode)
            {
                WhatsApp.RequestAvatarRefresh(chat);
            }
        }

        private void ChatItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Item bindings update their visible row automatically. Rebuilding or moving
            // the entire list for every preview/timestamp change created a large dispatcher
            // backlog during history sync. A refresh is only needed when an active search
            // could change whether the row matches.
            if (!HasActiveSearchQuery())
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.IsChatPinned) ||
                e.PropertyName == nameof(ChatItem.PinnedTimestamp))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, RefreshVisibleChats);
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.LastMessage) ||
                e.PropertyName == nameof(ChatItem.Timestamp))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => ScheduleVisibleChatsRefresh());
            }
        }

        private bool TryApplyIncrementalCollectionChange(NotifyCollectionChangedEventArgs args)
        {
            if (args == null || !CanUseIncrementalVisibleChatUpdates())
            {
                return false;
            }

            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (args.NewItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null || VisibleChats.Contains(newItem))
                        {
                            continue;
                        }

                        int targetIndex = Chats.IndexOf(newItem);
                        if (targetIndex < 0)
                        {
                            return false;
                        }

                        VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), newItem);
                    }
                    return true;

                case NotifyCollectionChangedAction.Remove:
                    if (args.OldItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem == null)
                        {
                            continue;
                        }

                        int existingIndex = VisibleChats.IndexOf(oldItem);
                        if (existingIndex >= 0)
                        {
                            VisibleChats.RemoveAt(existingIndex);
                        }
                    }
                    return true;

                case NotifyCollectionChangedAction.Move:
                    if (args.NewItems == null || args.NewItems.Count != 1)
                    {
                        return false;
                    }

                    var movedItem = args.NewItems[0] as ChatItem;
                    if (movedItem == null)
                    {
                        return false;
                    }

                    return MoveVisibleChatToMatchSource(movedItem);

                case NotifyCollectionChangedAction.Replace:
                    if (args.OldItems == null || args.NewItems == null)
                    {
                        return false;
                    }

                    foreach (ChatItem oldItem in args.OldItems)
                    {
                        if (oldItem == null)
                        {
                            continue;
                        }

                        int existingIndex = VisibleChats.IndexOf(oldItem);
                        if (existingIndex >= 0)
                        {
                            VisibleChats.RemoveAt(existingIndex);
                        }
                    }

                    foreach (ChatItem newItem in args.NewItems)
                    {
                        if (newItem == null || VisibleChats.Contains(newItem))
                        {
                            continue;
                        }

                        int targetIndex = Chats.IndexOf(newItem);
                        if (targetIndex < 0)
                        {
                            return false;
                        }

                        VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), newItem);
                    }
                    return true;

                default:
                    return false;
            }
        }

        private bool MoveVisibleChatToMatchSource(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            int sourceIndex = Chats.IndexOf(chat);
            int visibleIndex = VisibleChats.IndexOf(chat);
            if (sourceIndex < 0 || visibleIndex < 0)
            {
                return false;
            }

            int targetIndex = Math.Min(sourceIndex, VisibleChats.Count - 1);
            if (targetIndex < 0)
            {
                return false;
            }

            if (visibleIndex != targetIndex)
            {
                VisibleChats.Move(visibleIndex, targetIndex);
            }

            return true;
        }

        private bool HasActiveSearchQuery()
        {
            return !string.IsNullOrWhiteSpace(SearchBox?.Text?.Trim());
        }

        private bool CanUseIncrementalVisibleChatUpdates()
        {
            return !HasActiveSearchQuery();
        }

        private void RefreshVisibleChats()
        {
            var service = WhatsApp;
            if (_initialSyncRendering || service.IsInitialSyncSafeMode)
            {
                return;
            }
            string selectedJid = (ChatList.SelectedItem as ChatItem)?.JID ?? _lastSelectedChatJid;
            string query = SearchBox?.Text?.Trim() ?? string.Empty;
            var source = Chats.ToList();
            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (service.ResolveDisplayName(c.JID, "search").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.LastMessage) && c.LastMessage.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.JID) && c.JID.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }

            // UI-level guard: if canonical duplicates still exist transiently, show only one row.
            var deduped = new List<ChatItem>();
            var canonicalIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.JID)) continue;
                string canonical = service.GetCanonicalJid(item.JID);
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    canonical = item.JID;
                }

                if (!canonicalIndex.TryGetValue(canonical, out var existingIndex))
                {
                    canonicalIndex[canonical] = deduped.Count;
                    deduped.Add(item);
                    continue;
                }

                var existing = deduped[existingIndex];

                // PN/LID duplicates can receive app-state updates on different rows.
                // Preserve pin state regardless of which row has the newest preview.
                if (item.IsChatPinned && !existing.IsChatPinned)
                {
                    existing.IsChatPinned = true;
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }
                else if (item.IsChatPinned && existing.IsChatPinned &&
                         (item.PinnedTimestamp ?? 0) > (existing.PinnedTimestamp ?? 0))
                {
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }

                DateTime existingPreviewUtc = existing.LastMessageTimestampUtc ?? DateTime.MinValue;
                DateTime itemPreviewUtc = item.LastMessageTimestampUtc ?? DateTime.MinValue;
                bool itemHasNewerPreview = itemPreviewUtc > existingPreviewUtc;
                bool samePreviewButBetterAvatar = itemPreviewUtc == existingPreviewUtc &&
                    string.IsNullOrWhiteSpace(existing.AvatarUrl) &&
                    !string.IsNullOrWhiteSpace(item.AvatarUrl);
                bool samePreviewAndAvatarButBetterName = itemPreviewUtc == existingPreviewUtc &&
                    string.Equals(existing.AvatarUrl, item.AvatarUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(existing.Name) &&
                    !string.IsNullOrWhiteSpace(item.Name);

                if (itemHasNewerPreview || samePreviewButBetterAvatar || samePreviewAndAvatarButBetterName)
                {
                    if (existing.IsChatPinned && !item.IsChatPinned)
                    {
                        item.IsChatPinned = true;
                        item.PinnedTimestamp = existing.PinnedTimestamp;
                    }
                    deduped[existingIndex] = item;
                }
            }

            source = deduped
                .OrderByDescending(c => c.IsChatPinned)
                .ThenByDescending(c => c.PinnedTimestamp ?? 0)
                .ThenByDescending(c => c.LastMessageTimestampUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _isRefreshingVisibleChats = true;
            _suppressSelectionChanged = true;
            try
            {
                for (int i = 0; i < source.Count; i++)
                {
                    var item = source[i];
                    if (i < VisibleChats.Count && ReferenceEquals(VisibleChats[i], item))
                    {
                        continue;
                    }

                    int existingIndex = VisibleChats.IndexOf(item);
                    if (existingIndex >= 0)
                    {
                        VisibleChats.Move(existingIndex, i);
                    }
                    else
                    {
                        VisibleChats.Insert(i, item);
                    }
                }

                while (VisibleChats.Count > source.Count)
                {
                    VisibleChats.RemoveAt(VisibleChats.Count - 1);
                }

                if (!string.IsNullOrWhiteSpace(selectedJid))
                {
                    string selectedCanonical = service.GetCanonicalJid(selectedJid);
                    var selected = VisibleChats.FirstOrDefault(c =>
                        string.Equals(c.JID, selectedJid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(service.GetCanonicalJid(c.JID), selectedCanonical, StringComparison.OrdinalIgnoreCase));
                    if (selected != null)
                    {
                        ChatList.SelectedItem = selected;
                        _lastSelectedChatJid = selected.JID;
                    }
                    else
                    {
                        // Keep the remembered JID so a later CollectionChanged can restore
                        // (e.g. temporary remove during PN/LID merge).
                        _lastSelectedChatJid = selectedJid;
                    }
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
                _isRefreshingVisibleChats = false;
            }

            var restored = ChatList.SelectedItem as ChatItem;
            if (restored != null)
            {
                ChatSelected?.Invoke(this, new ChatSelectedEventArgs(restored));
            }
            else if (!string.IsNullOrWhiteSpace(_lastSelectedChatJid))
            {
                RestoreSelectionByJid(_lastSelectedChatJid);
                restored = ChatList.SelectedItem as ChatItem;
                if (restored != null)
                {
                    ChatSelected?.Invoke(this, new ChatSelectedEventArgs(restored));
                }
            }
        }

        private void RestoreSelectionByJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || ChatList == null)
            {
                return;
            }

            var service = WhatsApp;
            string canonical = service.GetCanonicalJid(jid);
            var selected = VisibleChats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(service.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));

            if (selected == null || ReferenceEquals(ChatList.SelectedItem, selected))
            {
                return;
            }

            bool previousSuppression = _suppressSelectionChanged;
            _suppressSelectionChanged = true;
            try
            {
                ChatList.SelectedItem = selected;
                _lastSelectedChatJid = selected.JID;
            }
            finally
            {
                _suppressSelectionChanged = previousSuppression;
            }
        }

        public static string GetChatDisplayName(string name, ChatKind kind)
        {
            var item = new ChatItem { Name = name, Kind = kind };
            IStringResources strings = null;
            try
            {
                strings = App.Services?.GetService<IStringResources>();
            }
            catch
            {
            }

            return item.GetNameResolved(strings);
        }
    }
}
