using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy chats list page. Detail opens on <see cref="ShellView"/>'s detail host.
    /// </summary>
    public sealed partial class ChatsView : Page, UI.Views.IConversationShellPage
    {
        private ShellViewModel _shell;
        private bool _hooked;
        private bool _openingDeepLink;

        public event EventHandler MenuClicked;

        /// <summary>Shell opens the shared detail host for this chat.</summary>
        public event EventHandler<ChatSelectedEventArgs> ChatOpenRequested;

        public ChatListViewModel ChatListViewModel => ChatListPart?.ViewModel;

        public ChatListView ListChrome => ChatListPart;

        public bool HasActiveChat => _shell?.HasActiveChat == true;

        public ChatsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Loaded += ChatsView_Loaded;
            Unloaded += ChatsView_Unloaded;
        }

        public Task InitAsync(ShellViewModel shell)
        {
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            if (!_hooked)
            {
                ChatListPart.ConfigureScope(ChatListScope.Active);
                if (_shell != null)
                {
                    _shell.PropertyChanged += Shell_PropertyChanged;
                }

                _hooked = true;
            }

            return Task.CompletedTask;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = InitAsync(App.Services?.GetService<ShellViewModel>());
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            UnhookShell();
        }

        private void ChatsView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookShell();
        }

        private void UnhookShell()
        {
            if (_shell != null && _hooked)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _hooked = false;
            }
        }

        public void NotifyLocalConversationsCleared()
        {
            _ = NotifyLocalConversationsClearedAsync();
        }

        private async Task NotifyLocalConversationsClearedAsync()
        {
            ChatListPart?.ClearSelection();
            ChatOpenRequested?.Invoke(this, new ChatSelectedEventArgs(null));
            _shell?.ClearChat();
            await Task.CompletedTask;
        }

        private void ChatsView_Loaded(object sender, RoutedEventArgs e)
        {
            _ = TryOpenPendingDeepLinkAsync();
        }

        private void Shell_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.PendingOpenChatJid))
            {
                _ = TryOpenPendingDeepLinkAsync();
            }
        }

        public void RequestOpenPendingDeepLink()
        {
            _ = TryOpenPendingDeepLinkAsync();
        }

        private async Task TryOpenPendingDeepLinkAsync()
        {
            if (_shell == null || _openingDeepLink)
            {
                return;
            }

            string jid = _shell.PendingOpenChatJid;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _openingDeepLink = true;
            try
            {
                ChatItem chat = null;
                for (int i = 0; i < 50; i++)
                {
                    jid = _shell.PendingOpenChatJid;
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        return;
                    }

                    chat = ChatListPart?.FindChatByJid(jid);
                    if (chat != null)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }

                if (chat == null)
                {
                    Debug.WriteLine("[LegacyChats] Deep-link chat not found yet: " + jid);
                    return;
                }

                _shell.ClearPendingOpenChatJid();
                ChatListPart?.HighlightChatQuiet(chat);
                ChatOpenRequested?.Invoke(this, new ChatSelectedEventArgs(chat));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LegacyChats] Deep-link open failed: " + ex);
            }
            finally
            {
                _openingDeepLink = false;
            }
        }

        private void ChatListPart_ChatSelected(object sender, ChatSelectedEventArgs e)
        {
            if (e?.SelectedChat == null)
            {
                return;
            }

            ChatOpenRequested?.Invoke(this, e);
        }

        private void ChatListPart_MenuClicked(object sender, EventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        public bool TryHandleBack()
        {
            if (_shell == null || !_shell.HasActiveChat)
            {
                return false;
            }

            ChatListPart?.ClearSelection();
            ChatOpenRequested?.Invoke(this, new ChatSelectedEventArgs(null));
            _shell.ClearChat();
            return true;
        }

        public Task ResetForLoggedOutAsync()
        {
            ChatListPart?.ClearSelection();
            return Task.CompletedTask;
        }
    }
}
