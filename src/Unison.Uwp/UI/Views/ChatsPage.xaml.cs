using System;
using System.ComponentModel;
using System.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell content: chat list + detail (master-detail VisualStates).</summary>
    public sealed partial class ChatsPage : Page
    {
        private ShellViewModel _shell;
        private bool _hooked;

        public event EventHandler MenuClicked;

        public ChatsPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        public bool HasActiveChat => ChatDetailPart?.HasActiveChat == true;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _shell = App.Services?.GetService<ShellViewModel>();
            if (_shell != null && !_hooked)
            {
                _shell.PropertyChanged += Shell_PropertyChanged;
                ChatDetailPart.BackRequested += ChatDetailPart_BackRequested;
                _hooked = true;
            }

            ApplyChatPaneState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Keep cache/hooks — Required cache; only unhook on Unloaded if discarded.
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ChatPane))
            {
                ApplyChatPaneState();
            }
        }

        private void ApplyChatPaneState()
        {
            if (_shell == null)
            {
                return;
            }

            VisualStateManager.GoToState(this, _shell.ChatPane, false);
        }

        private async void ChatDetailPart_BackRequested(object sender, EventArgs e)
        {
            ChatListPart.ClearSelection();
            try
            {
                await ChatDetailPart.SetActiveChatAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsPage] Failed to clear chat: " + ex);
            }

            _shell?.ClearChat();
        }

        private async void ChatListPart_ChatSelected(object sender, ChatSelectedEventArgs e)
        {
            _shell?.SelectChat(e.SelectedChat);
            try
            {
                await ChatDetailPart.SetActiveChatAsync(e.SelectedChat);
                _shell?.ReportActiveChat(e.SelectedChat != null && ChatDetailPart.HasActiveChat);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsPage] Failed to open chat: " + ex);
            }
        }

        private void ChatListPart_MenuClicked(object sender, EventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        public bool TryHandleBack()
        {
            if (_shell != null &&
                ((_shell.IsNarrowWindow && _shell.ChatPane == ShellViewModel.PaneNarrowDetail) ||
                 (!_shell.IsNarrowWindow && _shell.HasActiveChat)))
            {
                ChatDetailPart_BackRequested(this, EventArgs.Empty);
                return true;
            }

            return false;
        }
    }
}
