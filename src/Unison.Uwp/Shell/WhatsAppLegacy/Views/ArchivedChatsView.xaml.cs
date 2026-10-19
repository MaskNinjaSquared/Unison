using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>Legacy archived list — detail opens on ShellView detail host.</summary>
    public sealed partial class ArchivedChatsView : Page, UI.Views.IConversationShellPage
    {
        private ShellViewModel _shell;
        private bool _hooked;

        public event EventHandler MenuClicked;

        public event EventHandler<ChatSelectedEventArgs> ChatOpenRequested;

        public ChatListViewModel ChatListViewModel => ChatListPart?.ViewModel;

        public ChatListView ListChrome => ChatListPart;

        public bool HasActiveChat => _shell?.HasActiveChat == true;

        public ArchivedChatsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        public Task InitAsync(ShellViewModel shell)
        {
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            if (!_hooked)
            {
                ChatListPart.ConfigureScope(ChatListScope.Archived);
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
            _hooked = false;
        }

        public void NotifyLocalConversationsCleared()
        {
            ChatListPart?.ClearSelection();
            ChatOpenRequested?.Invoke(this, new ChatSelectedEventArgs(null));
            _shell?.ClearChat();
        }

        public void RequestOpenPendingDeepLink()
        {
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
