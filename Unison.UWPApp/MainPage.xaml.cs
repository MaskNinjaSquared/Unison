using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.UWPApp.Models;
using Unison.UWPApp.Services;
using Unison.UWPApp.UI.Views;

namespace Unison.UWPApp
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
            
            // Hook up events
            ChatDetailPart.BackRequested += ChatDetailPart_BackRequested;

            // System Back Button support
            SystemNavigationManager.GetForCurrentView().BackRequested += MainPage_BackRequested;
        }

        private void MainPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;

            if (DebugPart.Visibility == Visibility.Visible)
            {
                DebugPart_BackRequested(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (LayoutStates.CurrentState?.Name == "NarrowState" && ChatDetailPart.Visibility == Visibility.Visible)
            {
                ChatDetailPart_BackRequested(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (LayoutStates.CurrentState?.Name == "WideState" && ChatDetailPart.HasActiveChat)
            {
                ChatDetailPart_BackRequested(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ChatDetailPart_BackRequested(object sender, EventArgs e)
        {
            ChatListPart.ClearSelection();

            if (LayoutStates.CurrentState?.Name == "NarrowState")
            {
                Column0.Width = new GridLength(1, GridUnitType.Star);
                Column1.Width = new GridLength(0);
                ChatListPart.Visibility = Visibility.Visible;
                ChatDetailPart.Visibility = Visibility.Collapsed;
            }
            
            UpdateBackButtonVisibility();
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            await WhatsAppService.Instance.InitializeAsync();
            
            WhatsAppService.Instance.OnSessionInitialized += (s, ev) => 
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowConnectedPanel());
            };

            WhatsAppService.Instance.OnError += (s, ex) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => 
                {
                    Debug.WriteLine($"[MainPage] Error: {ex.Message}");
                });
            };

            if (await WhatsAppService.Instance.IsRegisteredAsync())
            {
                ShowConnectedPanel();
                await WhatsAppService.Instance.ConnectAsync();
            }
            else
            {
                ShowLoginPanel();
            }
        }

        private void ChatListPart_ChatSelected(object sender, ChatSelectedEventArgs e)
        {
            ChatDetailPart.SetActiveChat(e.SelectedChat);

            if (LayoutStates.CurrentState?.Name == "NarrowState")
            {
                if (e.SelectedChat != null)
                {
                    Column0.Width = new GridLength(0);
                    Column1.Width = new GridLength(1, GridUnitType.Star);
                    ChatListPart.Visibility = Visibility.Collapsed;
                    ChatDetailPart.Visibility = Visibility.Visible;
                }
            }
            UpdateBackButtonVisibility();
        }

        private void ChatListPart_MenuClicked(object sender, EventArgs e)
        {
            Debug.WriteLine($"[MainPage] ChatListPart_MenuClicked. Current IsPaneOpen: {RootSplitView.IsPaneOpen}");
            RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
            Debug.WriteLine($"[MainPage] New IsPaneOpen: {RootSplitView.IsPaneOpen}");
        }

        private void NavListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavListView.SelectedItem is ListViewItem item)
            {
                string tag = item.Tag.ToString();
                if (tag == "chats")
                {
                    DebugPart.Visibility = Visibility.Collapsed;
                    RootContentGrid.Visibility = Visibility.Visible;
                }
                else if (tag == "debug")
                {
                    DebugPart.Visibility = Visibility.Visible;
                    // RootContentGrid.Visibility = Visibility.Collapsed; // Don't collapse, DebugPart is on top
                }
                RootSplitView.IsPaneOpen = false;
                UpdateBackButtonVisibility();
            }
        }

        private void DebugPart_BackRequested(object sender, EventArgs e)
        {
            DebugPart.Visibility = Visibility.Collapsed;
            NavListView.SelectedIndex = 0; // Back to chats
            UpdateBackButtonVisibility();
        }

        private void UpdateBackButtonVisibility()
        {
            bool showBack = false;
            if (DebugPart.Visibility == Visibility.Visible)
            {
                showBack = true;
            }
            else if (LayoutStates.CurrentState?.Name == "NarrowState")
            {
                showBack = ChatDetailPart.Visibility == Visibility.Visible;
            }
            else // Wide state
            {
                // In Wide state, we show the system back button if a chat is selected
                // (which allows clicking it to deselect/return to empty state)
                showBack = ChatDetailPart.HasActiveChat;
            }

            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = 
                showBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        public void ShowConnectedPanel()
        {
            MainOverlay.Visibility = Visibility.Collapsed;
            RootSplitView.Visibility = Visibility.Visible;
            UpdateBackButtonVisibility();
        }

        public void ShowLoginPanel()
        {
            MainOverlay.Visibility = Visibility.Visible;
            RootSplitView.Visibility = Visibility.Collapsed;
        }

        // Navigation Shims for legacy UI items that might be referenced
        private void BottomNavList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void ReloadQRButton_Click(object sender, RoutedEventArgs e) { }
        private void GenerateQRButton_Click(object sender, RoutedEventArgs e) { }

		private void LoginPart_Loaded(object sender, RoutedEventArgs e)
		{

		}

		private void LoginPart_Loaded_1(object sender, RoutedEventArgs e)
		{

		}
	}
}
