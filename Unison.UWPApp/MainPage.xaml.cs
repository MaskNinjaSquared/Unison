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
                Column0.Width = new GridLength(0);
                Column1.Width = new GridLength(1, GridUnitType.Star);
                ChatListPart.Visibility = Visibility.Collapsed;
                ChatDetailPart.Visibility = Visibility.Visible;
            }
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
            }
        }

        private void DebugPart_BackRequested(object sender, EventArgs e)
        {
            DebugPart.Visibility = Visibility.Collapsed;
            NavListView.SelectedIndex = 0; // Back to chats
        }

        public void ShowConnectedPanel()
        {
            MainOverlay.Visibility = Visibility.Collapsed;
            RootSplitView.Visibility = Visibility.Visible;
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
